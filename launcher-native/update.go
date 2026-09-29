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

// Linux replaces the AppImage; Windows hands off to the downloaded installer.
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
	name := "RootSixPlayer-" + version + "-Setup.exe"
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
	target := filepath.Join(root, "RootSixPlayerSetup.exe")
	if err := os.Rename(archive, target); err != nil {
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
