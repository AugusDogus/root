package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func discoveryWrite(t *testing.T, path, value string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(value), 0600); err != nil {
		t.Fatal(err)
	}
}

func discoveryFixture(t *testing.T, library string) string {
	t.Helper()
	game := filepath.Join(library, "steamapps", "common", "Root")
	for _, path := range []string{"Root.exe", "GameAssembly.dll", "Root_Data/il2cpp_data/Metadata/global-metadata.dat"} {
		discoveryWrite(t, filepath.Join(game, filepath.FromSlash(path)), "fixture")
	}
	discoveryWrite(t, filepath.Join(library, "steamapps", "appmanifest_"+AppID+".acf"),
		fmt.Sprintf(`"AppState" { "appid" "%s" "buildid" "%s" "installdir" "Root" }`, AppID, GameBuild))
	return game
}

func TestDiscoverAdditionalSteamLibrary(t *testing.T) {
	root := t.TempDir()
	steam, library := filepath.Join(root, "Steam"), filepath.Join(root, "Other Library")
	game := discoveryFixture(t, library)
	quoted := strings.ReplaceAll(library, `\`, `\\`)
	discoveryWrite(t, filepath.Join(steam, "steamapps", "libraryfolders.vdf"),
		fmt.Sprintf(`"libraryfolders" { "0" { "path" "%s" } "1" { "path" "%s" } }`, quoted, quoted))
	inst, err := discoverAt("", []string{steam})
	if err != nil {
		t.Fatal(err)
	}
	if inst.Game != game || len(inst.Libraries) != 2 || inst.Steam != steam {
		t.Fatalf("unexpected installation: %#v", inst)
	}
}

func TestDiscoveryRejectsWrongBuildAndMissingFiles(t *testing.T) {
	for _, broken := range []string{"build", "metadata", "folder"} {
		t.Run(broken, func(t *testing.T) {
			root := t.TempDir()
			game := discoveryFixture(t, root)
			switch broken {
			case "build":
				discoveryWrite(t, filepath.Join(root, "steamapps", "appmanifest_"+AppID+".acf"), `"appid" "965580" "buildid" "old" "installdir" "Root"`)
			case "metadata":
				if err := os.Remove(filepath.Join(game, "Root_Data/il2cpp_data/Metadata/global-metadata.dat")); err != nil {
					t.Fatal(err)
				}
			case "folder":
				renamed := filepath.Join(filepath.Dir(game), "Wrong")
				if err := os.Rename(game, renamed); err != nil {
					t.Fatal(err)
				}
				game = renamed
			}
			if _, err := discoverAt(game, []string{root}); err == nil {
				t.Fatal("invalid Steam installation accepted")
			}
		})
	}
}

func TestVDFEscapesWindowsLibraryPaths(t *testing.T) {
	path := filepath.Join(t.TempDir(), "libraryfolders.vdf")
	discoveryWrite(t, path, `"path" "D:\\SteamLibrary" "label" "a \"quote\""`)
	pairs, err := vdfPairs(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(pairs) != 2 || pairs[0][1] != `D:\SteamLibrary` || pairs[1][1] != `a "quote"` {
		t.Fatalf("incorrect VDF escape parsing: %#v", pairs)
	}
}

func TestDiscoverLegacySteamLibrary(t *testing.T) {
	steam, library := t.TempDir(), t.TempDir()
	game := discoveryFixture(t, library)
	quoted := strings.ReplaceAll(library, `\`, `\\`)
	discoveryWrite(t, filepath.Join(steam, "steamapps", "libraryfolders.vdf"),
		fmt.Sprintf(`"LibraryFolders" { "TimeNextStatsReport" "123456" "1" "%s" }`, quoted))
	inst, err := discoverAt("", []string{steam})
	if err != nil || inst.Game != game || len(inst.Libraries) != 2 {
		t.Fatalf("legacy library was not discovered: %#v, %v", inst, err)
	}
}

func TestProtonAndRuntimeMayUseDifferentLibraries(t *testing.T) {
	first, second := t.TempDir(), t.TempDir()
	proton := filepath.Join(first, "steamapps/common/Proton - Experimental")
	runtime := filepath.Join(second, "steamapps/common/SteamLinuxRuntime_4/_v2-entry-point")
	discoveryWrite(t, filepath.Join(proton, "proton"), "fixture")
	discoveryWrite(t, runtime, "fixture")
	actualProton, actualRuntime, err := protonPaths(Installation{Libraries: []string{first, second}})
	if err != nil || actualProton != proton || actualRuntime != runtime {
		t.Fatalf("Proton discovery failed: %q, %q, %v", actualProton, actualRuntime, err)
	}
}

func TestDiscoveryUsesManifestDirectory(t *testing.T) {
	root := t.TempDir()
	game := discoveryFixture(t, root)
	renamed := filepath.Join(filepath.Dir(game), "Root Custom Folder")
	if err := os.Rename(game, renamed); err != nil {
		t.Fatal(err)
	}
	discoveryWrite(t, filepath.Join(root, "steamapps", "appmanifest_"+AppID+".acf"),
		fmt.Sprintf(`"appid" "%s" "buildid" "%s" "installdir" "Root Custom Folder"`, AppID, GameBuild))
	inst, err := discoverAt("", []string{root})
	if err != nil || inst.Game != renamed {
		t.Fatalf("manifest directory was not discovered: %v, %v", inst, err)
	}
}

func TestDiscoveryRejectsManifestPathTraversal(t *testing.T) {
	root := t.TempDir()
	discoveryWrite(t, filepath.Join(root, "steamapps", "appmanifest_"+AppID+".acf"),
		fmt.Sprintf(`"appid" "%s" "buildid" "%s" "installdir" "../../outside"`, AppID, GameBuild))
	if _, err := discoverAt("", []string{root}); err == nil {
		t.Fatal("unsafe manifest directory accepted")
	}
}
