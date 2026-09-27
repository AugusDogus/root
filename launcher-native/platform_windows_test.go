//go:build windows

package main

import (
	"path/filepath"
	"testing"
)

func TestWindowsCommandDoesNotCreateConsole(t *testing.T) {
	lab := t.TempDir()
	cmd, _, err := platformGameCommand(Installation{}, lab, []string{"-noaudio"}, nil, false)
	if err != nil {
		t.Fatal(err)
	}
	if cmd.Path != filepath.Join(lab, "game", "Root.exe") || cmd.SysProcAttr == nil || cmd.SysProcAttr.CreationFlags&0x08000000 == 0 {
		t.Fatal("Windows game launch would create a console or use the wrong executable")
	}
	if _, _, err := platformGameCommand(Installation{}, lab, nil, nil, true); err == nil {
		t.Fatal("unsupported Windows headless launch accepted")
	}
}

func TestWindowsLockExcludesOtherLaunchers(t *testing.T) {
	data := t.TempDir()
	lock, err := acquireLock(data)
	if err != nil {
		t.Fatal(err)
	}
	defer lock.Close()
	if second, err := acquireLock(data); err == nil {
		second.Close()
		t.Fatal("concurrent launcher accepted")
	}
}
