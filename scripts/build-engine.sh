#!/usr/bin/env bash
# Builds the pinned audio.cpp into external/audio.cpp/build.
#
#   ./build-native.sh [extra cmake flags...]
#
# The engine is a submodule pinned to the commit these bindings are tested
# against, so this needs no paths and no environment: the version being built is
# recorded in the tree, not chosen by whoever runs it. Extra arguments go to the
# configure step, which is how a backend gets turned on:
#
#   ./build-native.sh -DGGML_CUDA=ON
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/external/audio.cpp"
BUILD="$ENGINE/build"

# A fresh clone without --recursive leaves the submodule directory empty, which
# would otherwise fail deep inside cmake with nothing pointing at the cause.
if [ ! -f "$ENGINE/CMakeLists.txt" ]; then
    echo "engine submodule is not checked out; fetching it."
    # Not --recursive: audio.cpp's own submodule is the server frontends, which
    # the C ABI does not need and whose URL is SSH-only.
    git -C "$ROOT" submodule update --init external/audio.cpp
fi

echo "engine: $(git -C "$ENGINE" rev-parse --short HEAD) (pinned)"

# The tests drive the ABI from both languages and diff the results, so the C
# test binaries are part of a complete build, not an extra.
cmake -S "$ENGINE" -B "$BUILD" \
    -DCMAKE_BUILD_TYPE=Release \
    -DAUDIOCPP_BUILD_C_API=ON \
    "$@"
cmake --build "$BUILD" \
    --target audiocpp audiocpp_c_api_path_test audiocpp_c_api_model_test \
    --parallel "$( (nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4) )"

echo
echo "built: $BUILD/bin"
