package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"strings"
)

const prepareUnityVersion = "2022.3.62"

type preparedMetadata struct {
	Build        string `json:"build"`
	Version      string `json:"version"`
	PluginSHA256 string `json:"plugin_sha256"`
}

type prepareManifest struct {
	preparedMetadata
	LoaderSHA256 string `json:"loader_sha256"`
	UnitySHA256  string `json:"unity_sha256"`
	UnityVersion string `json:"unity_version"`
}

type preparedPayload struct {
	manifest prepareManifest
	plugin   []byte
	unity    []byte
	loader   *zip.Reader
}

// Prepare only writes launcher-owned copies. Payload hashes and archive paths
// are checked before creating even a staging directory.
func Prepare(inst Installation, data string, payload fs.FS, progress func(string)) error {
	var err error
	data, err = filepath.Abs(data)
	if err != nil {
		return err
	}
	inst.Game, err = filepath.Abs(inst.Game)
	if err != nil {
		return err
	}
	if err := validateGame(inst.Game); err != nil {
		return err
	}
	bundle, err := prepareReadPayload(payload)
	if err != nil {
		return fmt.Errorf("The launcher package could not be verified. Download a fresh launcher: %w", err)
	}
	if err := prepareOutsideGame(inst.Game, data); err != nil {
		return err
	}
	marker := filepath.Join(data, "prepared.json")
	if _, err := os.Lstat(marker); err == nil {
		return prepareUpgrade(inst, data, bundle, progress)
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	for _, role := range []string{"host", "client"} {
		if _, err := os.Lstat(filepath.Join(data, role)); !errors.Is(err, os.ErrNotExist) {
			return fmt.Errorf("An incomplete or different installation exists in %s. Existing files and saves were preserved; choose a fresh launcher data folder", data)
		}
	}
	size, err := prepareGameSize(inst.Game)
	if err != nil {
		return err
	}
	for _, entry := range bundle.loader.File {
		if entry.UncompressedSize64 > (^uint64(0))-size {
			return errors.New("The loader archive declares an unsupported size")
		}
		size += entry.UncompressedSize64
	}
	if uint64(len(bundle.unity)) > (^uint64(0))-size {
		return errors.New("The Unity archive declares an unsupported size")
	}
	size += uint64(len(bundle.unity))
	const reserve = uint64(2 * 1024 * 1024 * 1024)
	if size > ((^uint64(0))-reserve)/2 {
		return errors.New("The game files exceed the supported preparation size")
	}
	diskPath, err := prepareExistingDirectory(data)
	if err != nil {
		return err
	}
	free, err := availableDisk(diskPath)
	if err != nil {
		return fmt.Errorf("Could not check free space for the game copies: %w", err)
	}
	if free < size*2+reserve {
		return errors.New("Not enough free space. Allow space for two game copies plus 2 GiB for the loader and Proton")
	}
	stage, err := prepareStage(data)
	if err != nil {
		return err
	}
	defer os.RemoveAll(stage)
	for index, role := range []string{"host", "client"} {
		prepareProgress(progress, fmt.Sprintf("Preparing your game (%d of 2)…", index+1))
		game := filepath.Join(stage, role, "game")
		if err := prepareCopyGame(inst.Game, game); err != nil {
			return fmt.Errorf("Could not copy Root. The Steam installation is unchanged: %w", err)
		}
		if err := prepareExtractLoader(bundle.loader, game); err != nil {
			return err
		}
		if err := prepareInstallFiles(game, bundle); err != nil {
			return err
		}
	}
	if err := validateGame(inst.Game); err != nil {
		return fmt.Errorf("Root changed during preparation. The staged copies were discarded: %w", err)
	}
	metadata, err := json.Marshal(bundle.manifest.preparedMetadata)
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(stage, "prepared.json"), metadata, 0600); err != nil {
		return err
	}
	var moved []string
	for _, name := range []string{"host", "client", "prepared.json"} {
		target := filepath.Join(data, name)
		if _, err := os.Lstat(target); !errors.Is(err, os.ErrNotExist) {
			var cleanup []error
			for _, created := range moved {
				cleanup = append(cleanup, os.RemoveAll(created))
			}
			return errors.Join(fmt.Errorf("Preparation stopped because %s appeared while copying. Existing files were preserved", target), errors.Join(cleanup...))
		}
		if err := os.Rename(filepath.Join(stage, name), target); err != nil {
			var cleanup []error
			for _, created := range moved {
				cleanup = append(cleanup, os.RemoveAll(created))
			}
			return errors.Join(fmt.Errorf("Could not finish preparing the isolated copies: %w", err), errors.Join(cleanup...))
		}
		moved = append(moved, target)
	}
	prepareProgress(progress, "Game files ready. Finishing setup…")
	return nil
}

