package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"unicode/utf8"
)

type Installation struct {
	Game      string
	Steam     string
	Libraries []string
}

var vdfPairPattern = regexp.MustCompile(`"((?:[^"\\]|\\.)*)"\s*"((?:[^"\\]|\\.)*)"`)

func vdfPairs(path string) ([][2]string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || info.Size() > 4*1024*1024 {
		return nil, fmt.Errorf("Steam configuration %s is not a regular file under 4 MiB", path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if !utf8.Valid(data) {
		return nil, fmt.Errorf("Steam configuration %s is not valid UTF-8", path)
	}
	unescape := strings.NewReplacer(`\\`, `\`, `\"`, `"`)
	var pairs [][2]string
	for _, match := range vdfPairPattern.FindAllStringSubmatch(string(data), -1) {
		pairs = append(pairs, [2]string{unescape.Replace(match[1]), unescape.Replace(match[2])})
	}
	return pairs, nil
}

func resolvedPath(path string) (string, error) {
	if path == "~" || strings.HasPrefix(path, "~/") || strings.HasPrefix(path, `~\`) {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		path = filepath.Join(home, strings.TrimPrefix(strings.TrimPrefix(path[1:], "/"), `\`))
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return "", err
	}
	resolved, err := filepath.EvalSymlinks(abs)
	if errors.Is(err, os.ErrNotExist) {
		return filepath.Clean(abs), nil
	}
	return resolved, err
}

func regularFile(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.Mode().IsRegular()
}

func directoryExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

func librariesFor(root string) ([]string, error) {
	resolved, err := resolvedPath(root)
	if err != nil {
		return nil, err
	}
	libraries := []string{resolved}
	pairs, err := vdfPairs(filepath.Join(root, "steamapps", "libraryfolders.vdf"))
	if errors.Is(err, os.ErrNotExist) {
		return libraries, nil
	}
	if err != nil {
		return nil, err
	}
	seen := map[string]bool{resolved: true}
	for _, pair := range pairs {
		// Older Steam clients store library paths directly under numeric keys.
		legacy := pair[0] != "" && strings.Trim(pair[0], "0123456789") == "" && filepath.IsAbs(pair[1])
		if (pair[0] != "path" && !legacy) || pair[1] == "" {
			continue
		}
		library, err := resolvedPath(pair[1])
		if err != nil {
			return nil, err
		}
		if !seen[library] {
			libraries = append(libraries, library)
			seen[library] = true
		}
	}
	return libraries, nil
}

func validateGame(game string) error {
	manifest := filepath.Join(filepath.Dir(filepath.Dir(game)), "appmanifest_"+AppID+".acf")
	pairs, err := vdfPairs(manifest)
	if err != nil {
		return fmt.Errorf("Root's Steam manifest could not be read. Verify Root's installation in Steam, then retry: %w", err)
	}
	fields := make(map[string]string)
	for _, pair := range pairs {
		fields[pair[0]] = pair[1]
	}
	if fields["appid"] != AppID || fields["buildid"] != GameBuild {
		return fmt.Errorf("this mod needs Root Steam build %s; the selected installation has build %q. No game files were changed", GameBuild, fields["buildid"])
	}
	if fields["installdir"] != filepath.Base(game) {
		return errors.New("the selected game folder does not match Root's Steam manifest")
	}
	for _, name := range []string{"Root.exe", "GameAssembly.dll", "Root_Data/il2cpp_data/Metadata/global-metadata.dat"} {
		if !regularFile(filepath.Join(game, filepath.FromSlash(name))) {
			return fmt.Errorf("Root is missing %s. Verify Root's files in Steam before preparing the mod", name)
		}
	}
	return nil
}

func Discover(override string) (Installation, error) {
	roots, err := steamRoots()
	if err != nil {
		return Installation{}, err
	}
	return discoverAt(override, roots)
}

func discoverAt(override string, roots []string) (Installation, error) {
	var firstError error
	for _, root := range roots {
		if !directoryExists(filepath.Join(root, "steamapps")) {
			continue
		}
		libraries, err := librariesFor(root)
		if err != nil {
			if firstError == nil {
				firstError = err
			}
			continue
		}
		for _, library := range libraries {
			var game string
			if override != "" {
				game, err = resolvedPath(override)
				if err != nil {
					return Installation{}, err
				}
			} else {
				game, err = gameFromManifest(library)
				if errors.Is(err, os.ErrNotExist) {
					continue
				}
				if err != nil {
					if firstError == nil {
						firstError = err
					}
					continue
				}
			}
			if !directoryExists(game) {
				continue
			}
			if err = validateGame(game); err != nil {
				if firstError == nil {
					firstError = err
				}
				continue
			}
			return Installation{Game: game, Steam: libraries[0], Libraries: libraries}, nil
		}
	}
	if firstError != nil {
		return Installation{}, firstError
	}
	return Installation{}, errors.New("Root was not found in Steam's libraries. Open Steam, install Root, then reopen this launcher. If Root is already installed, launch it once through Steam and retry")
}

func gameFromManifest(library string) (string, error) {
	pairs, err := vdfPairs(filepath.Join(library, "steamapps", "appmanifest_"+AppID+".acf"))
	if err != nil {
		return "", err
	}
	for _, pair := range pairs {
		if pair[0] != "installdir" {
			continue
		}
		name := pair[1]
		if name == "" || name == "." || name == ".." || strings.ContainsAny(name, `/\:`) {
			return "", errors.New("Root's Steam manifest has an invalid installation folder. Verify Root's files in Steam, then retry")
		}
		return filepath.Join(library, "steamapps", "common", name), nil
	}
	return "", errors.New("Root's Steam manifest is missing its installation folder. Verify Root's files in Steam, then retry")
}

func protonPaths(inst Installation) (string, string, error) {
	var proton, runtime string
	for _, library := range inst.Libraries {
		candidate := filepath.Join(library, "steamapps", "common", "Proton - Experimental")
		if proton == "" && regularFile(filepath.Join(candidate, "proton")) {
			proton = candidate
		}
		candidate = filepath.Join(library, "steamapps", "common", "SteamLinuxRuntime_4", "_v2-entry-point")
		if runtime == "" && regularFile(candidate) {
			runtime = candidate
		}
	}
	if proton == "" || runtime == "" {
		return "", "", errors.New("install Proton Experimental and Steam Linux Runtime 4 in Steam, then retry")
	}
	return proton, runtime, nil
}
