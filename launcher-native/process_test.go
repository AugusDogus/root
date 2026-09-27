package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestGameEnvironmentStripsInheritedLabConfiguration(t *testing.T) {
	t.Setenv("ROOT_LAB_SAVE_FILE", "/unrelated-save")
	t.Setenv("ROOT_LAB_TEST_SEED", "unexpected")
	t.Setenv("ROOT_LAB_MODE", "server")
	values := make(map[string]string)
	for _, item := range gameEnvironment("steam-menu", GameOptions{}) {
		key, value, _ := strings.Cut(item, "=")
		values[key] = value
	}
	if values["ROOT_LAB_MODE"] != "steam-menu" || values["ROOT_LAB_SAVE_FILE"] != "" || values["ROOT_LAB_TEST_SEED"] != "" {
		t.Fatal("unrelated lab configuration leaked into the launcher")
	}
	if values["SteamAppId"] != AppID || values["ROOT_FRIENDS_LAUNCHER"] != "1" {
		t.Fatal("launcher identity not set")
	}
}

func TestStartRefusesSteamInstallationBeforeLaunch(t *testing.T) {
	root := t.TempDir()
	game := filepath.Join(root, "game")
	discoveryWrite(t, filepath.Join(game, "Root.exe"), "not executable")
	_, err := StartGame(Installation{Game: game}, root, "steam-menu", GameOptions{})
	if err == nil || !strings.Contains(err.Error(), "Steam installation") {
		t.Fatalf("normal Steam folder was not rejected: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "results")); !os.IsNotExist(err) {
		t.Fatal("rejection touched the game folder")
	}
}

func TestProbeRequiresHeadlessModeBeforeLaunch(t *testing.T) {
	for _, mode := range []string{"steam-online-test", "steam-menu-test"} {
		_, err := StartGame(Installation{}, t.TempDir(), mode, GameOptions{})
		if err == nil || !strings.Contains(err.Error(), "headless") {
			t.Fatalf("visible UI probe %s was accepted: %v", mode, err)
		}
	}
}

func TestEveryGameModeDisablesBepInExConsole(t *testing.T) {
	for _, mode := range []string{"bootstrap", "server", "steam-menu", "steam-host", "steam-client", "steam-wait", "steam-online-test", "steam-menu-test"} {
		args := gameArguments(t.TempDir(), mode, false)
		found := false
		for index, value := range args {
			if value == "--enable-console" && index+1 < len(args) && args[index+1] == "false" {
				found = true
			}
		}
		if !found {
			t.Fatalf("mode %s can allocate a BepInEx console", mode)
		}
	}
}
