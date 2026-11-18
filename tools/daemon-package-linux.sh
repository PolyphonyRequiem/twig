#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
revision="$(git rev-parse --verify HEAD)"
[[ -z "$(git status --porcelain --untracked-files=normal)" ]] || { echo "Clean checkout required." >&2; exit 1; }
out=artifacts/daemon-linux-x64
[[ ! -e "$out" ]] || { echo "Refusing to overwrite $out" >&2; exit 1; }
python3 tools/run-bounded.py --timeout 120 -- dotnet publish src/Twig.Daemon/Twig.Daemon.csproj \
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:InvariantGlobalization=true \
    "-p:DaemonSourceRevisionId=$revision" -m:1 --verbosity minimal -o "$out"
python3 -I tools/daemon-linux.py manifest "$out" "$revision"
archive=twig-daemon-linux-x64.tar.gz
tar -czf "artifacts/$archive" -C tools daemon-linux.py -C ../artifacts daemon-linux-x64
(cd artifacts && sha256sum "$archive" > "$archive.sha256")
