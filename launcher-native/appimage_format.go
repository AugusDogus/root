package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
)

func validateAppImage(path string) error {
	file, err := os.Open(path)
	if err != nil {
		return err
	}
	defer file.Close()
	var header [64]byte
	if _, err := io.ReadFull(file, header[:]); err != nil {
		return err
	}
	if string(header[:7]) != "\x7fELF\x02\x01\x01" || string(header[8:11]) != "AI\x02" || binary.LittleEndian.Uint16(header[18:20]) != 62 {
		return fmt.Errorf("download is not an x86_64 type-2 AppImage")
	}
	return nil
}
