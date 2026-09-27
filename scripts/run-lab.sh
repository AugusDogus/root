#!/usr/bin/env bash
# Parse the full runner before launching a long-lived child. Bash otherwise
# resumes reading changed byte offsets if this file is edited during a run.
run_lab() {
set -euo pipefail
umask 077

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
python3 - "$project_dir/launcher" <<'PY'
import sys
sys.path.insert(0, sys.argv[1])
from test_budget import require_test_budget
require_test_budget()
PY
lab_dir="${ROOT_LAB_DIR:-$project_dir/.lab}"
if [[ ! -f "$lab_dir/game/Root.exe" || ! -d "$lab_dir/compatdata/pfx" ]]; then
  echo 'Root lab files are missing. Run python3 scripts/setup-lab.py first.' >&2
  exit 1
fi
# Only xvfb-run may supply a display. Also pin Wine itself, not just Proton,
# to this lab so helper launches cannot fall back to the user's default prefix.
unset DISPLAY WAYLAND_DISPLAY WAYLAND_SOCKET
export WINEPREFIX="$lab_dir/compatdata/pfx"
exec 9>"$lab_dir/run.lock"
if flock --nonblock 9; then :; else
  echo 'Another Root lab run is active. Wait for it to exit before starting a new run.' >&2
  exit 1
fi
steam_dir="$HOME/.local/share/Steam"
mode="${1:-inspect}"
players="${2:-4}"
case "$mode" in
  inspect|host|server|client|client-probe|setup|selection-tests|recovery-tests) ;;
  *) echo 'Expected mode: inspect, host, server, client, client-probe, setup, selection-tests, or recovery-tests' >&2; exit 2 ;;
esac
case "$players" in
  4|6) ;;
  *) echo 'Expected player count: 4 or 6' >&2; exit 2 ;;
esac

mkdir -p "$lab_dir/results/$mode" "$lab_dir/shadercache"
cleanup() {
  rm -f "$lab_dir/results/$mode/display.json"
  if [[ "$mode" == server ]]; then
    rm -f "$lab_dir/results/server/endpoint.json"
  fi
  if [[ ( "$mode" == client || "$mode" == client-probe ) && -n "${ROOT_CLIENT_CONNECTION:-}" ]]; then
    rm -f "$lab_dir/connection.json"
  fi
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
graphics_args=(-batchmode -nographics)
run_timeout=180s
if [[ "$mode" == server ]]; then run_timeout=360s; fi
if [[ "$mode" == client-probe ]]; then run_timeout=240s; fi
if [[ -n "${ROOT_LAB_LIFETIME_SECONDS:-}" ]]; then
  if [[ ! "$ROOT_LAB_LIFETIME_SECONDS" =~ ^[0-9]{1,5}$ ]]; then
    echo 'ROOT_LAB_LIFETIME_SECONDS must be between 30 and 43200.' >&2; exit 2
  fi
  lab_lifetime=$((10#$ROOT_LAB_LIFETIME_SECONDS))
  if (( lab_lifetime < 30 || lab_lifetime > 43200 )); then
    echo 'ROOT_LAB_LIFETIME_SECONDS must be between 30 and 43200.' >&2; exit 2
  fi
  run_timeout="$((lab_lifetime + 60))s"
fi
if [[ "$mode" == client-probe || "$mode" == client ]]; then
  graphics_args=(-screen-fullscreen 0 -screen-width 1280 -screen-height 800)
fi
if [[ "$mode" == server ]]; then rm -f "$lab_dir/results/server/endpoint.json"; fi
if [[ ( "$mode" == client || "$mode" == client-probe ) && -n "${ROOT_CLIENT_CONNECTION:-}" ]]; then
  cp -- "$ROOT_CLIENT_CONNECTION" "$lab_dir/connection.json"
  chmod 600 "$lab_dir/connection.json"
fi
cd "$lab_dir/game"

# xvfb-run allocates an unused display and removes only its own display on exit.
# Disable both Unity audio and Wine audio drivers, without changing host sinks.
# Keep the lock in this shell; Wine helper processes must not inherit it.
xvfb-run --auto-servernum --server-args='-screen 0 1280x800x24 -nolisten tcp' \
  env -u WAYLAND_DISPLAY -u WAYLAND_SOCKET \
  ROOT_LAB_MODE="$mode" ROOT_LAB_PLAYERS="$players" ROOT_LAB_OUTPUT="$lab_dir/results/$mode" \
  XDG_SESSION_TYPE=x11 SDL_VIDEODRIVER=x11 SDL_AUDIODRIVER=dummy \
  PULSE_SERVER="unix:$lab_dir/no-audio-socket" \
  WINEDLLOVERRIDES='winhttp=n,b;winepulse.drv=d;winealsa.drv=d' \
  PROTON_ENABLE_WAYLAND=0 PROTON_USE_WINED3D=1 LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 \
  STEAM_COMPAT_DATA_PATH="$lab_dir/compatdata" \
  STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam_dir" \
  STEAM_COMPAT_SHADER_PATH="$lab_dir/shadercache" \
  SteamAppId=965580 SteamGameId=965580 \
  python3 "$project_dir/scripts/record-display.py" \
  nice -n 10 timeout --signal=TERM --kill-after=10s "$run_timeout" \
  "$steam_dir/steamapps/common/SteamLinuxRuntime_4/_v2-entry-point" \
  --verb=waitforexitandrun -- \
  "$steam_dir/steamapps/common/Proton - Experimental/proton" \
  waitforexitandrun "$lab_dir/game/Root.exe" \
  "${graphics_args[@]}" -noaudio -job-worker-count 2 -logFile "$lab_dir/results/$mode/player.log" 9>&-
exit 0
}
run_lab "$@"
