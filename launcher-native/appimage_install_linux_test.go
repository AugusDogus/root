package main

import (
	"bytes"
	"context"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestAppImageReplacementPreservesRunningFileAndRejectsCorruption(t *testing.T) {
	root := t.TempDir()
	target, source := filepath.Join(root, "Root.AppImage"), filepath.Join(root, "download")
	old, updated := appImageFixture("old"), appImageFixture("new")
	prepareTestWrite(t, target, old)
	prepareTestWrite(t, source, updated)
	if err := os.Chmod(target, 0751); err != nil {
		t.Fatal(err)
	}
	running, err := os.Open(target)
	if err != nil {
		t.Fatal(err)
	}
	defer running.Close()
	if err := replaceAppImage(source, target, "wrong"); err == nil {
		t.Fatal("bad checksum replaced the installed image")
	}
	unchanged, err := os.ReadFile(target)
	if err != nil || !bytes.Equal(unchanged, old) {
		t.Fatal("failed update changed the installed image")
	}
	if err := replaceAppImage(source, target, prepareDigest(updated)); err != nil {
		t.Fatal(err)
	}
	installed, err := os.ReadFile(target)
	if err != nil || !bytes.Equal(installed, updated) {
		t.Fatal("replacement image was not installed")
	}
	stillRunning, err := io.ReadAll(running)
	if err != nil || !bytes.Equal(stillRunning, old) {
		t.Fatal("replacement changed the old process's open file")
	}
	info, err := os.Stat(target)
	if err != nil || info.Mode().Perm() != 0751 {
		t.Fatal("replacement lost the original permissions")
	}
	entries, err := os.ReadDir(root)
	if err != nil || len(entries) != 2 {
		t.Fatal("update left temporary files behind")
	}
}

func TestInheritedAppImageEnvironmentIsIgnored(t *testing.T) {
	root := t.TempDir()
	appdir := filepath.Join(root, "appdir")
	inside := filepath.Join(appdir, "usr/bin/RootSixPlayer")
	image := filepath.Join(root, "Root.AppImage")
	prepareTestWrite(t, inside, []byte("launcher"))
	prepareTestWrite(t, image, appImageFixture("payload"))
	if path, err := identifyAppImage(filepath.Join(root, "standalone"), appdir, image); err != nil || path != "" {
		t.Fatalf("inherited AppImage was selected for replacement: %s, %v", path, err)
	}
	if path, err := identifyAppImage(inside, appdir, image); err != nil || path != image {
		t.Fatalf("running AppImage was not identified: %s, %v", path, err)
	}
}

func TestLinuxUpdateReplacesImageWithoutCreatingExecutableCache(t *testing.T) {
	root := t.TempDir()
	appdir := filepath.Join(root, "appdir")
	inside := filepath.Join(appdir, "usr/bin/RootSixPlayer")
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Dir(inside), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(executable, inside); err != nil {
		t.Fatal(err)
	}
	installed := filepath.Join(root, "My Root.AppImage")
	prepareTestWrite(t, installed, appImageFixture("old launcher"))
	t.Setenv("APPDIR", appdir)
	t.Setenv("APPIMAGE", installed)
	client, responses, url := updateFixture(t, "linux")
	data := t.TempDir()
	// An old standalone cache must never intercept AppImage updates.
	prepareTestWrite(t, filepath.Join(data, "launcher-updates/current.json"), []byte("obsolete cache"))
	path, err := launcherUpdateWithClient(context.Background(), client, data, func(string) {})
	if err != nil || path != installed {
		t.Fatalf("image update: %s, %v", path, err)
	}
	got, err := os.ReadFile(installed)
	if err != nil || string(got) != responses[url] {
		t.Fatal("downloaded image was not installed")
	}
	entries, err := os.ReadDir(filepath.Join(data, "launcher-updates"))
	if err != nil || len(entries) != 1 || entries[0].Name() != "current.json" {
		t.Fatalf("Linux update created a cache or left partial files: %v, %v", entries, err)
	}
}

func TestStandaloneLinuxUpdateRequiresAppImage(t *testing.T) {
	t.Setenv("APPDIR", "")
	t.Setenv("APPIMAGE", "")
	client, _, _ := updateFixture(t, "linux")
	path, err := fetchLauncherUpdate(context.Background(), client, t.TempDir(), "0.7.4", "linux", func(string) {})
	if err == nil || path != "" {
		t.Fatal("standalone Linux update was accepted")
	}
}

func TestPackagedAppImageReplacement(t *testing.T) {
	artifact := os.Getenv("ROOT_APPIMAGE_TEST")
	if artifact == "" {
		t.Skip("set ROOT_APPIMAGE_TEST to exercise the packaged artifact")
	}
	content, err := os.ReadFile(artifact)
	if err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "Root.AppImage")
	prepareTestWrite(t, target, appImageFixture("old image"))
	if err := os.Chmod(target, 0755); err != nil {
		t.Fatal(err)
	}
	if err := replaceAppImage(artifact, target, prepareDigest(content)); err != nil {
		t.Fatal(err)
	}
	output, err := exec.Command(target, "--appimage-extract-and-run", "--headless", "--version").Output()
	if err != nil || string(output) != Version+"\n" {
		t.Fatalf("replaced AppImage failed version check: %q, %v", output, err)
	}
}
