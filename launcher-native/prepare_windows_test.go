//go:build windows

package main

import "testing"

func TestPreparedDataOutsideWindowsGame(t *testing.T) {
	for _, test := range []struct {
		name, game, data string
		inside           bool
	}{
		{"separate drives", `D:\SteamLibrary\steamapps\common\Root`, `C:\Users\Friend\AppData\Local\root-six-player`, false},
		{"same drive outside", `D:\SteamLibrary\steamapps\common\Root`, `d:\root-six-player`, false},
		{"same drive inside", `D:\SteamLibrary\steamapps\common\Root`, `d:\SteamLibrary\steamapps\common\Root\mod`, true},
		{"same directory", `D:\SteamLibrary\steamapps\common\Root`, `d:\SteamLibrary\steamapps\common\Root`, true},
		{"different shares", `\\server\games\Root`, `\\server\userdata\root-six-player`, false},
		{"same share inside", `\\server\games\Root`, `\\SERVER\GAMES\Root\mod`, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			err := prepareCheckOutsideGame(test.game, test.data)
			if (err != nil) != test.inside {
				t.Fatalf("containment error = %v, want rejected = %v", err, test.inside)
			}
		})
	}
}
