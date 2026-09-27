//go:build windows

package main

import (
	"context"
	"sync"
	"testing"
	"time"
)

func TestWindowsNativeProgressLifecycle(t *testing.T) {
	// Run under the dedicated test desktop. Calling the native loop directly
	// ensures a broken window cannot pass by taking OpenProgress's log fallback.
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	updates := make(chan string)
	stop, finished := make(chan struct{}), make(chan struct{})
	ready := make(chan error, 1)
	var once sync.Once
	closeWindow := func() { once.Do(func() { close(stop) }) }
	defer closeWindow()
	go func() {
		defer close(finished)
		runProgressWindow(updates, stop, ready)
	}()
	select {
	case err := <-ready:
		if err != nil {
			t.Fatalf("native progress window failed to initialize: %v", err)
		}
	case <-ctx.Done():
		t.Fatal("native progress window did not initialize within five seconds")
	}
	select {
	case updates <- "Finishing setup (1 of 2). Test status update.":
	case <-finished:
		t.Fatal("native progress window closed before receiving its status update")
	case <-ctx.Done():
		t.Fatal("native progress window did not receive its status update within five seconds")
	}
	closeWindow()
	closeWindow() // Closing the owner repeatedly must remain harmless.
	select {
	case <-finished:
	case <-ctx.Done():
		t.Fatal("native progress window did not finish cleanup within five seconds")
	}
}
