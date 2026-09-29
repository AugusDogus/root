package main

import (
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"
)

func TestRootArtworkSupportsSteamCacheLayouts(t *testing.T) {
	for _, directory := range []string{AppID, filepath.Join(AppID, "content-hash"), "."} {
		t.Run(directory, func(t *testing.T) {
			cache := t.TempDir()
			name := "logo.png"
			if directory == "." {
				name = AppID + "_" + name
			}
			path := filepath.Join(cache, directory, name)
			if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
				t.Fatal(err)
			}
			file, err := os.Create(path)
			if err != nil {
				t.Fatal(err)
			}
			art := image.NewRGBA(image.Rect(0, 0, 2, 2))
			art.Set(0, 0, color.White)
			if err := png.Encode(file, art); err != nil {
				t.Fatal(err)
			}
			if err := file.Close(); err != nil {
				t.Fatal(err)
			}
			loaded := cachedRootArt(cache, "logo.png")
			if loaded == nil || loaded.Bounds() != art.Bounds() {
				t.Fatal("Root artwork was not found in Steam's cache")
			}
		})
	}
}

func TestLauncherRendersWithoutGameArt(t *testing.T) {
	missing := t.TempDir()
	theme := loadLauncherTheme(missing, missing)
	if theme.font == nil || theme.forest != nil || theme.logo != nil {
		t.Fatal("missing game files must select the bundled font and plain background")
	}
	for _, dimensions := range []image.Point{{800, 500}, {1200, 750}, {480, 300}} {
		theme.paint(image.NewRGBA(image.Rectangle{Max: dimensions}), "Finishing setup (1 of 2). This can take a few minutes.", 0)
	}
}

func TestRootMenuFontRejectsChangedGameBytes(t *testing.T) {
	game := t.TempDir()
	directory := filepath.Join(game, "Root_Data")
	if err := os.Mkdir(directory, 0700); err != nil {
		t.Fatal(err)
	}
	file, err := os.Create(filepath.Join(directory, "resources.assets"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := file.WriteAt(make([]byte, 118812), 29078616); err != nil {
		t.Fatal(err)
	}
	file.Close()
	if _, err := rootMenuFont(game); err == nil {
		t.Fatal("changed font bytes must not be parsed")
	}
}
