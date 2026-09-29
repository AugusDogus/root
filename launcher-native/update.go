package main

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

type launcherHandoff struct{ executable string }

func (handoff *launcherHandoff) Error() string { return "opening the updated launcher" }

type cachedLauncher struct {
	Version string `json:"version"`
	SHA256  string `json:"sha256"`
}

func launcherUpdatePath(data, version string) string {
	return filepath.Join(data, "launcher-updates", version, "RootSixPlayer.exe")
}

func launcherFileDigest(path string) (string, error) {
	file, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() || info.Size() < 1 || info.Size() > maxUpdateSize {
		return "", fmt.Errorf("launcher is not a regular file under 320 MiB")
	}
	hash := sha256.New()
	if _, err := io.Copy(hash, io.LimitReader(file, maxUpdateSize+1)); err != nil {
		return "", err
	}
	return fmt.Sprintf("%x", hash.Sum(nil)), nil
}

func cachedLauncherUpdate(data, current string) (string, error) {
	var cached cachedLauncher
	err := readJSON(filepath.Join(data, "launcher-updates", "current.json"), &cached)
	if errors.Is(err, os.ErrNotExist) {
		return "", nil
	}
	if err != nil {
		return "", err
	}
	version, err := parseReleaseVersion(cached.Version)
	if err != nil {
		return "", err
	}
	running, err := parseReleaseVersion(current)
	if err != nil {
		return "", err
	}
	if !version.newerThan(running) {
		return "", nil
	}
	path := launcherUpdatePath(data, cached.Version)
	digest, err := launcherFileDigest(path)
	if err != nil {
		return "", err
	}
	if digest != cached.SHA256 {
		return "", fmt.Errorf("cached launcher %s failed its checksum check", cached.Version)
	}
	return path, nil
}

// Linux replaces the running AppImage. Windows locks running EXEs, so it
// stages the replacement in a versioned directory.
func fetchLauncherUpdate(ctx context.Context, client *http.Client, data, current, platform string, progress func(string)) (string, error) {
	release, err := latestLauncherRelease(ctx, client)
	if err != nil {
		return "", err
	}
	version := strings.TrimPrefix(release.Tag, "v")
	next, err := parseReleaseVersion(version)
	if err != nil {
		return "", err
	}
	running, err := parseReleaseVersion(current)
	if err != nil {
		return "", err
	}
	if !next.newerThan(running) {
		return "", nil
	}
	name := "RootSixPlayer.exe"
	if platform == "linux" {
		name = "RootSixPlayer-" + version + "-x86_64.AppImage"
	} else if platform != "windows" {
		return "", fmt.Errorf("automatic updates are not available for %s", platform)
	}
	asset, err := release.asset(name, maxUpdateSize)
	if err != nil {
		return "", err
	}
	checksums, err := release.asset("SHA256SUMS", 64<<10)
	if err != nil {
		return "", err
	}
	var sums strings.Builder
	if err := downloadUpdate(ctx, client, checksums.URL, &sums, 64<<10); err != nil {
		return "", err
	}
	expected, err := releaseChecksum(sums.String(), name)
	if err != nil {
		return "", err
	}
	progress("Downloading Root Six Player " + version + "...")
	root := filepath.Join(data, "launcher-updates")
	if err := os.MkdirAll(root, 0700); err != nil {
		return "", err
	}
	stage, err := os.MkdirTemp(root, ".download-")
	if err != nil {
		return "", err
	}
	defer os.RemoveAll(stage)
	archive := filepath.Join(stage, "download")
	file, err := os.OpenFile(archive, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return "", err
	}
	err = downloadUpdate(ctx, client, asset.URL, file, asset.Size)
	err = errors.Join(err, file.Close())
	if err != nil {
		return "", err
	}
	digest, err := launcherFileDigest(archive)
	if err != nil {
		return "", err
	}
	if digest != expected {
		return "", fmt.Errorf("downloaded launcher failed its release checksum check")
	}
	progress("Installing Root Six Player " + version + "...")
	if platform == "linux" {
		installed, err := runningAppImage()
		if err != nil {
			return "", err
		}
		if installed == "" {
			return "", fmt.Errorf("Linux updates require the AppImage launcher. Download it from GitHub Releases and open it")
		}
		if err := replaceAppImage(archive, installed, expected); err != nil {
			return "", err
		}
		return installed, nil
	}
	target := launcherUpdatePath(data, version)
	executable := archive
	if err := os.Chmod(executable, 0700); err != nil {
		return "", err
	}
	if err := os.MkdirAll(filepath.Dir(target), 0700); err != nil {
		return "", err
	}
	if err := os.Rename(executable, target); err != nil {
		return "", err
	}
	if err := writeJSON(filepath.Join(root, "current.json"), cachedLauncher{version, digest}); err != nil {
		return "", err
	}
	return target, nil
}

func launcherUpdate(ctx context.Context, data string, progress func(string)) (string, error) {
	if runtime.GOARCH != "amd64" {
		return "", nil
	}
	return launcherUpdateWithClient(ctx, updateHTTPClient(), data, progress)
}

func launcherUpdateWithClient(ctx context.Context, client *http.Client, data string, progress func(string)) (string, error) {
	if runtime.GOOS == "windows" {
		cached, err := cachedLauncherUpdate(data, Version)
		if err != nil {
			return "", fmt.Errorf("The saved launcher update could not be verified. Delete %s, then open a freshly downloaded launcher. Saved matches are outside that folder and are unchanged: %w", filepath.Join(data, "launcher-updates"), err)
		}
		if cached != "" {
			return cached, nil
		}
	}
	progress("Checking for updates...")
	updated, err := fetchLauncherUpdate(ctx, client, data, Version, runtime.GOOS, progress)
	if err != nil {
		// A private repository, rate limit, or interrupted download must not
		// prevent an already installed version from starting.
		progress("Update unavailable. Opening the installed version.")
		log.Print("Launcher update check: ", err)
		if ctx.Err() != nil {
			return "", ctx.Err()
		}
		return "", nil
	}
	return updated, nil
}
