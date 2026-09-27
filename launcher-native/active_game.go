package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

type activeGame struct {
	PID     int    `json:"pid"`
	Started string `json:"started"`
}

func requireGameClosed(data string) error {
	var game activeGame
	err := readJSON(filepath.Join(data, "active-game.json"), &game)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil || game.PID <= 0 || game.Started == "" {
		return fmt.Errorf("the running-game record could not be read. Close the modded game and remove active-game.json from %s before retrying", data)
	}
	started, err := processIdentity(game.PID)
	if err != nil {
		return fmt.Errorf("could not check whether Root is running: %w", err)
	}
	if started == game.Started {
		return errors.New("Root Six Player is already running. Close the modded game before opening or updating it")
	}
	return nil
}

func rememberGame(data string, game *GameProcess) error {
	started, err := processIdentity(game.cmd.Process.Pid)
	if err != nil {
		return err
	}
	if started == "" {
		return errors.New("Root exited before it could start. Check the client launch log and retry")
	}
	return writeJSON(filepath.Join(data, "active-game.json"), activeGame{game.cmd.Process.Pid, started})
}