func prepareReadPayload(source fs.FS) (preparedPayload, error) {
	var result preparedPayload
	manifest, err := fs.ReadFile(source, "manifest.json")
	if err != nil {
		return result, err
	}
	if err := json.Unmarshal(manifest, &result.manifest); err != nil {
		return result, err
	}
	if result.manifest.Build != GameBuild || result.manifest.Version != Version || result.manifest.UnityVersion != prepareUnityVersion {
		return result, errors.New("Package game, launcher, or Unity version does not match this launcher")
	}
	var loader []byte
	for _, item := range []struct {
		name, digest string
		target       *[]byte
	}{
		{"EngineProbe.dll", result.manifest.PluginSHA256, &result.plugin},
		{"loader.zip", result.manifest.LoaderSHA256, &loader},
		{"unity.zip", result.manifest.UnitySHA256, &result.unity},
	} {
		content, err := fs.ReadFile(source, item.name)
		if err != nil {
			return result, err
		}
		if !prepareValidHash(item.digest) || prepareDigest(content) != strings.ToLower(item.digest) {
			return result, fmt.Errorf("%s checksum does not match the package manifest", item.name)
		}
		*item.target = content
	}
	result.loader, err = prepareReadZip(loader)
	if err != nil {
		return result, fmt.Errorf("Invalid loader archive: %w", err)
	}
	if _, err := prepareReadZip(result.unity); err != nil {
		return result, fmt.Errorf("Invalid Unity archive: %w", err)
	}
	result.manifest.PluginSHA256 = strings.ToLower(result.manifest.PluginSHA256)
	return result, nil
}

func prepareReadZip(content []byte) (*zip.Reader, error) {
	archive, err := zip.NewReader(bytes.NewReader(content), int64(len(content)))
	if err != nil {
		return nil, err
	}
	seen := make(map[string]bool)
	for _, entry := range archive.File {
		name := strings.ReplaceAll(entry.Name, "\\", "/")
		if name == "" || strings.HasPrefix(name, "/") || strings.Contains(name, ":") {
			return nil, fmt.Errorf("Unsafe archive path %q", entry.Name)
		}
		for _, part := range strings.Split(strings.TrimSuffix(name, "/"), "/") {
			if part == "" || part == "." || part == ".." || strings.TrimRight(part, " .") != part || strings.ContainsAny(part, "\x00\r\n") {
				return nil, fmt.Errorf("Unsafe archive path %q", entry.Name)
			}
			stem := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
			if stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
				len(stem) == 4 && (strings.HasPrefix(stem, "COM") || strings.HasPrefix(stem, "LPT")) && stem[3] >= '1' && stem[3] <= '9' {
				return nil, fmt.Errorf("Unsafe Windows archive path %q", entry.Name)
			}
		}
		key := strings.ToLower(path.Clean(name))
		if seen[key] {
			return nil, fmt.Errorf("Duplicate archive path %q", entry.Name)
		}
		seen[key] = true
		if entry.Mode()&os.ModeSymlink != 0 || !entry.Mode().IsRegular() && !entry.FileInfo().IsDir() {
			return nil, fmt.Errorf("Archive entry %q is not a regular file or directory", entry.Name)
		}
	}
	return archive, nil
}

