//go:build linux

package main

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func TestLinuxDiscoveryUsesXDGAndSteamRootLink(t *testing.T) {
	home, data := t.TempDir(), t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("XDG_DATA_HOME", data)
	roots, err := steamRoots()
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{filepath.Join(data, "Steam"), filepath.Join(home, ".steam/root"), filepath.Join(home, "snap/steam/common/.local/share/Steam")} {
		found := false
		for _, root := range roots {
			if root == expected {
				found = true
			}
		}
		if !found {
			t.Fatalf("missing Steam discovery path %s", expected)
		}
	}
}

func TestLinuxBudgetValidation(t *testing.T) {
	const gib = uint64(1024 * 1024 * 1024)
	for _, test := range []struct {
		name, group, limit, swap string
		available                uint64
	}{
		{"wrong service", "/unbounded", "4294967296", "0", 16 * gib},
		{"unlimited", "/root-private-test.service", "max", "0", 16 * gib},
		{"oversized", "/root-private-test.service", "8589934593", "0", 16 * gib},
		{"swap", "/root-private-test.service", "4294967296", "max", 16 * gib},
		{"reserve", "/root-private-test.service", "4294967296", "0", 7 * gib},
	} {
		t.Run(test.name, func(t *testing.T) {
			var budget *BudgetError
			if err := validateBudget(test.group, test.limit, test.swap, test.available); !errors.As(err, &budget) {
				t.Fatalf("unsafe development budget accepted: %v", err)
			}
		})
	}
	if err := validateBudget("/root-private-test.service", "8589934592\n", "0\n", 8*gib); err != nil {
		t.Fatal(err)
	}
}

func TestLinuxCompletedGameNeedsNoTermination(t *testing.T) {
	done := make(chan struct{})
	close(done)
	game := &GameProcess{cmd: &exec.Cmd{}, done: done, proton: filepath.Join(t.TempDir(), "unavailable-proton")}
	if err := stopPlatformProcess(game); err != nil {
		t.Fatalf("cleanly exited game required Wine termination: %v", err)
	}
}

func TestLinuxDataLockConflictsAndReleases(t *testing.T) {
	data := t.TempDir()
	first, err := acquireLock(data)
	if err != nil {
		t.Fatal(err)
	}
	defer first.Close()
	if second, err := acquireLock(data); err == nil {
		second.Close()
		t.Fatal("concurrent launcher accepted")
	}
	if err := first.Close(); err != nil {
		t.Fatal(err)
	}
	last, err := acquireLock(data)
	if err != nil {
		t.Fatal(err)
	}
	last.Close()
}

func TestLinuxHeadlessPlanUsesOwnMutedDisplayAndPrefix(t *testing.T) {
	library, lab, bin := t.TempDir(), t.TempDir(), t.TempDir()
	discoveryWrite(t, filepath.Join(library, "steamapps/common/Proton - Experimental/proton"), "fixture")
	discoveryWrite(t, filepath.Join(library, "steamapps/common/SteamLinuxRuntime_4/_v2-entry-point"), "fixture")
	discoveryWrite(t, filepath.Join(bin, "xvfb-run"), "#!/bin/sh\nexit 99\n")
	if err := os.Chmod(filepath.Join(bin, "xvfb-run"), 0700); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", bin)
	cmd, _, err := platformGameCommand(Installation{Steam: library, Libraries: []string{library}}, lab, nil,
		[]string{"DISPLAY=:0", "WAYLAND_DISPLAY=desktop", "WINEPREFIX=/unrelated"}, true)
	if err != nil {
		t.Fatal(err)
	}
	if cmd.SysProcAttr == nil || !cmd.SysProcAttr.Setsid || cmd.Args[0] != "xvfb-run" {
		t.Fatal("headless launch lacks isolated process group/display")
	}
	values := make(map[string]string)
	for _, item := range cmd.Env {
		key, value, _ := strings.Cut(item, "=")
		values[key] = value
	}
	if values["DISPLAY"] != "" || values["WAYLAND_DISPLAY"] != "" || values["WINEPREFIX"] != filepath.Join(lab, "compatdata/pfx") {
		t.Fatal("desktop display or unrelated prefix inherited")
	}
	if values["SDL_AUDIODRIVER"] != "dummy" || !strings.Contains(values["PULSE_SERVER"], "no-audio-socket") {
		t.Fatal("headless game is not muted")
	}
	if !strings.Contains(strings.Join(cmd.Args, " "), "--auto-servernum") {
		t.Fatal("Xvfb display is not allocated independently")
	}
}
