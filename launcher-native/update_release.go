package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

const releaseRepository = "AugusDogus/root"
const releaseAPI = "https://api.github.com/repos/" + releaseRepository + "/releases/latest"
const maxUpdateSize int64 = 320 << 20

type releaseVersion [3]uint64

func parseReleaseVersion(value string) (releaseVersion, error) {
	var result releaseVersion
	parts := strings.Split(value, ".")
	if len(parts) != 3 {
		return result, fmt.Errorf("unsupported release version %q", value)
	}
	for index, part := range parts {
		if part == "" || (len(part) > 1 && part[0] == '0') || strings.Trim(part, "0123456789") != "" {
			return result, fmt.Errorf("unsupported release version %q", value)
		}
		number, err := strconv.ParseUint(part, 10, 32)
		if err != nil {
			return result, err
		}
		result[index] = number
	}
	return result, nil
}

func (version releaseVersion) newerThan(other releaseVersion) bool {
	for index, number := range version {
		if number != other[index] {
			return number > other[index]
		}
	}
	return false
}

type releaseAsset struct {
	Name string `json:"name"`
	URL  string `json:"browser_download_url"`
	Size int64  `json:"size"`
}

type launcherRelease struct {
	Tag        string         `json:"tag_name"`
	Draft      bool           `json:"draft"`
	Prerelease bool           `json:"prerelease"`
	Assets     []releaseAsset `json:"assets"`
}

func updateHTTPClient() *http.Client {
	return &http.Client{Timeout: 3 * time.Minute, CheckRedirect: func(request *http.Request, via []*http.Request) error {
		if len(via) >= 5 || request.URL.Scheme != "https" || request.URL.User != nil {
			return fmt.Errorf("update download redirected outside supported HTTPS hosts")
		}
		switch request.URL.Host {
		case "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com":
			return nil
		}
		return fmt.Errorf("update download redirected outside GitHub")
	}}
}

func downloadUpdate(ctx context.Context, client *http.Client, url string, output io.Writer, limit int64) error {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	request.Header.Set("User-Agent", "RootSixPlayer/"+Version)
	response, err := client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("GitHub returned HTTP %d", response.StatusCode)
	}
	if response.ContentLength > limit {
		return fmt.Errorf("update download exceeds %d bytes", limit)
	}
	written, err := io.Copy(output, io.LimitReader(response.Body, limit+1))
	if err != nil {
		return err
	}
	if written > limit {
		return fmt.Errorf("update download exceeds %d bytes", limit)
	}
	return nil
}

func latestLauncherRelease(ctx context.Context, client *http.Client) (launcherRelease, error) {
	var release launcherRelease
	var content strings.Builder
	ctx, cancel := context.WithTimeout(ctx, 4*time.Second)
	defer cancel()
	if err := downloadUpdate(ctx, client, releaseAPI, &content, 1<<20); err != nil {
		return release, err
	}
	if err := json.Unmarshal([]byte(content.String()), &release); err != nil {
		return release, err
	}
	if release.Draft || release.Prerelease || !strings.HasPrefix(release.Tag, "v") {
		return release, fmt.Errorf("GitHub did not return a published stable release")
	}
	_, err := parseReleaseVersion(strings.TrimPrefix(release.Tag, "v"))
	return release, err
}

func (release launcherRelease) asset(name string, limit int64) (releaseAsset, error) {
	var found []releaseAsset
	for _, asset := range release.Assets {
		if asset.Name == name {
			found = append(found, asset)
		}
	}
	if len(found) != 1 {
		return releaseAsset{}, fmt.Errorf("release %s must contain exactly one %s", release.Tag, name)
	}
	asset := found[0]
	expected := "https://github.com/" + releaseRepository + "/releases/download/" + release.Tag + "/" + name
	if asset.URL != expected || asset.Size <= 0 || asset.Size > limit {
		return releaseAsset{}, fmt.Errorf("release asset %s has an unexpected URL or size", name)
	}
	return asset, nil
}

func releaseChecksum(content, name string) (string, error) {
	checksum := ""
	for _, line := range strings.Split(content, "\n") {
		parts := strings.SplitN(line, "  ", 2)
		if len(parts) == 2 && parts[1] == name {
			decoded, err := hex.DecodeString(parts[0])
			if err != nil || len(decoded) != 32 || checksum != "" {
				return "", fmt.Errorf("release checksum for %s is invalid or duplicated", name)
			}
			checksum = strings.ToLower(parts[0])
		}
	}
	if checksum == "" {
		return "", fmt.Errorf("release checksum for %s is missing", name)
	}
	return checksum, nil
}
