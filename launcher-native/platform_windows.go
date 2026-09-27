//go:build windows

package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

func steamRoots() ([]string, error) {
	var roots []string
	for _, source := range []struct {
		hive   syscall.Handle
		access uint32
		value  string
	}{
		{syscall.HKEY_CURRENT_USER, syscall.KEY_READ, "SteamPath"},
		{syscall.HKEY_LOCAL_MACHINE, syscall.KEY_READ | syscall.KEY_WOW64_32KEY, "InstallPath"},
		{syscall.HKEY_LOCAL_MACHINE, syscall.KEY_READ | syscall.KEY_WOW64_64KEY, "InstallPath"},
	} {
		if path := steamRegistryPath(source.hive, source.access, source.value); path != "" {
			roots = append(roots, path)
		}
	}
	for _, variable := range []string{"ProgramFiles(x86)", "ProgramFiles"} {
		if path := os.Getenv(variable); path != "" {
			roots = append(roots, filepath.Join(path, "Steam"))
		}
	}
	return append(roots, `C:\Program Files (x86)\Steam`), nil
}

func steamRegistryPath(hive syscall.Handle, access uint32, value string) string {
	keyPath, err := syscall.UTF16PtrFromString(`Software\Valve\Steam`)
	if err != nil {
		return ""
	}
	var key syscall.Handle
	if err := syscall.RegOpenKeyEx(hive, keyPath, 0, access, &key); err == nil {
		defer syscall.RegCloseKey(key)
		name, err := syscall.UTF16PtrFromString(value)
		if err != nil {
			return ""
		}
		var kind, size uint32
		if syscall.RegQueryValueEx(key, name, nil, &kind, nil, &size) == nil && kind == syscall.REG_SZ && size >= 2 && size <= 65536 {
			buffer := make([]uint16, (size+1)/2)
			if syscall.RegQueryValueEx(key, name, nil, &kind, (*byte)(unsafe.Pointer(&buffer[0])), &size) == nil {
				if path := syscall.UTF16ToString(buffer); path != "" {
					return path
				}
			}
		}
	}
	return ""
}

func dataHome() (string, error) {
	base := os.Getenv("LOCALAPPDATA")
	if base == "" {
		return "", errors.New("Windows did not provide a local app data folder. Sign out and back in before starting the launcher")
	}
	return filepath.Join(base, "RootSixPlayer"), nil
}

func processIdentity(pid int) (string, error) {
	handle, err := syscall.OpenProcess(0x1000, false, uint32(pid))
	if errors.Is(err, syscall.Errno(87)) {
		return "", nil
	}
	if err != nil {
		return "", err
	}
	defer syscall.CloseHandle(handle)
	var code uint32
	if err := syscall.GetExitCodeProcess(handle, &code); err != nil {
		return "", err
	}
	if code != 259 {
		return "", nil
	}
	var created, exited, kernel, user syscall.Filetime
	if err := syscall.GetProcessTimes(handle, &created, &exited, &kernel, &user); err != nil {
		return "", err
	}
	return fmt.Sprintf("%d:%d", created.HighDateTime, created.LowDateTime), nil
}

func acquireLock(data string) (io.Closer, error) {
	if err := os.MkdirAll(data, 0700); err != nil {
		return nil, err
	}
	path := filepath.Join(data, "launcher.lock")
	name, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	// Denying all sharing also conflicts with Python's existing open/byte lock.
	handle, err := syscall.CreateFile(name, syscall.GENERIC_READ|syscall.GENERIC_WRITE, 0, nil,
		syscall.OPEN_ALWAYS, syscall.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		return nil, fmt.Errorf("the launcher data folder could not be locked. Close any other launcher using this folder and retry: %w", err)
	}
	return os.NewFile(uintptr(handle), path), nil
}

func availableDisk(path string) (uint64, error) {
	name, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return 0, err
	}
	var available, total, free uint64
	result, _, callErr := syscall.NewLazyDLL("kernel32.dll").NewProc("GetDiskFreeSpaceExW").Call(
		uintptr(unsafe.Pointer(name)), uintptr(unsafe.Pointer(&available)), uintptr(unsafe.Pointer(&total)), uintptr(unsafe.Pointer(&free)))
	if result == 0 {
		return 0, callErr
	}
	return available, nil
}

func checkLaunchBudget(headless bool) error {
	if headless {
		return errors.New("headless development launches require the constrained Linux Xvfb adapter; no game was started")
	}
	return nil
}

func platformGameCommand(inst Installation, lab string, args, env []string, headless bool) (*exec.Cmd, string, error) {
	if err := checkLaunchBudget(headless); err != nil {
		return nil, "", err
	}
	cmd := exec.Command(filepath.Join(lab, "game", "Root.exe"), args...)
	cmd.Dir, cmd.Env = filepath.Join(lab, "game"), env
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP | 0x08000000} // CREATE_NO_WINDOW affects console children only.
	return cmd, "", nil
}

func stopPlatformProcess(game *GameProcess) error {
	select {
	case <-game.done:
		return nil
	default:
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	stop := exec.CommandContext(ctx, "taskkill", "/PID", strconv.Itoa(game.cmd.Process.Pid), "/T", "/F")
	stop.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000, HideWindow: true}
	err := stop.Run()
	select {
	case <-game.done:
		return nil // A concurrently exiting game needs no further termination.
	case <-time.After(10 * time.Second):
		killErr := game.cmd.Process.Kill()
		if killErr != nil && !errors.Is(killErr, os.ErrProcessDone) {
			err = errors.Join(err, killErr)
		}
	}
	select {
	case <-game.done:
	case <-time.After(10 * time.Second):
		err = errors.Join(err, errors.New("Root did not exit after termination; close it in Task Manager before reopening the launcher"))
	}
	return err
}

func showError(message string) {
	fmt.Fprintln(os.Stderr, message)
	text, err := syscall.UTF16PtrFromString(strings.ReplaceAll(message, "\x00", ""))
	if err != nil {
		return
	}
	title, _ := syscall.UTF16PtrFromString("Root Six Player")
	syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x10)
}