func prepareExtractLoader(archive *zip.Reader, game string) error {
	for _, entry := range archive.File {
		target := filepath.Join(game, filepath.FromSlash(strings.ReplaceAll(entry.Name, "\\", "/")))
		if entry.FileInfo().IsDir() {
			if err := os.MkdirAll(target, 0755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
			return err
		}
		input, err := entry.Open()
		if err != nil {
			return err
		}
		err = prepareWriteReader(target, input, 0644)
		closeErr := input.Close()
		if err != nil || closeErr != nil {
			return errors.Join(err, closeErr)
		}
	}
	return nil
}

func prepareInstallFiles(game string, bundle preparedPayload) error {
	config := filepath.Join(game, "BepInEx", "config", "BepInEx.cfg")
	oldConfig, err := os.ReadFile(config)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	for _, item := range []struct {
		name string
		data []byte
	}{
		{filepath.Join(game, "BepInEx", "plugins", "EngineProbe.dll"), bundle.plugin},
		{filepath.Join(game, "BepInEx", "unity-libs", prepareUnityVersion+".zip"), bundle.unity},
		{config, prepareOfflineConfig(oldConfig)},
	} {
		if err := os.MkdirAll(filepath.Dir(item.name), 0755); err != nil {
			return err
		}
		if err := os.WriteFile(item.name, item.data, 0644); err != nil {
			return err
		}
	}
	return nil
}

func prepareOfflineConfig(content []byte) []byte {
	newline := "\n"
	if bytes.Contains(content, []byte("\r\n")) {
		newline = "\r\n"
	}
	lines := strings.Split(strings.TrimSuffix(strings.ReplaceAll(string(content), "\r\n", "\n"), "\n"), "\n")
	var result []string
	inIL2CPP, sectionFound, keyFound := false, false, false
	setting := "UnityBaseLibrariesSource = " + prepareUnityVersion + ".zip"
	for _, line := range lines {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			if inIL2CPP && !keyFound {
				result = append(result, setting)
				keyFound = true
			}
			inIL2CPP = strings.EqualFold(trimmed, "[IL2CPP]")
			sectionFound = sectionFound || inIL2CPP
		}
		key, _, assignment := strings.Cut(trimmed, "=")
		if inIL2CPP && assignment && strings.EqualFold(strings.TrimSpace(key), "UnityBaseLibrariesSource") {
			line = line[:len(line)-len(strings.TrimLeft(line, " \t"))] + setting
			keyFound = true
		}
		result = append(result, line)
	}
	if !sectionFound {
		if len(result) != 0 && result[len(result)-1] != "" {
			result = append(result, "")
		}
		result = append(result, "[IL2CPP]", setting)
	} else if !keyFound {
		result = append(result, setting)
	}
	return []byte(strings.Join(result, newline) + newline)
}

type prepareUpdate struct {
	target, staged, backup string
	content                []byte
	hadOriginal, installed bool
}

