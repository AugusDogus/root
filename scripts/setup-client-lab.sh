#!/usr/bin/env bash
set -euo pipefail
umask 077
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"
client_dir=.lab/client
if [[ $# -gt 0 ]]; then
  case "$1" in
    [1-6]) client_dir=".lab/clients/seat-$1" ;;
    *) echo 'Expected an optional seat number from 1 to 6.' >&2; exit 2 ;;
  esac
fi
exec 9>.lab/run.lock
flock --nonblock 9 || { echo 'Stop the host lab before copying its prefix.' >&2; exit 1; }
mkdir -p "$client_dir"
exec 8>"$client_dir/run.lock"
flock --nonblock 8 || { echo 'Stop the client lab before updating its files.' >&2; exit 1; }
for directory in game compatdata; do
  if [[ ! -d "$client_dir/$directory" ]]; then
    cp -a --reflink=auto ".lab/$directory" "$client_dir/$directory"
  fi
done
cp engine-probe/bin/Debug/net6.0/EngineProbe.dll "$client_dir/game/BepInEx/plugins/"
echo "Isolated client game and prefix ready: $client_dir"
