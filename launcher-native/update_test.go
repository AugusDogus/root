package main

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

type updateTransport func(*http.Request) (*http.Response, error)

func (transport updateTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return transport(request)
}

func updateFixture(t *testing.T, platform string) (*http.Client, map[string]string, string) {
	t.Helper()
	name := "RootSixPlayer.exe"
	payload := []byte("MZ new Windows launcher")
	if platform == "linux" {
		name = "RootSixPlayer-0.8.0-x86_64.AppImage"
		payload = appImageFixture("new Linux launcher")
	}
	base := "https://github.com/" + releaseRepository + "/releases/download/v0.8.0/"
	sums := prepareDigest(payload) + "  " + name + "\n"
	release := launcherRelease{Tag: "v0.8.0", Assets: []releaseAsset{
		{Name: name, URL: base + name, Size: int64(len(payload))},
		{Name: "SHA256SUMS", URL: base + "SHA256SUMS", Size: int64(len(sums))},
	}}
	metadata, err := json.Marshal(release)
	if err != nil {
		t.Fatal(err)
	}
	responses := map[string]string{releaseAPI: string(metadata), base + name: string(payload), base + "SHA256SUMS": sums}
	client := &http.Client{Transport: updateTransport(func(request *http.Request) (*http.Response, error) {
		body, ok := responses[request.URL.String()]
		if !ok {
			return nil, fmt.Errorf("unexpected update request: %s", request.URL)
		}
		return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(body)), ContentLength: int64(len(body)), Header: make(http.Header)}, nil
	})}
	return client, responses, base + name
}

func TestWindowsUpdateDownloadsAndReusesVerifiedExecutable(t *testing.T) {
	client, _, _ := updateFixture(t, "windows")
	data := t.TempDir()
	var messages []string
	path, err := fetchLauncherUpdate(context.Background(), client, data, "0.7.3", "windows", func(message string) { messages = append(messages, message) })
	if err != nil {
		t.Fatal(err)
	}
	if path != launcherUpdatePath(data, "0.8.0") || len(messages) != 2 {
		t.Fatalf("unexpected update: %s, %v", path, messages)
	}
	cached, err := cachedLauncherUpdate(data, "0.7.3")
	if err != nil || cached != path {
		t.Fatalf("offline cache lookup: %s, %v", cached, err)
	}
	if cached, err := cachedLauncherUpdate(data, "0.8.0"); err != nil || cached != "" {
		t.Fatalf("updated launcher must not hand off to itself: %s, %v", cached, err)
	}
	if err := os.WriteFile(path, []byte("changed"), 0700); err != nil {
		t.Fatal(err)
	}
	if _, err := cachedLauncherUpdate(data, "0.7.3"); err == nil {
		t.Fatal("changed executable was accepted")
	}
}

func TestFailedDownloadPreservesCachedVersion(t *testing.T) {
	client, responses, url := updateFixture(t, "windows")
	data := t.TempDir()
	marker := filepath.Join(data, "launcher-updates", "current.json")
	previous := launcherUpdatePath(data, "0.7.5")
	prepareTestWrite(t, previous, []byte("previous launcher"))
	if err := writeJSON(marker, cachedLauncher{"0.7.5", prepareDigest([]byte("previous launcher"))}); err != nil {
		t.Fatal(err)
	}
	responses[url] = "damaged"
	if _, err := fetchLauncherUpdate(context.Background(), client, data, "0.7.5", "windows", func(string) {}); err == nil {
		t.Fatal("damaged download was accepted")
	}
	if cached, err := cachedLauncherUpdate(data, "0.7.3"); err != nil || cached != previous {
		t.Fatalf("previous launcher was lost: %s, %v", cached, err)
	}
	partial, err := filepath.Glob(filepath.Join(data, "launcher-updates", ".download-*"))
	if err != nil || len(partial) != 0 {
		t.Fatalf("partial download remained: %v, %v", partial, err)
	}
}

func TestCurrentAndOlderReleasesAreNotDownloaded(t *testing.T) {
	for _, current := range []string{"0.8.0", "0.10.0", "1.0.0"} {
		client, responses, url := updateFixture(t, "windows")
		delete(responses, url)
		path, err := fetchLauncherUpdate(context.Background(), client, t.TempDir(), current, "windows", func(string) {})
		if err != nil || path != "" {
			t.Fatalf("version %s must not downgrade: %s, %v", current, path, err)
		}
	}
}

