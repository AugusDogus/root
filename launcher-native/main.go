package main

import (
	"context"
	"embed"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
)

// Payloads are assembled and verified by scripts/package-native-launcher.py.
//
//go:embed payload/*
var embedded embed.FS

var reportError = showError

func main() {
	if len(os.Args) == 4 && os.Args[1] == "--launcher-progress" {
		progressChild(os.Args[2], os.Args[3])
		return
	}
	if err := run(); err != nil {
		var handoff *launcherHandoff
		if errors.As(err, &handoff) {
			// run has closed its progress window and released the data lock.
			command := exec.Command(handoff.executable, os.Args[1:]...)
			if err = command.Start(); err == nil {
				_ = command.Process.Release()
				return
			}
			err = fmt.Errorf("The updated launcher could not start. Download the latest launcher from GitHub Releases and retry: %w", err)
		}
		log.Print(err)
		reportError("Root Six Player could not open.\n\n" + err.Error() + "\n\nYour Steam installation and saved matches have not been removed.")
		os.Exit(1)
	}
}

func run() (result error) {
	defaultData, err := dataHome()
	if err != nil {
		return err
	}
	data := flag.String("data", defaultData, "Launcher-owned installation and save directory")
	game := flag.String("game", "", "Root installation inside a Steam library")
	headless := flag.Bool("headless", false, "Run an isolated development test")
	prepareOnly := flag.Bool("prepare-only", false, "Prepare the game without opening the menu")
	menuMode := flag.String("test-menu", "", "Native UI probe mode (isolated tests only)")
	licenseDir := flag.String("licenses", "", "Extract bundled dependency notices and source archives")
	version := flag.Bool("version", false, "Print launcher version")
	noUpdate := flag.Bool("no-update", false, "Skip automatic launcher updates for this launch")
	flag.Parse()
	if *headless {
		reportError = func(message string) { fmt.Fprintln(os.Stderr, message) }
	}
	if flag.NArg() != 0 {
		return fmt.Errorf("unexpected launcher arguments")
	}
	if *version {
		fmt.Println(Version)
		return nil
	}
	payload, err := fs.Sub(embedded, "payload")
	if err != nil {
		return err
	}
	if *licenseDir != "" {
		return exportLicenses(payload, *licenseDir)
	}
	if err = validateTestMode(*headless); err != nil {
		return err
	}
	mode := "steam-menu"
	if *menuMode != "" {
		if !*headless || (*menuMode != "steam-online-test" && *menuMode != "steam-menu-test") {
			return fmt.Errorf("native UI probes require an isolated headless development run")
		}
		mode = *menuMode
	}
	*data, err = filepath.Abs(*data)
	if err != nil {
		return err
	}
	// Resolve existing parents before any writes, including a symlinked data directory.
	parent, err := prepareExistingDirectory(*data)
	if err != nil {
		return err
	}
	resolvedParent, err := filepath.EvalSymlinks(parent)
	if err != nil {
		return err
	}
	remaining, err := filepath.Rel(parent, *data)
	if err != nil {
		return err
	}
	*data = filepath.Join(resolvedParent, remaining)
	inst, err := DiscoverForUpdate(*game)
	if err != nil {
		return err
	}
	if err = prepareOutsideGame(inst.Game, *data); err != nil {
		return err
	}
	if err = os.MkdirAll(*data, 0700); err != nil {
		return err
	}
	lock, err := acquireLock(*data)
	if err != nil {
		return err
	}
	defer lock.Close()
	if err = requireGameClosed(*data); err != nil {
		return err
	}
	file, err := os.OpenFile(filepath.Join(*data, "launcher.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	defer file.Close()
	log.SetOutput(io.MultiWriter(file, os.Stderr))
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	progress := func(message string) { log.Print(message) }
	if !*headless {
		update, closeWindow := OpenProgress(inst)
		defer closeWindow()
		progress = func(message string) { log.Print(message); update(message) }
	}
	if !*headless && !*prepareOnly && !*noUpdate {
		updated, err := launcherUpdate(ctx, *data, progress)
		if err != nil {
			return err
		}
		if updated != "" {
			return &launcherHandoff{executable: updated}
		}
	}
	if err = Prepare(inst, *data, payload, progress); err != nil {
		return err
	}
	if err = Bootstrap(ctx, inst, *data, *headless, progress); err != nil {
		return err
	}
	if *prepareOnly {
		return nil
	}
	// Remove the former control API's stale credentials during upgrades.
	if err = os.Remove(filepath.Join(*data, "client", "launcher-control.json")); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	progress("Opening Root...")
	process, err := StartGame(inst, filepath.Join(*data, "client"), mode, GameOptions{Headless: *headless})
	if err != nil {
		return err
	}
	if err = rememberGame(*data, process); err != nil {
		return errors.Join(err, process.Close())
	}
	// Root now owns its host, saves, and menu lifecycle. Do not wait or close it.
	return nil
}

func exportLicenses(payload fs.FS, directory string) error {
	return fs.WalkDir(payload, ".", func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return nil
		}
		if path != "THIRD-PARTY-NOTICES.txt" && path != "dependency-sources.zip" && path != "license-sources.json" && !strings.HasPrefix(path, "licenses/") {
			return nil
		}
		destination := filepath.Join(directory, filepath.FromSlash(path))
		if err = os.MkdirAll(filepath.Dir(destination), 0755); err != nil {
			return err
		}
		content, err := fs.ReadFile(payload, path)
		if err != nil {
			return err
		}
		// Explicit export never replaces a user's existing license/source file.
		file, err := os.OpenFile(destination, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0644)
		if err != nil {
			return err
		}
		_, err = file.Write(content)
		return errors.Join(err, file.Close())
	})
}
