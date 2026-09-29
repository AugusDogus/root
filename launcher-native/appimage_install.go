package main

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
)

// APPIMAGE alone can be inherited from another desktop application. Only use
// it when this process is the launcher inside the corresponding AppDir.
func runningAppImage() (string, error) {
	if runtime.GOOS != "linux" {
		return "", nil
	}
	executable, err := os.Executable()
	if err != nil {
		return "", err
	}
	return identifyAppImage(executable, os.Getenv("APPDIR"), os.Getenv("APPIMAGE"))
}

func identifyAppImage(executable, appdir, appimage string) (string, error) {
	if !filepath.IsAbs(appdir) || !filepath.IsAbs(appimage) {
		return "", nil
	}
	inside, err := filepath.EvalSymlinks(filepath.Join(appdir, "usr/bin/RootSixPlayer"))
	if err != nil || inside != executable {
		return "", nil
	}
	info, err := os.Lstat(appimage)
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() {
		return "", fmt.Errorf("the original AppImage is no longer a regular file; download the new version manually")
	}
	if err := validateAppImage(appimage); err != nil {
		return "", err
	}
	return appimage, nil
}

func replaceAppImage(source, target, expected string) error {
	before, err := os.Lstat(target)
	if err != nil {
		return err
	}
	if !before.Mode().IsRegular() {
		return fmt.Errorf("AppImage update target must be a regular file")
	}
	input, err := os.Open(source)
	if err != nil {
		return err
	}
	defer input.Close()
	output, err := os.CreateTemp(filepath.Dir(target), ".root-update-*.AppImage")
	if err != nil {
		return fmt.Errorf("could not stage an update beside %s; move the AppImage to a writable folder and retry: %w", target, err)
	}
	defer os.Remove(output.Name())
	written, copyErr := io.Copy(output, io.LimitReader(input, maxUpdateSize+1))
	err = errors.Join(copyErr, output.Chmod(before.Mode().Perm()), output.Sync(), output.Close())
	if err != nil {
		return err
	}
	if written > maxUpdateSize {
		return fmt.Errorf("AppImage update exceeds 320 MiB")
	}
	digest, err := launcherFileDigest(output.Name())
	if err != nil {
		return err
	}
	if digest != expected {
		return fmt.Errorf("staged AppImage failed its checksum check; the original file is unchanged")
	}
	if err := validateAppImage(output.Name()); err != nil {
		return err
	}
	after, err := os.Lstat(target)
	if err != nil || !os.SameFile(before, after) {
		return fmt.Errorf("the AppImage changed during the update; reopen it before retrying")
	}
	// Rename within the same directory atomically swaps the pathname. Processes
	// using the old image keep their mapped inode until they exit.
	return os.Rename(output.Name(), target)
}
