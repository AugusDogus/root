//go:build windows

package main

import (
	"fmt"
	"os"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

var progressUser32 = syscall.NewLazyDLL("user32.dll")
var progressCreateWindow = progressUser32.NewProc("CreateWindowExW")
var progressDestroyWindow = progressUser32.NewProc("DestroyWindow")
var progressSendMessage = progressUser32.NewProc("SendMessageW")
var progressPeekMessage = progressUser32.NewProc("PeekMessageW")
var progressTranslateMessage = progressUser32.NewProc("TranslateMessage")
var progressDispatchMessage = progressUser32.NewProc("DispatchMessageW")
var progressSetText = progressUser32.NewProc("SetWindowTextW")
var progressIsWindow = progressUser32.NewProc("IsWindow")

// Windows MSG, including pointer-sized WPARAM and LPARAM on both architectures.
type progressMessage struct {
	Window  uintptr
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	X, Y    int32
	Private uint32
}

func progressWindow(class, text string, style uint32, x, y, width, height int, parent uintptr) (uintptr, error) {
	className, err := syscall.UTF16PtrFromString(class)
	if err != nil {
		return 0, err
	}
	caption, err := syscall.UTF16PtrFromString(text)
	if err != nil {
		return 0, err
	}
	window, _, callErr := progressCreateWindow.Call(0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(caption)),
		uintptr(style), uintptr(x), uintptr(y), uintptr(width), uintptr(height), parent, 0, 0, 0)
	if window == 0 {
		return 0, callErr
	}
	return window, nil
}

func runProgressWindow(updates <-chan string, stop <-chan struct{}, ready chan<- error) {
	// Win32 windows and their message pump must stay on their creating thread.
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	const childVisible = 0x40000000 | 0x10000000
	window, err := progressWindow("STATIC", "Root Six Player", 0x00C00000|0x00080000|0x10000000,
		int(-2147483648), int(-2147483648), 480, 150, 0)
	if err != nil {
		ready <- err
		return
	}
	defer progressDestroyWindow.Call(window)
	text, err := progressWindow("STATIC", "Preparing Root...", childVisible, 24, 20, 425, 48, window)
	if err != nil {
		ready <- err
		return
	}
	font, _, _ := syscall.NewLazyDLL("gdi32.dll").NewProc("GetStockObject").Call(17) // DEFAULT_GUI_FONT
	progressSendMessage.Call(text, 0x0030, font, 1)                                  // WM_SETFONT
	controls := struct{ Size, Classes uint32 }{8, 0x20}                              // ICC_PROGRESS_CLASS
	initialized, _, _ := syscall.NewLazyDLL("comctl32.dll").NewProc("InitCommonControlsEx").Call(uintptr(unsafe.Pointer(&controls)))
	if initialized != 0 {
		if bar, err := progressWindow("msctls_progress32", "", childVisible|0x08, 24, 80, 425, 16, window); err == nil {
			progressSendMessage.Call(bar, 0x040A, 1, 30) // PBM_SETMARQUEE
		}
	}
	ready <- nil
	tick := time.NewTicker(30 * time.Millisecond)
	defer tick.Stop()
	for {
		var message progressMessage
		for {
			found, _, _ := progressPeekMessage.Call(uintptr(unsafe.Pointer(&message)), 0, 0, 0, 1) // PM_REMOVE
			if found == 0 {
				break
			}
			if message.Message == 0x0012 { // WM_QUIT
				return
			}
			progressTranslateMessage.Call(uintptr(unsafe.Pointer(&message)))
			progressDispatchMessage.Call(uintptr(unsafe.Pointer(&message)))
		}
		if exists, _, _ := progressIsWindow.Call(window); exists == 0 {
			return
		}
		select {
		case <-stop:
			return
		case message := <-updates:
			caption, err := syscall.UTF16PtrFromString(strings.ReplaceAll(message, "\x00", ""))
			if err == nil {
				progressSetText.Call(text, uintptr(unsafe.Pointer(caption)))
			}
		case <-tick.C:
		}
	}
}

func OpenProgress() (func(string), func()) {
	updates := make(chan string, 1)
	stop, finished := make(chan struct{}), make(chan struct{})
	ready := make(chan error, 1)
	go func() {
		defer close(finished)
		runProgressWindow(updates, stop, ready)
	}()
	if err := <-ready; err != nil {
		fmt.Fprintln(os.Stderr, "Startup progress is unavailable:", err)
		<-finished
		return func(message string) { fmt.Fprintln(os.Stderr, message) }, func() {}
	}
	var mu sync.Mutex
	var once sync.Once
	update := func(message string) {
		mu.Lock()
		defer mu.Unlock()
		select {
		case <-finished:
			return
		default:
		}
		// Keep only the latest status. Preparation must never block on the UI.
		select {
		case <-updates:
		default:
		}
		select {
		case updates <- message:
		default:
		}
	}
	return update, func() { once.Do(func() { close(stop) }); <-finished }
}
