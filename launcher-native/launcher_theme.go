package main

import (
	"crypto/sha256"
	"fmt"
	"image"
	"image/color"
	"image/draw"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"

	xdraw "golang.org/x/image/draw"
	"golang.org/x/image/font"
	"golang.org/x/image/font/gofont/goregular"
	"golang.org/x/image/font/opentype"
	"golang.org/x/image/math/fixed"
)

type launcherTheme struct {
	forest image.Image
	logo   image.Image
	font   *opentype.Font
}

// Root build 22238765 stores its original Baskerville font directly in this
// serialized file. Verify the exact font bytes before parsing; unsupported
// builds use the bundled Go font. No game artwork or fonts enter our packages.
func rootMenuFont(game string) (*opentype.Font, error) {
	file, err := os.Open(filepath.Join(game, "Root_Data", "resources.assets"))
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data := make([]byte, 118812)
	if _, err := file.ReadAt(data, 29078616); err != nil {
		return nil, err
	}
	if fmt.Sprintf("%x", sha256.Sum256(data)) != "e3d88f10fbc3fd8e80b3c27fcb6465a267c4e5cb297b83ee7656b8a7a68513b1" {
		return nil, fmt.Errorf("installed Root menu font does not match supported build %s", GameBuild)
	}
	return opentype.Parse(data)
}

func loadLauncherTheme(game, steam string) launcherTheme {
	face, err := rootMenuFont(game)
	if err != nil {
		log.Print("Using fallback launcher font: ", err)
		// This constant font is shipped by x/image and covered by its license.
		face, err = opentype.Parse(goregular.TTF)
		if err != nil {
			panic("invalid bundled Go font")
		}
	}
	cache := filepath.Join(steam, "appcache", "librarycache")
	return launcherTheme{
		font:   face,
		forest: cachedRootArt(cache, "library_hero.jpg"),
		logo:   cachedRootArt(cache, "logo.png"),
	}
}

func cachedRootArt(cache, name string) image.Image {
	paths := []string{filepath.Join(cache, AppID, name), filepath.Join(cache, AppID+"_"+name)}
	// Current Steam versions put artwork under a content-hash directory.
	matches, _ := filepath.Glob(filepath.Join(cache, AppID, "*", name))
	paths = append(paths, matches...)
	for _, path := range paths {
		art, err := readLauncherImage(path)
		if err == nil {
			return art
		}
	}
	return nil
}

func readLauncherImage(path string) (image.Image, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	config, _, err := image.DecodeConfig(io.LimitReader(file, 16<<20))
	if err != nil {
		return nil, err
	}
	if config.Width < 1 || config.Height < 1 || config.Width > 4096 || config.Height > 4096 {
		return nil, fmt.Errorf("launcher artwork exceeds 4096 pixels")
	}
	if _, err = file.Seek(0, io.SeekStart); err != nil {
		return nil, err
	}
	art, _, err := image.Decode(io.LimitReader(file, 16<<20))
	return art, err
}

func (theme launcherTheme) paint(canvas *image.RGBA, message string, pulse int) {
	w, h := canvas.Bounds().Dx(), canvas.Bounds().Dy()
	scale := min(float64(w)/800, float64(h)/500)
	ox, oy := (float64(w)-800*scale)/2, (float64(h)-500*scale)/2
	rect := func(x, y, width, height float64) image.Rectangle {
		return image.Rect(int(ox+x*scale), int(oy+y*scale), int(ox+(x+width)*scale), int(oy+(y+height)*scale))
	}
	ink := color.RGBA{35, 30, 24, 255}
	cream := color.RGBA{241, 225, 180, 255}
	fill := func(area image.Rectangle, tint color.Color) {
		draw.Draw(canvas, area, image.NewUniform(tint), image.Point{}, draw.Src)
	}
	fill(canvas.Bounds(), ink)
	if theme.forest != nil {
		// Cover, preserving the original illustration's proportions.
		source := theme.forest.Bounds()
		ratio := max(float64(w)/float64(source.Dx()), float64(h)/float64(source.Dy()))
		dw, dh := int(float64(source.Dx())*ratio), int(float64(source.Dy())*ratio)
		xdraw.CatmullRom.Scale(canvas, image.Rect((w-dw)/2, (h-dh)/2, (w+dw)/2, (h+dh)/2), theme.forest, source, draw.Over, nil)
	}
	if theme.logo != nil {
		xdraw.CatmullRom.Scale(canvas, rect(200, 4, 400, 225), theme.logo, theme.logo.Bounds(), draw.Over, nil)
	}
	text := func(value string, points, center, baseline float64, tint color.Color) {
		face, err := opentype.NewFace(theme.font, &opentype.FaceOptions{Size: points * scale, DPI: 72, Hinting: font.HintingFull})
		if err != nil {
			return
		}
		defer face.Close()
		drawer := font.Drawer{Dst: canvas, Src: image.NewUniform(tint), Face: face}
		drawer.Dot = fixed.P(int(ox+center*scale)-drawer.MeasureString(value).Ceil()/2, int(oy+baseline*scale))
		drawer.DrawString(value)
	}
	if theme.logo == nil {
		text("Root", 76, 400, 157, cream)
	}
	text("Six Player", 38, 400, 250, cream)
	// A simple parchment panel keeps changing setup text readable over the art.
	fill(rect(48, 302, 704, 144), ink)
	fill(rect(51, 305, 698, 138), cream)
	fill(rect(75, 330, 650, 3), color.RGBA{150, 114, 58, 255})
	fill(rect(75+float64(pulse)*550/23, 330, 100, 3), color.RGBA{221, 167, 41, 255})
	lines := wrapLauncherStatus(message, 62)
	for index, line := range lines {
		text(line, 23, 400, 369+float64(index)*27, ink)
	}
	text("Root will open when ready.", 17, 400, 426, color.RGBA{84, 72, 51, 255})
	text("v"+Version, 15, 400, 477, cream)
}

func wrapLauncherStatus(message string, limit int) []string {
	var lines []string
	line := ""
	for _, word := range strings.Fields(message) {
		if line != "" && len([]rune(line+" "+word)) > limit {
			lines = append(lines, line)
			line = ""
		}
		if line != "" {
			line += " "
		}
		line += word
	}
	if line != "" {
		lines = append(lines, line)
	}
	return lines
}
