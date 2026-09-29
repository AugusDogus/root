// A harmless launcher used by the Windows installer regression.
package main

import (
	"fmt"
	"os"
	"time"
)

var version = "fixture"

func main() {
	if len(os.Args) == 4 && os.Args[1] == "--hold" {
		if err := os.WriteFile(os.Args[2], []byte("ready"), 0600); err != nil {
			panic(err)
		}
		deadline := time.Now().Add(45 * time.Second)
		for time.Now().Before(deadline) {
			if _, err := os.Stat(os.Args[3]); err == nil {
				return
			}
			time.Sleep(50 * time.Millisecond)
		}
		os.Exit(1)
	}
	if path := os.Getenv("ROOT_INSTALLER_SMOKE_LOG"); path != "" {
		if err := os.WriteFile(path, []byte(version), 0600); err != nil {
			panic(err)
		}
	}
	fmt.Println(version)
}
