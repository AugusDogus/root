package main

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"testing/fstest"
)

func prepareTestGame(t *testing.T) Installation {
	t.Helper()
	library := t.TempDir()
	game := filepath.Join(library, "steamapps", "common", "Root")
	for name, value := range map[string]string{
		"Root.exe": "game", "GameAssembly.dll": "assembly", "Root_Data/il2cpp_data/Metadata/global-metadata.dat": "metadata",
		"BepInEx/plugins/OtherMod.dll": "steam mod", "dotnet/user.dll": "steam runtime",
		"winhttp.dll": "steam proxy", "doorstop_config.ini": "steam doorstop",
	} {
		prepareTestWrite(t, filepath.Join(game, filepath.FromSlash(name)), []byte(value))
	}
	manifest := `"AppState" { "appid" "965580" "buildid" "` + GameBuild + `" "installdir" "Root" }`
	prepareTestWrite(t, filepath.Join(library, "steamapps", "appmanifest_965580.acf"), []byte(manifest))
	return Installation{Game: game}
}

func prepareTestWrite(t *testing.T, name string, content []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(name), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(name, content, 0644); err != nil {
		t.Fatal(err)
	}
}

func prepareTestZip(t *testing.T, names map[string]string, link string) []byte {
	t.Helper()
	var content bytes.Buffer
	archive := zip.NewWriter(&content)
	for name, value := range names {
		header := &zip.FileHeader{Name: name, Method: zip.Deflate}
		header.SetMode(0644)
		if name == link {
			header.SetMode(os.ModeSymlink | 0777)
		}
		entry, err := archive.CreateHeader(header)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := entry.Write([]byte(value)); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return content.Bytes()
}

func prepareTestPayload(t *testing.T, loader []byte) fstest.MapFS {
	t.Helper()
	if loader == nil {
		loader = prepareTestZip(t, map[string]string{
			"winhttp.dll": "bundled proxy", "doorstop_config.ini": "bundled doorstop",
			"BepInEx/core/BepInEx.dll": "loader", "dotnet/runtime.dll": "bundled runtime",
		}, "")
	}
	plugin := []byte("new private mod")
	unity := prepareTestZip(t, map[string]string{"UnityEngine.CoreModule.dll": "cached unity"}, "")
	manifest, err := json.Marshal(prepareManifest{
		preparedMetadata: preparedMetadata{Build: GameBuild, Version: Version, PluginSHA256: prepareDigest(plugin)},
		LoaderSHA256:     prepareDigest(loader), UnitySHA256: prepareDigest(unity), UnityVersion: prepareUnityVersion,
	})
	if err != nil {
		t.Fatal(err)
	}
	return fstest.MapFS{
		"manifest.json": {Data: manifest}, "EngineProbe.dll": {Data: plugin},
		"loader.zip": {Data: loader}, "unity.zip": {Data: unity},
	}
}

func TestPrepareRejectsCorruptPayloadBeforeWriting(t *testing.T) {
	for _, name := range []string{"EngineProbe.dll", "loader.zip", "unity.zip"} {
		t.Run(name, func(t *testing.T) {
			inst := prepareTestGame(t)
			data := filepath.Join(t.TempDir(), "new-data")
			payload := prepareTestPayload(t, nil)
			payload[name].Data = []byte("corrupt")
			if err := Prepare(inst, data, payload, nil); err == nil || !strings.Contains(err.Error(), "checksum") {
				t.Fatalf("expected checksum rejection, got %v", err)
			}
			if _, err := os.Lstat(data); !errors.Is(err, os.ErrNotExist) {
				t.Fatalf("preparation wrote data before verifying payload: %v", err)
			}
		})
	}
}

func TestPrepareRejectsUnsafeArchivesBeforeWriting(t *testing.T) {
	for _, test := range []struct{ name, link string }{
		{"../escaped", ""}, {`BepInEx\..\..\escaped`, ""}, {"/escaped", ""},
		{`C:\escaped`, ""}, {"BepInEx/.. /escaped", ""}, {"BepInEx/CON", ""},
		{"BepInEx/link", "BepInEx/link"},
	} {
		t.Run(test.name, func(t *testing.T) {
			inst := prepareTestGame(t)
			data := filepath.Join(t.TempDir(), "new-data")
			payload := prepareTestPayload(t, prepareTestZip(t, map[string]string{test.name: "bad"}, test.link))
			if err := Prepare(inst, data, payload, nil); err == nil {
				t.Fatal("unsafe archive was accepted")
			}
			if _, err := os.Lstat(data); !errors.Is(err, os.ErrNotExist) {
				t.Fatalf("unsafe archive created a data directory: %v", err)
			}
		})
	}
}

func TestPrepareMakesOfflineIsolatedCopies(t *testing.T) {
	inst := prepareTestGame(t)
	data := filepath.Join(t.TempDir(), "data")
	payload := prepareTestPayload(t, nil)
	var updates []string
	if err := Prepare(inst, data, payload, func(message string) { updates = append(updates, message) }); err != nil {
		t.Fatal(err)
	}
	if len(updates) == 0 {
		t.Fatal("no preparation progress was reported")
	}
	for _, role := range []string{"host", "client"} {
		game := filepath.Join(data, role, "game")
		prepareTestContents(t, filepath.Join(game, "Root.exe"), []byte("game"))
		prepareTestContents(t, filepath.Join(game, "winhttp.dll"), []byte("bundled proxy"))
		prepareTestContents(t, filepath.Join(game, "BepInEx", "plugins", "EngineProbe.dll"), payload["EngineProbe.dll"].Data)
		prepareTestContents(t, filepath.Join(game, "BepInEx", "unity-libs", prepareUnityVersion+".zip"), payload["unity.zip"].Data)
		config, err := os.ReadFile(filepath.Join(game, "BepInEx", "config", "BepInEx.cfg"))
		if err != nil || !strings.Contains(string(config), "[IL2CPP]\nUnityBaseLibrariesSource = 2022.3.62.zip") {
			t.Fatalf("offline Unity configuration missing: %q, %v", config, err)
		}
		for _, name := range []string{"BepInEx/plugins/OtherMod.dll", "dotnet/user.dll"} {
			if _, err := os.Stat(filepath.Join(game, filepath.FromSlash(name))); !errors.Is(err, os.ErrNotExist) {
				t.Fatalf("source mod was copied: %s", name)
			}
		}
		if _, err := os.Stat(filepath.Join(data, role, "bindings-ready")); !errors.Is(err, os.ErrNotExist) {
			t.Fatal("Prepare must leave binding initialization to Bootstrap")
		}
	}
	prepareTestContents(t, filepath.Join(inst.Game, "winhttp.dll"), []byte("steam proxy"))
	prepareTestContents(t, filepath.Join(inst.Game, "BepInEx", "plugins", "OtherMod.dll"), []byte("steam mod"))
	if _, err := os.Stat(filepath.Join(data, "preparing")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("staging remains after successful preparation: %v", err)
	}
}

func prepareTestPrevious(t *testing.T, data string) []byte {
	t.Helper()
	old := []byte("previous private mod")
	marker, err := json.Marshal(preparedMetadata{Build: GameBuild, Version: "0.6.0", PluginSHA256: prepareDigest(old)})
	if err != nil {
		t.Fatal(err)
	}
	prepareTestWrite(t, filepath.Join(data, "prepared.json"), marker)
	for _, role := range []string{"host", "client"} {
		game := filepath.Join(data, role, "game")
		prepareTestWrite(t, filepath.Join(game, "Root.exe"), []byte("old isolated game"))
		prepareTestWrite(t, filepath.Join(game, "BepInEx", "plugins", "EngineProbe.dll"), old)
		prepareTestWrite(t, filepath.Join(game, "BepInEx", "core", "BepInEx.dll"), []byte("existing loader preserved"))
		prepareTestWrite(t, filepath.Join(game, "BepInEx", "config", "BepInEx.cfg"), []byte("# Custom settings\r\n[Logging.Console]\r\nEnabled = true\r\n\r\n[IL2CPP]\r\nUnityBaseLibrariesSource = https://example.invalid/{VERSION}.zip\r\nUpdateInteropAssemblies = false\r\n"))
	}
	prepareTestWrite(t, filepath.Join(data, "saves", "friends.json"), []byte("saved match"))
	prepareTestWrite(t, filepath.Join(data, "client", "compatdata", "prefs"), []byte("existing preferences"))
	return marker
}

func TestPrepareUpgradesPythonMetadataWithoutReplacingLoaderOrSaves(t *testing.T) {
	inst := prepareTestGame(t)
	data := filepath.Join(t.TempDir(), "data")
	prepareTestPrevious(t, data)
	payload := prepareTestPayload(t, nil)
	if err := Prepare(inst, data, payload, nil); err != nil {
		t.Fatal(err)
	}
	for _, role := range []string{"host", "client"} {
		game := filepath.Join(data, role, "game")
		prepareTestContents(t, filepath.Join(game, "BepInEx", "plugins", "EngineProbe.dll"), payload["EngineProbe.dll"].Data)
		prepareTestContents(t, filepath.Join(game, "BepInEx", "core", "BepInEx.dll"), []byte("existing loader preserved"))
		prepareTestContents(t, filepath.Join(game, "BepInEx", "unity-libs", prepareUnityVersion+".zip"), payload["unity.zip"].Data)
		config, err := os.ReadFile(filepath.Join(game, "BepInEx", "config", "BepInEx.cfg"))
		if err != nil || !strings.Contains(string(config), "UnityBaseLibrariesSource = 2022.3.62.zip\r\n") ||
			!strings.Contains(string(config), "Enabled = true\r\n") || !strings.Contains(string(config), "UpdateInteropAssemblies = false\r\n") {
			t.Fatalf("configuration was not preserved and migrated: %q, %v", config, err)
		}
	}
	prepareTestContents(t, filepath.Join(data, "saves", "friends.json"), []byte("saved match"))
	prepareTestContents(t, filepath.Join(data, "client", "compatdata", "prefs"), []byte("existing preferences"))
	if err := Prepare(inst, data, payload, nil); err != nil {
		t.Fatalf("preparation is not repeatable: %v", err)
	}
}

func TestPrepareRefusesUnknownModifiedModBeforeUpdatingEitherCopy(t *testing.T) {
	inst := prepareTestGame(t)
	data := filepath.Join(t.TempDir(), "data")
	marker := prepareTestPrevious(t, data)
	clientPlugin := filepath.Join(data, "client", "game", "BepInEx", "plugins", "EngineProbe.dll")
	prepareTestWrite(t, clientPlugin, []byte("unknown user modification"))
	if err := Prepare(inst, data, prepareTestPayload(t, nil), nil); err == nil || !strings.Contains(err.Error(), "changed outside") {
		t.Fatalf("expected unknown-mod rejection, got %v", err)
	}
	prepareTestContents(t, clientPlugin, []byte("unknown user modification"))
	prepareTestContents(t, filepath.Join(data, "host", "game", "BepInEx", "plugins", "EngineProbe.dll"), []byte("previous private mod"))
	prepareTestContents(t, filepath.Join(data, "prepared.json"), marker)
	prepareTestContents(t, filepath.Join(data, "saves", "friends.json"), []byte("saved match"))
}

func TestPrepareRefusesDataInsideSteamGame(t *testing.T) {
	inst := prepareTestGame(t)
	data := filepath.Join(inst.Game, "private-copies")
	if err := Prepare(inst, data, prepareTestPayload(t, nil), nil); err == nil || !strings.Contains(err.Error(), "inside the Steam") {
		t.Fatalf("expected Steam path protection, got %v", err)
	}
	if _, err := os.Lstat(data); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("launcher wrote into the Steam installation")
	}
}