func prepareUpgrade(inst Installation, data string, bundle preparedPayload, progress func(string)) error {
	marker := filepath.Join(data, "prepared.json")
	if err := prepareNoSymlinks(data, marker); err != nil {
		return err
	}
	content, err := os.ReadFile(marker)
	if err != nil {
		return err
	}
	var previous preparedMetadata
	if err := json.Unmarshal(content, &previous); err != nil {
		return fmt.Errorf("The preparation record is unreadable. Existing files and saves were preserved: %w", err)
	}
	if previous.Build != GameBuild || !prepareValidHash(previous.PluginSHA256) {
		return errors.New("This prepared copy uses a different game version or has an invalid mod record. Saved matches are preserved; use the matching launcher or a fresh data folder")
	}
	var updates []prepareUpdate
	for _, role := range []string{"host", "client"} {
		game := filepath.Join(data, role, "game")
		plugin := filepath.Join(game, "BepInEx", "plugins", "EngineProbe.dll")
		unity := filepath.Join(game, "BepInEx", "unity-libs", prepareUnityVersion+".zip")
		config := filepath.Join(game, "BepInEx", "config", "BepInEx.cfg")
		for _, name := range []string{plugin, unity, config, filepath.Join(game, "Root.exe")} {
			if err := prepareNoSymlinks(data, name); err != nil {
				return err
			}
		}
		if info, err := os.Stat(filepath.Join(game, "Root.exe")); err != nil || !info.Mode().IsRegular() {
			return errors.New("A prepared Root game copy is missing. Existing files and saves were preserved; use a fresh launcher data folder")
		}
		installed, err := os.ReadFile(plugin)
		if err != nil || prepareDigest(installed) != strings.ToLower(previous.PluginSHA256) && prepareDigest(installed) != bundle.manifest.PluginSHA256 {
			return errors.New("Prepared mod files changed outside this launcher. They were preserved; restore the previous mod files before trying again")
		}
		if prepareDigest(installed) != bundle.manifest.PluginSHA256 {
			updates = append(updates, prepareUpdate{target: plugin, content: bundle.plugin})
		}
		cached, err := os.ReadFile(unity)
		if errors.Is(err, os.ErrNotExist) {
			updates = append(updates, prepareUpdate{target: unity, content: bundle.unity})
		} else if err != nil {
			return err
		} else if prepareDigest(cached) != strings.ToLower(bundle.manifest.UnitySHA256) {
			return errors.New("Cached Unity libraries differ from this launcher. They were preserved; restore the original cache or use a fresh launcher data folder")
		}
		oldConfig, err := os.ReadFile(config)
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
		newConfig := prepareOfflineConfig(oldConfig)
		if !bytes.Equal(oldConfig, newConfig) {
			updates = append(updates, prepareUpdate{target: config, content: newConfig})
		}
	}
	metadata, err := json.Marshal(bundle.manifest.preparedMetadata)
	if err != nil {
		return err
	}
	updates = append(updates, prepareUpdate{target: marker, content: metadata})
	stage, err := prepareStage(data)
	if err != nil {
		return err
	}
	cleanup := true
	defer func() {
		if cleanup {
			_ = os.RemoveAll(stage)
		}
	}()
	for index := range updates {
		item := &updates[index]
		item.staged = filepath.Join(stage, fmt.Sprintf("new-%d", index))
		item.backup = filepath.Join(stage, fmt.Sprintf("old-%d", index))
		if err := os.WriteFile(item.staged, item.content, 0600); err != nil {
			return err
		}
		if err := os.MkdirAll(filepath.Dir(item.target), 0755); err != nil {
			return err
		}
	}
	if err := validateGame(inst.Game); err != nil {
		return err
	}
	for index := range updates {
		item := &updates[index]
		if _, err := os.Lstat(item.target); err == nil {
			err = os.Rename(item.target, item.backup)
			if err != nil {
				return prepareRollback(updates[:index], stage, &cleanup, err)
			}
			item.hadOriginal = true
		} else if !errors.Is(err, os.ErrNotExist) {
			return prepareRollback(updates[:index], stage, &cleanup, err)
		}
		if err := os.Rename(item.staged, item.target); err != nil {
			return prepareRollback(updates[:index+1], stage, &cleanup, err)
		}
		item.installed = true
	}
	prepareProgress(progress, "Already prepared. Ready to host or join.")
	return nil
}

func prepareRollback(updates []prepareUpdate, stage string, cleanup *bool, cause error) error {
	var failures []error
	for index := len(updates) - 1; index >= 0; index-- {
		item := updates[index]
		if item.installed {
			if err := os.Remove(item.target); err != nil && !errors.Is(err, os.ErrNotExist) {
				failures = append(failures, err)
			}
		}
		if item.hadOriginal {
			if err := os.Rename(item.backup, item.target); err != nil {
				failures = append(failures, err)
			}
		}
	}
	if len(failures) > 0 {
		*cleanup = false
		return errors.Join(fmt.Errorf("Update and recovery failed. Saved matches are untouched; original mod files remain in %s. Restore those files before launching: %w", stage, cause), errors.Join(failures...))
	}
	return fmt.Errorf("The update failed. Previous game files and saves were preserved; retry preparation: %w", cause)
}

func prepareStage(data string) (string, error) {
	if err := os.MkdirAll(data, 0700); err != nil {
		return "", err
	}
	stage := filepath.Join(data, "preparing")
	if err := os.Mkdir(stage, 0700); err != nil {
		return "", fmt.Errorf("Could not create preparation staging. If an interrupted preparing folder remains, keep any recovery backups and choose a fresh data folder: %w", err)
	}
	return stage, nil
}

