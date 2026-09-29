package main

import (
	"os"
	"os/exec"
	"strconv"
)

// The installer runs after main returns and releases the launcher executable.
// Launcher flags belong to the AppImage, not to the Windows installer.
func launcherUpdateCommand(executable, platform string, parent int, arguments []string) *exec.Cmd {
	if platform == "windows" {
		return exec.Command(executable, "/S", "/UPDATE", "/PARENT="+strconv.Itoa(parent))
	}
	command := exec.Command(executable, arguments...)
	command.Env = replaceEnvironment(os.Environ(), map[string]string{"APPIMAGE_EXTRACT_AND_RUN": "1"})
	return command
}
