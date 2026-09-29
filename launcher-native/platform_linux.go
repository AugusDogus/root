//go:build linux

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
)

func steamRoots() ([]string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return nil, err
	}
	data := os.Getenv("XDG_DATA_HOME")
	if !filepath.IsAbs(data) {
		data = filepath.Join(home, ".local/share")
	}
	return []string{filepath.Join(data, "Steam"), filepath.Join(home, ".local/share/Steam"),
		filepath.Join(home, ".steam/steam"), filepath.Join(home, ".steam/root"), filepath.Join(home, ".steam/debian-installation"),
		filepath.Join(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
		filepath.Join(home, "snap/steam/common/.local/share/Steam")}, nil
}

func processIdentity(pid int) (string, error) {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if errors.Is(err, os.ErrNotExist) {
		return "", nil
	}
	if err != nil {
		return "", err
	}
	end := strings.LastIndexByte(string(data), ')')
	if end < 0 {
		return "", errors.New("invalid process status")
	}
	fields := strings.Fields(string(data)[end+1:])
	if len(fields) < 20 {
		return "", errors.New("incomplete process status")
	}
	if fields[0] == "Z" || fields[0] == "X" {
		return "", nil
	}
	return fields[19], nil
}

func dataHome() (string, error) {
	base := os.Getenv("XDG_DATA_HOME")
	if base == "" || !filepath.IsAbs(base) {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		base = filepath.Join(home, ".local/share")
	}
	return filepath.Join(base, "root-six-player"), nil
}