func prepareGameSize(game string) (uint64, error) {
	var total uint64
	err := prepareWalkGame(game, func(name string, entry fs.DirEntry) error {
		if entry.IsDir() {
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if info.Size() < 0 || uint64(info.Size()) > (^uint64(0))-total {
			return errors.New("Game file sizes exceed the supported range")
		}
		total += uint64(info.Size())
		return nil
	})
	return total, err
}

func prepareCopyGame(source, destination string) error {
	return prepareWalkGame(source, func(name string, entry fs.DirEntry) error {
		relative, err := filepath.Rel(source, name)
		if err != nil {
			return err
		}
		target := filepath.Join(destination, relative)
		if entry.IsDir() {
			return os.MkdirAll(target, 0755)
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		input, err := os.Open(name)
		if err != nil {
			return err
		}
		err = prepareWriteReader(target, input, info.Mode().Perm())
		return errors.Join(err, input.Close())
	})
}

func prepareWalkGame(root string, visit func(string, fs.DirEntry) error) error {
	return filepath.WalkDir(root, func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if name != root {
			switch strings.ToLower(entry.Name()) {
			case "bepinex", "dotnet", "winhttp.dll", "doorstop_config.ini":
				if entry.IsDir() {
					return filepath.SkipDir
				}
				return nil
			}
		}
		if !entry.IsDir() && !entry.Type().IsRegular() {
			return fmt.Errorf("Root contains a symbolic link or special file at %s. The Steam installation was preserved", name)
		}
		return visit(name, entry)
	})
}

func prepareWriteReader(target string, source io.Reader, mode os.FileMode) error {
	output, err := os.OpenFile(target, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, mode)
	if err != nil {
		return err
	}
	_, copyErr := io.Copy(output, source)
	return errors.Join(copyErr, output.Close())
}

func prepareOutsideGame(game, data string) error {
	gamePath, err := filepath.EvalSymlinks(game)
	if err != nil {
		return err
	}
	parent, err := prepareExistingDirectory(data)
	if err != nil {
		return err
	}
	resolvedParent, err := filepath.EvalSymlinks(parent)
	if err != nil {
		return err
	}
	remainder, err := filepath.Rel(parent, data)
	if err != nil {
		return err
	}
	resolvedData := filepath.Join(resolvedParent, remainder)
	return prepareCheckOutsideGame(gamePath, resolvedData)
}

// Both paths must already have existing symlinks resolved. Separate Windows
// volumes cannot contain one another, and filepath.Rel rejects that pair.
func prepareCheckOutsideGame(gamePath, resolvedData string) error {
	if !strings.EqualFold(filepath.VolumeName(gamePath), filepath.VolumeName(resolvedData)) {
		return nil
	}
	relative, err := filepath.Rel(gamePath, resolvedData)
	if err != nil {
		return err
	}
	if relative == "." || relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return errors.New("The launcher data folder cannot be inside the Steam game installation. Choose a separate folder; Steam files were not changed")
	}
	return nil
}

func prepareExistingDirectory(name string) (string, error) {
	current, err := filepath.Abs(name)
	if err != nil {
		return "", err
	}
	for {
		info, err := os.Stat(current)
		if err == nil {
			if !info.IsDir() {
				return "", fmt.Errorf("%s is not a directory", current)
			}
			return current, nil
		}
		if !errors.Is(err, os.ErrNotExist) || filepath.Dir(current) == current {
			return "", err
		}
		current = filepath.Dir(current)
	}
}

func prepareNoSymlinks(root, target string) error {
	for current := target; ; current = filepath.Dir(current) {
		info, err := os.Lstat(current)
		if err == nil && info.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("Prepared path %s is a symbolic link. Existing files were preserved; use a fresh launcher data folder", current)
		}
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
		if current == root || filepath.Dir(current) == current {
			return nil
		}
	}
}

func prepareDigest(content []byte) string {
	digest := sha256.Sum256(content)
	return hex.EncodeToString(digest[:])
}

func prepareValidHash(value string) bool {
	decoded, err := hex.DecodeString(value)
	return err == nil && len(decoded) == sha256.Size
}

func prepareProgress(progress func(string), message string) {
	if progress != nil {
		progress(message)
	}
}
