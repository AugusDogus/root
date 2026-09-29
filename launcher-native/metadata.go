package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

const AppID = "965580"
const GameBuild = "22238765"
const Version = "0.7.7"

func writeJSON[T any](path string, value T) error {
	content, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return atomicWrite(path, content, 0600)
}

func atomicWrite(path string, content []byte, mode os.FileMode) error {
	file, err := os.CreateTemp(filepath.Dir(path), ".root-write-*")
	if err != nil {
		return err
	}
	name := file.Name()
	defer os.Remove(name)
	if err = file.Chmod(mode); err == nil {
		_, err = file.Write(content)
	}
	if err == nil {
		err = file.Sync()
	}
	closeErr := file.Close()
	if err == nil {
		err = closeErr
	}
	if err != nil {
		return err
	}
	return os.Rename(name, path)
}

func readJSON[T any](path string, value *T) error {
	file, err := os.Open(path)
	if err != nil {
		return err
	}
	defer file.Close()
	reader := io.LimitReader(file, 1024*1024+1)
	content, err := io.ReadAll(reader)
	if err != nil {
		return err
	}
	if len(content) > 1024*1024 {
		return fmt.Errorf("%s exceeds the supported configuration size", filepath.Base(path))
	}
	return json.Unmarshal(content, value)
}