func TestUpdateRejectsUntrustedReleaseMetadata(t *testing.T) {
	for _, mutate := range []func(*launcherRelease){
		func(release *launcherRelease) { release.Tag = "v../../bad" },
		func(release *launcherRelease) { release.Draft = true },
		func(release *launcherRelease) { release.Prerelease = true },
		func(release *launcherRelease) { release.Assets[0].URL = "https://example.com/payload.exe" },
		func(release *launcherRelease) { release.Assets[0].Size = maxUpdateSize + 1 },
		func(release *launcherRelease) { release.Assets = append(release.Assets, release.Assets[0]) },
	} {
		client, responses, _ := updateFixture(t, "windows")
		var release launcherRelease
		if err := json.Unmarshal([]byte(responses[releaseAPI]), &release); err != nil {
			t.Fatal(err)
		}
		mutate(&release)
		encoded, err := json.Marshal(release)
		if err != nil {
			t.Fatal(err)
		}
		responses[releaseAPI] = string(encoded)
		if _, err := fetchLauncherUpdate(context.Background(), client, t.TempDir(), "0.7.3", "windows", func(string) {}); err == nil {
			t.Fatal("untrusted metadata was accepted")
		}
	}
}

func TestUpdateRedirectsStayOnGitHubHTTPS(t *testing.T) {
	client := updateHTTPClient()
	for _, url := range []string{"http://github.com/file", "https://example.com/file", "https://user@github.com/file"} {
		request, err := http.NewRequest(http.MethodGet, url, nil)
		if err != nil {
			t.Fatal(err)
		}
		if err := client.CheckRedirect(request, nil); err == nil {
			t.Fatalf("unsafe redirect was accepted: %s", url)
		}
	}
}

func TestUpdateChecksumRejectsAmbiguousRecords(t *testing.T) {
	sum := strings.Repeat("a", 64) + "  RootSixPlayer.exe\n"
	for _, content := range []string{"", "xyz  RootSixPlayer.exe", sum + sum} {
		if _, err := releaseChecksum(content, "RootSixPlayer.exe"); err == nil {
			t.Fatal("missing or ambiguous checksum was accepted")
		}
	}
}

func TestDiscoveryAllowsUpdateBeforeBuildCompatibilityCheck(t *testing.T) {
	inst := prepareTestGame(t)
	manifest := filepath.Join(filepath.Dir(filepath.Dir(inst.Game)), "appmanifest_"+AppID+".acf")
	prepareTestWrite(t, manifest, []byte(`"appid" "965580" "buildid" "future-build" "installdir" "Root"`))
	if err := validateInstalledGame(inst.Game, ""); err != nil {
		t.Fatal(err)
	}
	if err := validateGame(inst.Game); err == nil {
		t.Fatal("a future game build must still fail preparation validation")
	}
}

func TestUnavailableReleaseKeepsInstalledLauncherUsable(t *testing.T) {
	for _, status := range []int{404, 403, 500} {
		client := &http.Client{Transport: updateTransport(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: status, Body: io.NopCloser(strings.NewReader("unavailable"))}, nil
		})}
		var messages []string
		path, err := launcherUpdateWithClient(context.Background(), client, t.TempDir(), func(message string) { messages = append(messages, message) })
		if err != nil || path != "" || messages[len(messages)-1] != "Update unavailable. Opening the installed version." {
			t.Fatalf("HTTP %d blocked startup: %s, %v, %v", status, path, err, messages)
		}
	}
}

func TestCachedUpdateStartsWithoutNetwork(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("only Windows uses cached executables")
	}
	data := t.TempDir()
	path := launcherUpdatePath(data, "99.0.0")
	prepareTestWrite(t, path, []byte("cached launcher"))
	if err := writeJSON(filepath.Join(data, "launcher-updates", "current.json"), cachedLauncher{"99.0.0", prepareDigest([]byte("cached launcher"))}); err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Transport: updateTransport(func(*http.Request) (*http.Response, error) {
		t.Fatal("cached handoff must not require the network")
		return nil, fmt.Errorf("offline")
	})}
	got, err := launcherUpdateWithClient(context.Background(), client, data, func(string) {})
	if err != nil || got != path {
		t.Fatalf("offline handoff failed: %s, %v", got, err)
	}
}

func appImageFixture(extra string) []byte {
	header := make([]byte, 64)
	copy(header, "\x7fELF\x02\x01\x01")
	copy(header[8:], "AI\x02")
	binary.LittleEndian.PutUint16(header[18:], 62)
	return append(header, extra...)
}
