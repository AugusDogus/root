#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"
if [[ ! -x .lab/sdk/dotnet || ! -f .lab/game/BepInEx/interop/tuber-canis.dll ]]; then
  echo 'Root lab SDK or engine bindings are missing. Run python3 scripts/setup-lab.py first.' >&2
  exit 1
fi
exec 9>.lab/run.lock
if flock --nonblock 9; then :; else
  echo 'A Root lab run is active. Stop it before replacing the probe plugin.' >&2
  exit 1
fi
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  .lab/sdk/dotnet build engine-probe/EngineProbe.csproj --nologo -p:UseSharedCompilation=false
mkdir -p .lab/game/BepInEx/plugins
cp engine-probe/bin/Debug/net6.0/EngineProbe.dll .lab/game/BepInEx/plugins/