func acquireLock(data string) (io.Closer, error) {
	if err := os.MkdirAll(data, 0700); err != nil {
		return nil, err
	}
	file, err := os.OpenFile(filepath.Join(data, "launcher.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err := syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		file.Close()
		return nil, errors.New("another launcher is using this data folder. Close it before starting another")
	}
	return file, nil
}

func availableDisk(path string) (uint64, error) {
	var info syscall.Statfs_t
	if err := syscall.Statfs(path, &info); err != nil {
		return 0, err
	}
	return info.Bavail * uint64(info.Bsize), nil
}

type BudgetError struct{ Reason string }

func (err *BudgetError) Error() string {
	return err.Reason + ". Run development launches through python3 scripts/safe-test.py COMMAND. No game was started"
}

func validateBudget(group, maximum, swap string, available uint64) error {
	if filepath.Base(group) != "root-private-test.service" {
		return &BudgetError{"the development launch is outside its constrained systemd service"}
	}
	limit, err := strconv.ParseUint(strings.TrimSpace(maximum), 10, 64)
	if err != nil || limit > 8*1024*1024*1024 || strings.TrimSpace(swap) != "0" {
		return &BudgetError{"development tests require a maximum 8 GiB RAM budget and no swap"}
	}
	if available < 8*1024*1024*1024 {
		return &BudgetError{"less than 8 GiB RAM is available for the desktop"}
	}
	return nil
}

func checkLaunchBudget(headless bool) error {
	if !headless {
		return nil
	}
	membership, err := os.ReadFile("/proc/self/cgroup")
	if err != nil {
		return &BudgetError{"the process memory limits could not be read"}
	}
	var group string
	for _, line := range strings.Split(string(membership), "\n") {
		if strings.HasPrefix(line, "0::/") {
			group = strings.TrimPrefix(line, "0::")
		}
	}
	if filepath.Base(group) != "root-private-test.service" {
		return &BudgetError{"the development launch is outside its constrained systemd service"}
	}
	cgroup := filepath.Join("/sys/fs/cgroup", strings.TrimPrefix(group, "/"))
	maximum, err := os.ReadFile(filepath.Join(cgroup, "memory.max"))
	if err != nil {
		return &BudgetError{"the test memory limit could not be read"}
	}
	swap, err := os.ReadFile(filepath.Join(cgroup, "memory.swap.max"))
	if err != nil {
		return &BudgetError{"the test swap limit could not be read"}
	}
	memory, err := os.ReadFile("/proc/meminfo")
	if err != nil {
		return &BudgetError{"available desktop memory could not be read"}
	}
	var available uint64
	for _, line := range strings.Split(string(memory), "\n") {
		fields := strings.Fields(line)
		if len(fields) == 3 && fields[0] == "MemAvailable:" && fields[2] == "kB" {
			kilobytes, err := strconv.ParseUint(fields[1], 10, 64)
			if err == nil && kilobytes <= ^uint64(0)/1024 {
				available = kilobytes * 1024
			}
		}
	}
	return validateBudget(group, string(maximum), string(swap), available)
}

func platformGameCommand(inst Installation, lab string, args, env []string, headless bool) (*exec.Cmd, string, error) {
	proton, runtime, err := protonPaths(inst)
	if err != nil {
		return nil, "", err
	}
	prefix := filepath.Join(lab, "compatdata")
	shader := filepath.Join(lab, "shadercache")
	for _, path := range []string{prefix, shader} {
		if err := os.MkdirAll(path, 0700); err != nil {
			return nil, "", err
		}
	}
	env = replaceEnvironment(env, map[string]string{
		"WINEPREFIX": filepath.Join(prefix, "pfx"), "STEAM_COMPAT_DATA_PATH": prefix,
		"STEAM_COMPAT_CLIENT_INSTALL_PATH": inst.Steam, "STEAM_COMPAT_SHADER_PATH": shader,
		"WINEDLLOVERRIDES": "winhttp=n,b",
	})
	command := append([]string{runtime, "--verb=waitforexitandrun", "--", filepath.Join(proton, "proton"),
		"waitforexitandrun", filepath.Join(lab, "game", "Root.exe")}, args...)
	if headless {
		if _, err := exec.LookPath("xvfb-run"); err != nil {
			return nil, "", errors.New("headless development requires xvfb-run; no game was started")
		}
		clean := make([]string, 0, len(env))
		for _, value := range env {
			key, _, _ := strings.Cut(value, "=")
			if key != "DISPLAY" && key != "WAYLAND_DISPLAY" && key != "WAYLAND_SOCKET" {
				clean = append(clean, value)
			}
		}
		env = replaceEnvironment(clean, map[string]string{
			"ROOT_FRIENDS_LAUNCHER": "0", "SDL_AUDIODRIVER": "dummy", "SDL_VIDEODRIVER": "x11", "XDG_SESSION_TYPE": "x11",
			"PULSE_SERVER":          "unix:" + filepath.Join(lab, "no-audio-socket"),
			"WINEDLLOVERRIDES":      "winhttp=n,b;winepulse.drv=d;winealsa.drv=d",
			"PROTON_ENABLE_WAYLAND": "0", "PROTON_USE_WINED3D": "1", "LIBGL_ALWAYS_SOFTWARE": "1", "LP_NUM_THREADS": "2",
		})
		// Leave room for window decorations around a full 1080p client.
		command = append([]string{"xvfb-run", "--auto-servernum", "--server-args=-screen 0 2000x1200x24 -nolisten tcp", "nice", "-n", "10"}, command...)
		command = append(command, "-noaudio", "-job-worker-count", "2")
	}
	cmd := exec.Command(command[0], command[1:]...)
	cmd.Dir, cmd.Env = filepath.Join(lab, "game"), env
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	return cmd, proton, nil
}

func stopPlatformProcess(game *GameProcess) error {
	// Bootstrap normally quits by itself. A stopped Wine server returns an
	// error to -k, so do not terminate an already completed launch.
	select {
	case <-game.done:
		return nil
	default:
	}
	var result error
	if game.proton != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		stop := exec.CommandContext(ctx, filepath.Join(game.proton, "files/bin/wineserver"), "-k")
		stop.Env = game.cmd.Env
		if err := stop.Run(); err != nil {
			result = fmt.Errorf("the isolated Wine prefix did not stop cleanly: %w", err)
		}
		cancel()
	}
	select {
	case <-game.done:
		return result
	default:
	}
	if err := syscall.Kill(-game.cmd.Process.Pid, syscall.SIGTERM); err != nil && !errors.Is(err, syscall.ESRCH) {
		result = errors.Join(result, err)
	}
	timer := time.NewTimer(15 * time.Second)
	defer timer.Stop()
	select {
	case <-game.done:
		return result
	case <-timer.C:
	}
	if err := syscall.Kill(-game.cmd.Process.Pid, syscall.SIGKILL); err != nil && !errors.Is(err, syscall.ESRCH) {
		result = errors.Join(result, err)
	}
	timer.Reset(10 * time.Second)
	select {
	case <-game.done:
	case <-timer.C:
		result = errors.Join(result, errors.New("the isolated game process did not exit after termination"))
	}
	return result
}

func showError(message string) {
	fmt.Fprintln(os.Stderr, message)
	if _, err := exec.LookPath("zenity"); err == nil {
		_ = exec.Command("zenity", "--error", "--no-markup", "--title=Root Six Player", "--text="+message, "--width=460").Run()
	}
}
