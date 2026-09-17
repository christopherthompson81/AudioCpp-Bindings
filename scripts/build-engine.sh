#!/usr/bin/env bash
# Builds the pinned audio.cpp into external/audio.cpp/build.
#
#   ./build-engine.sh [--ccache] [--cuda-arch <list>] [extra cmake flags...]
#
# The engine is a submodule pinned to the commit these bindings are tested
# against, so this needs no paths and no environment: the version being built is
# recorded in the tree, not chosen by whoever runs it. Unrecognised arguments go
# to the configure step, which is how a backend gets turned on:
#
#   ./build-engine.sh -DENGINE_ENABLE_CUDA=ON
#
# The two flags are the ones audio.cpp's own configure advises about but cannot
# apply for you, named as its scripts/build_linux.sh names them. Neither changes
# a default: a build that passes nothing builds exactly what it built before.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/external/audio.cpp"
BUILD="$ENGINE/build"

# A fresh clone without --recursive leaves the submodule directory empty, which
# would otherwise fail deep inside cmake with nothing pointing at the cause.
if [ ! -f "$ENGINE/CMakeLists.txt" ]; then
    # A source archive from GitHub's download link is not a clone and contains no
    # submodules, so the fetch below could not work and the error it produced
    # would not say why.
    if ! git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
        echo "this is not a git clone, so the pinned engine cannot be fetched."
        echo "clone the repository instead:"
        echo "  git clone https://github.com/christopherthompson81/AudioCpp-Bindings"
        exit 1
    fi
    echo "engine submodule is not checked out; fetching it."
    # Not --recursive: audio.cpp's own submodule is the server frontends, which
    # the C ABI does not need and whose URL is SSH-only.
    git -C "$ROOT" submodule update --init external/audio.cpp
fi

# Reporting the commit is a convenience, not a precondition; a checkout that is
# present but not a repository should still build.
echo "engine: $(git -C "$ENGINE" rev-parse --short HEAD 2>/dev/null || echo "unknown revision") (pinned)"

# --ccache and --cuda-arch are consumed here; everything else is a cmake flag and
# is passed through untouched.
use_ccache=""
cuda_arch=""
cmake_args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --ccache) use_ccache="yes"; shift ;;
        --cuda-arch)
            [ $# -ge 2 ] || { echo "--cuda-arch needs an architecture list (e.g. native)" >&2; exit 1; }
            cuda_arch="$2"; shift 2 ;;
        --cuda-arch=*) cuda_arch="${1#*=}"; shift ;;
        *) cmake_args+=("$1"); shift ;;
    esac
done

# audio.cpp forces GGML_CCACHE OFF, so ggml's own probe never runs and NOTHING is
# cached unless a launcher is wired up by hand. Its configure prints a hint
# saying so -- but the hint is silent once C and CXX have launchers, which is
# exactly the state in which the CUDA half, by far the most expensive part of a
# GPU build, is still compiling uncached. So all three are set together here.
if [ -n "$use_ccache" ]; then
    if ! command -v ccache >/dev/null 2>&1; then
        echo "--ccache was passed but ccache is not installed" >&2
        exit 1
    fi
    for language in C CXX CUDA; do
        # "cache the build", not "use ccache rather than whatever you chose": a
        # launcher already in the environment is left alone and said so, since -D
        # would otherwise beat both the environment and the existing cache.
        launcher="CMAKE_${language}_COMPILER_LAUNCHER"
        if [ -n "${!launcher:-}" ]; then
            echo "$launcher=${!launcher} is set; leaving the $language launcher alone."
            continue
        fi
        cmake_args+=("-D${launcher}=ccache")
    done
fi

# The portable default compiles every .cu file once per architecture in a
# nine-entry list. `native` is this host only -- right for a build that exists to
# run the tests, wrong for anything shipped -- so it is opt-in and never implied.
# -U first: CMAKE_CUDA_ARCHITECTURES is sticky, so a previous configure's list
# would otherwise win over the one just asked for.
if [ -n "$cuda_arch" ]; then
    cmake_args+=(-UCMAKE_CUDA_ARCHITECTURES "-DCMAKE_CUDA_ARCHITECTURES=$cuda_arch")
fi

# The tests drive the ABI from both languages and diff the results, so the C
# test binaries are part of a complete build, not an extra.
cmake -S "$ENGINE" -B "$BUILD" \
    -DCMAKE_BUILD_TYPE=Release \
    -DAUDIOCPP_BUILD_C_API=ON \
    ${cmake_args[@]+"${cmake_args[@]}"}

# -DGGML_CUDA=ON only seeds the default of the engine's own ENGINE_ENABLE_CUDA,
# which option() ignores once a cache exists -- so it works on a fresh configure
# and quietly does nothing on a rebuild, leaving a CPU-only library that fails
# much later with "CUDA backend requested but it is not registered in this build".
# Say so here, where the flag was actually given.
# The || true matters under set -e: sed exits non-zero on a missing cache, and a
# bare assignment from a command substitution carries that status.
cuda="$(sed -n 's/^ENGINE_ENABLE_CUDA:BOOL=//p' "$BUILD/CMakeCache.txt" 2>/dev/null || true)"
for argument in ${cmake_args[@]+"${cmake_args[@]}"}; do
    case "$argument" in
        -DGGML_CUDA=[Oo][Nn]|-DGGML_CUDA=1|-DGGML_CUDA:BOOL=[Oo][Nn])
            if [ "$cuda" != "ON" ]; then
                echo
                echo "warning: -DGGML_CUDA=ON did not take. This build has no CUDA."
                echo "         Use -DENGINE_ENABLE_CUDA=ON instead, which is the"
                echo "         option the engine actually reads."
                echo
            fi
            # Said once, however many times the flag was repeated.
            break
            ;;
    esac
done
# Read back from the cache rather than from the flags: these are sticky settings,
# so a rebuild that passed neither flag still inherits whatever the last one set,
# and the build log should say what is actually in force.
arch="$(sed -n 's/^CMAKE_CUDA_ARCHITECTURES:[^=]*=//p' "$BUILD/CMakeCache.txt" 2>/dev/null || true)"
launcher="$(sed -n 's/^CMAKE_CXX_COMPILER_LAUNCHER:[^=]*=//p' "$BUILD/CMakeCache.txt" 2>/dev/null || true)"
cuda_launcher="$(sed -n 's/^CMAKE_CUDA_COMPILER_LAUNCHER:[^=]*=//p' "$BUILD/CMakeCache.txt" 2>/dev/null || true)"
echo "backends: CUDA=${cuda:-OFF}"
echo "compiler launcher: CXX=${launcher:-none} CUDA=${cuda_launcher:-none}"
if [ "$cuda" = "ON" ]; then
    echo "cuda architectures: ${arch:-default} ($(printf %s "${arch:-}" | tr ';' '\n' | grep -c . || true) entries)"
fi

cmake --build "$BUILD" \
    --target audiocpp audiocpp_c_api_path_test audiocpp_c_api_model_test \
    --parallel "$( (nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4) )"

echo
echo "built: $BUILD/bin"
