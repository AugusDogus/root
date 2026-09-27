package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type GameOptions struct {
	Headless bool
}

func validateTestMode(headless bool) error { return checkLaunchBudget(headless) }

func gameArguments(output, mode string, muted bool) []string {
	// BepInEx can explicitly allocate a console even for a GUI child process.
	// Its own command-line override keeps that console disabled in every mode.
	args := []string{"--enable-console", "false", "-logFile", filepath.Join(output, "player.log")}
	if muted {
		args = append(args, "-noaudio")
	}
	if mode == "server" || mode == "bootstrap" {
		return append(args, "-batchmode", "-nographics", "-noaudio")
	}
	return append(args, "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "800")
}

type GameProcess struct {
	cmd       *exec.Cmd
	lab       string
	proton    string
	done      chan struct{}
	mu        sync.Mutex
	err       error
	closeOnce sync.Once
	closeErr  error
}

func (game *GameProcess) Done() <-chan struct{} { return game.done }

func (game *GameProcess) Err() error {
	game.mu.Lock()
	defer game.mu.Unlock()
	return game.err
}

func gameEnvironment(mode string, opts GameOptions) []string {
	env := make([]string, 0, len(os.Environ())+8)
	for _, value := range os.Environ() {
		key, _, _ := strings.Cut(value, "=")
		if !strings.HasPrefix(strings.ToUpper(key), "ROOT_LAB_") {
			env = append(env, value)
		}
	}
	env = replaceEnvironment(env, map[string]string{
		"ROOT_LAB_MODE": mode, "ROOT_LAB_LIFETIME_SECONDS": "43200", "ROOT_FRIENDS_LAUNCHER": "1",
		"SteamAppId": AppID, "SteamGameId": AppID,
	})
	return env
}

func replaceEnvironment(env []string, values map[string]string) []string {
	result := make([]string, 0, len(env)+len(values))
	for _, item := range env {
		key, _, _ := strings.Cut(item, "=")
		replaced := false
		for replacement := range values {
			if strings.EqualFold(key, replacement) {
				replaced = true
				break
			}
		}
		if !replaced {
			result = append(result, item)
		}
	}
	for key, value := range values {
		result = append(result, key+"="+value)
	}
	return result
}

func StartGame(inst Installation, dataRole string, mode string, opts GameOptions) (*GameProcess, error) {
	if err := checkLaunchBudget(opts.Headless); err != nil {
		return nil, err
	}
	switch mode {
	case "bootstrap", "server", "steam-menu", "steam-host", "steam-client", "steam-wait":
	case "steam-online-test", "steam-menu-test":
		if !opts.Headless {
			return nil, errors.New("the native UI probe requires a constrained headless development launch; no game was started")
		}
	default:
		return nil, errors.New("unsupported launcher game mode; no game was started")
	}
	lab, err := resolvedPath(dataRole)
	if err != nil {
		return nil, err
	}
	copyPath, err := resolvedPath(filepath.Join(lab, "game"))
	if err != nil {
		return nil, err
	}
	original, err := resolvedPath(inst.Game)
	if err != nil {
		return nil, err
	}
	copyInfo, copyError := os.Stat(copyPath)
	originalInfo, originalError := os.Stat(original)
	sameDirectory := copyError == nil && originalError == nil && os.SameFile(copyInfo, originalInfo)
	if sameDirectory || copyPath == original || !regularFile(filepath.Join(copyPath, "Root.exe")) {
		return nil, errors.New("the isolated Root copy is missing or points to the Steam installation. Prepare the launcher before starting; no game was launched")
	}
	output := filepath.Join(lab, "results", mode)
	if err := os.MkdirAll(output, 0700); err != nil {
		return nil, err
	}
	for _, file := range []string{filepath.Join(output, "endpoint.json"), filepath.Join(lab, "steam-status.json"),
		filepath.Join(lab, "native-menu-ready"), filepath.Join(lab, "native-menu-screen"), filepath.Join(lab, "native-board-settings.json")} {
		if err := os.Remove(file); err != nil && !errors.Is(err, os.ErrNotExist) {
			return nil, err
		}
	}
	args := gameArguments(output, mode, os.Getenv("ROOT_FRIENDS_MUTE") == "1")
	cmd, proton, err := platformGameCommand(inst, lab, args, gameEnvironment(mode, opts), opts.Headless)
	if err != nil {
		return nil, err
	}
	log, err := os.OpenFile(filepath.Join(output, "launch.log"), os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil {
		return nil, err
	}
	cmd.Stdout, cmd.Stderr = log, log
	if err := cmd.Start(); err != nil {
		log.Close()
		return nil, fmt.Errorf("Root could not start. Check %s; your Steam installation was not changed: %w", output, err)
	}
	game := &GameProcess{cmd: cmd, lab: lab, proton: proton, done: make(chan struct{})}
	go func() {
		err := cmd.Wait()
		closeErr := log.Close()
		game.mu.Lock()
		game.err = errors.Join(err, closeErr)
		game.mu.Unlock()
		close(game.done)
	}()
	return game, nil
}

func (game *GameProcess) Close() error {
	game.closeOnce.Do(func() {
		game.closeErr = stopPlatformProcess(game)
		select {
		case <-game.done:
			for _, name := range []string{"connection.json", "steam-config.json", "steam-status.json", "results/server/endpoint.json"} {
				if err := os.Remove(filepath.Join(game.lab, filepath.FromSlash(name))); err != nil && !errors.Is(err, os.ErrNotExist) {
					game.closeErr = errors.Join(game.closeErr, err)
				}
			}
		default:
			game.closeErr = errors.Join(game.closeErr, errors.New("Root did not stop. Close the isolated game before relaunching; connection files were preserved"))
		}
	})
	return game.closeErr
}

func Bootstrap(ctx context.Context, inst Installation, data string, headless bool, progress func(string)) error {
	for step, role := range []string{"host", "client"} {
		lab := filepath.Join(data, role)
		marker := filepath.Join(lab, "bindings-ready")
		interop := filepath.Join(lab, "game", "BepInEx", "interop", "tuber-canis.dll")
		if regularFile(marker) && regularFile(interop) {
			continue
		}
		if err := ctx.Err(); err != nil {
			return err
		}
		if progress != nil {
			progress(fmt.Sprintf("Finishing setup (%d of 2). This can take a few minutes.", step+1))
		}
		game, err := StartGame(inst, lab, "bootstrap", GameOptions{Headless: headless})
		if err != nil {
			return err
		}
		timer := time.NewTimer(10 * time.Minute)
		select {
		case <-ctx.Done():
			err = ctx.Err()
		case <-timer.C:
			err = errors.New("Root preparation timed out after ten minutes")
		case <-game.Done():
			err = game.Err()
		}
		timer.Stop()
		err = errors.Join(err, game.Close())
		if err != nil {
			return fmt.Errorf("%s preparation did not complete. Check %s and retry: %w", role, filepath.Join(lab, "results", "bootstrap"), err)
		}
		log, err := os.ReadFile(filepath.Join(lab, "game", "BepInEx", "LogOutput.log"))
		if err != nil || !strings.Contains(string(log), "Private launcher bindings ready") || !regularFile(interop) {
			return fmt.Errorf("%s preparation did not produce game bindings. Check %s and retry", role, filepath.Join(lab, "results", "bootstrap"))
		}
		if err := os.WriteFile(marker, nil, 0600); err != nil {
			return err
		}
	}
	if progress != nil {
		progress("Ready to host or join.")
	}
	return nil
}
