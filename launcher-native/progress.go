package main

import (
	"bufio"
	"fmt"
	"image"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"

	"golang.org/x/exp/shiny/driver"
	"golang.org/x/exp/shiny/screen"
	"golang.org/x/mobile/event/lifecycle"
	"golang.org/x/mobile/event/paint"
	"golang.org/x/mobile/event/size"
)

type progressStatus string
type progressStop struct{}
type progressPulse struct{}

// The window runs in a child of the same executable so the native event loop
// owns its main thread on both platforms. It has no game or network connection.
func progressChild(game, steam string) {
	updates := make(chan string, 1)
	stop := make(chan struct{})
	go func() {
		defer close(stop)
		scanner := bufio.NewScanner(os.Stdin)
		for scanner.Scan() {
			updates <- scanner.Text()
		}
	}()
	ready := make(chan error, 1)
	go func() {
		if err := <-ready; err != nil {
			fmt.Fprintln(os.Stderr, "Startup window:", err)
		}
	}()
	runProgressWindow(loadLauncherTheme(game, steam), updates, stop, ready)
}

func runProgressWindow(theme launcherTheme, updates <-chan string, stop <-chan struct{}, ready chan<- error) {
	driver.Main(func(display screen.Screen) {
		window, err := display.NewWindow(&screen.NewWindowOptions{Width: 800, Height: 500, Title: "Root Six Player"})
		if err != nil {
			ready <- err
			return
		}
		defer window.Release()
		done := make(chan struct{})
		defer close(done)
		go func() {
			ticker := time.NewTicker(200 * time.Millisecond)
			defer ticker.Stop()
			for {
				select {
				case <-done:
					return
				case <-stop:
					window.Send(progressStop{})
					return
				case message := <-updates:
					window.Send(progressStatus(message))
				case <-ticker.C:
					window.Send(progressPulse{})
				}
			}
		}()
		ready <- nil
		status := "Preparing Root..."
		pulse := 0
		dimensions := image.Pt(800, 500)
		var buffer screen.Buffer
		defer func() {
			if buffer != nil {
				buffer.Release()
			}
		}()
		for {
			switch event := window.NextEvent().(type) {
			case lifecycle.Event:
				if event.To == lifecycle.StageDead {
					return
				}
			case progressStop:
				return
			case progressStatus:
				status = string(event)
			case size.Event:
				dimensions = event.Size()
			case paint.Event:
			case progressPulse:
				pulse = (pulse + 1) % 24
			default:
				continue
			}
			if dimensions.X < 1 || dimensions.Y < 1 || dimensions.X > 4096 || dimensions.Y > 4096 {
				continue
			}
			if buffer == nil || buffer.Size() != dimensions {
				if buffer != nil {
					buffer.Release()
				}
				buffer, err = display.NewBuffer(dimensions)
				if err != nil {
					fmt.Fprintln(os.Stderr, "Startup window buffer:", err)
					return
				}
			}
			theme.paint(buffer.RGBA(), status, pulse)
			window.Upload(image.Point{}, buffer, buffer.Bounds())
			window.Publish()
		}
	})
}

func OpenProgress(inst Installation) (func(string), func()) {
	fallback := func(message string) { fmt.Fprintln(os.Stderr, message) }
	executable, err := os.Executable()
	if err != nil {
		return fallback, func() {}
	}
	cmd := exec.Command(executable, "--launcher-progress", inst.Game, inst.Steam)
	cmd.Stderr = os.Stderr
	pipe, err := cmd.StdinPipe()
	if err != nil {
		return fallback, func() {}
	}
	if err := cmd.Start(); err != nil {
		pipe.Close()
		return fallback, func() {}
	}
	done := make(chan struct{})
	go func() { _ = cmd.Wait(); close(done) }()
	var mu sync.Mutex
	closed := false
	return func(message string) {
			mu.Lock()
			defer mu.Unlock()
			if !closed {
				if _, err := fmt.Fprintln(pipe, strings.ReplaceAll(message, "\n", " ")); err != nil {
					fallback(message)
				}
			}
		}, func() {
			mu.Lock()
			if closed {
				mu.Unlock()
				return
			}
			closed = true
			pipe.Close()
			mu.Unlock()
			select {
			case <-done:
			case <-time.After(2 * time.Second):
				_ = cmd.Process.Kill()
				<-done
			}
		}
}