func TestPrepareRollbackRestoresReplacedAndMissingFiles(t *testing.T) {
	directory := t.TempDir()
	oldTarget := filepath.Join(directory, "plugin")
	newTarget := filepath.Join(directory, "cache")
	backup := filepath.Join(directory, "backup")
	prepareTestWrite(t, oldTarget, []byte("new plugin"))
	prepareTestWrite(t, backup, []byte("old plugin"))
	prepareTestWrite(t, newTarget, []byte("new cache"))
	cleanup := true
	err := prepareRollback([]prepareUpdate{
		{target: oldTarget, backup: backup, hadOriginal: true, installed: true},
		{target: newTarget, installed: true},
	}, directory, &cleanup, errors.New("publish failed"))
	if err == nil || !cleanup {
		t.Fatalf("expected a recovered update failure: %v", err)
	}
	prepareTestContents(t, oldTarget, []byte("old plugin"))
	if _, err := os.Stat(newTarget); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("rollback kept a newly installed file")
	}
}

func TestPrepareOfflineConfigInsertsIntoExistingSection(t *testing.T) {
	input := []byte("[IL2CPP]\nUpdateInteropAssemblies = false\n[Logging]\nEnabled = true\n")
	config := prepareOfflineConfig(input)
	if !strings.Contains(string(config), "UnityBaseLibrariesSource = 2022.3.62.zip\n[Logging]") {
		t.Fatalf("Unity setting was not inserted in its own section: %s", config)
	}
	if !bytes.Equal(config, prepareOfflineConfig(config)) {
		t.Fatal("configuration update is not idempotent")
	}
}

func prepareTestContents(t *testing.T, name string, expected []byte) {
	t.Helper()
	actual, err := os.ReadFile(name)
	if err != nil || !bytes.Equal(actual, expected) {
		t.Fatalf("unexpected contents for %s: %q, %v", name, actual, err)
	}
}
