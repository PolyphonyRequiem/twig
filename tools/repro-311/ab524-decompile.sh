#!/usr/bin/env bash
# AB#524: decompile the PINNED Terminal.Gui initializer path.
# The tool needs a runtime >= its target; /usr/lib/dotnet only has 8.0.29, so
# point DOTNET_ROOT at the preview.5 side-install and roll forward onto 11.0.
set -u
export HOME=/home/polyphonyrequiem
unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy
export DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5
export PATH=$DOTNET_ROOT:$PATH
export DOTNET_ROLL_FORWARD=LatestMajor

TOOLS=/tmp/ab524-tools
DLL=/home/polyphonyrequiem/.nuget/packages/terminal.gui/2.0.0-develop.5185/lib/net10.0/Terminal.Gui.dll
OUT=/tmp/tg-decomp
mkdir -p "$OUT"

for T in Terminal.Gui.Configuration.ConfigProperty \
         Terminal.Gui.Configuration.ConfigurationManager \
         Terminal.Gui.ModuleInitializers; do
  f="$OUT/${T##*.}.cs"
  "$TOOLS/ilspycmd" -t "$T" "$DLL" > "$f" 2>&1
  echo "$f : $(wc -l < "$f") lines"
done
