#!/usr/bin/env bash
# Builds the audioio shim. CMake rather than NuGet, because miniaudio is a
# single C header compiled for this machine; there is no prebuilt package.
#
# The output lands in native/audioio/build, which is one of the places the
# managed resolver looks, so nothing needs configuring after a build.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$here/native/audioio"
out="$src/build"

generator=()
command -v ninja >/dev/null 2>&1 && generator=(-G Ninja)

cmake -S "$src" -B "$out" "${generator[@]}" -DCMAKE_BUILD_TYPE=Release
cmake --build "$out" --config Release --parallel

echo
echo "built:"
find "$out" -maxdepth 2 \( -name 'libaudioio.*' -o -name 'audioio.dll' \) -print
