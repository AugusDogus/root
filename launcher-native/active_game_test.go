package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestActiveGamePreventsUpdatesAndRejectsReusedPID(t *testing.T) {
	data := t.TempDir()
	identity, err := processIdentity(os.Getpid())
	if err != nil || identity == "" {
		t.Fatalf("process identity: %q %v", identity, err)
	}
	path := filepath.Join(data, "active-game.json")
	if err := writeJSON(path, activeGame{os.Getpid(), identity}); err != nil {
		t.Fatal(err)
	}
	if requireGameClosed(data) == nil {
		t.Fatal("running game permitted an update")
	}
	if err := writeJSON(path, activeGame{os.Getpid(), "different-start-time"}); err != nil {
		t.Fatal(err)
	}
	if err := requireGameClosed(data); err != nil {
		t.Fatalf("reused PID blocked launcher: %v", err)
	}
}
