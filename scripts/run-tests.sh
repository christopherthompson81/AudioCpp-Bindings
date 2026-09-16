#!/usr/bin/env bash
# Runs the C# tests against a built libaudiocpp, and checks that the bindings
# report exactly what the C tests report for the same models.
#
#   ./run-tests.sh [models-root] [threads]
#   ./run-tests.sh <build-dir> [models-root] [threads]
#
# With no build directory it uses the pinned engine build that
# scripts/build-engine.sh produces, which is the version these tests are
# written against.
#
# Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
set -uo pipefail

BINDINGS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$BINDINGS_ROOT/external/audio.cpp"
# The build directory used to be required and first. Now that it defaults to the
# pinned engine it is usually absent, and the models root -- still the argument
# that matters -- would otherwise have to be preceded by a path nobody needs to
# type. A configured build directory always has a CMakeCache.txt and a models root
# never does, so which was meant is decidable rather than guessed. Both the old
# two-argument form and the short form work, and the choice is printed.
if [ -n "${1:-}" ] && [ ! -f "$1/CMakeCache.txt" ]; then
    set -- "$ENGINE/build" "$@"
    echo "using the pinned engine build; treating $2 as the models root"
fi
BUILD_DIR="${1:-$ENGINE/build}"
if [ ! -d "$BUILD_DIR" ]; then
    echo "no engine build at $BUILD_DIR"
    echo "build the pinned engine first: ./scripts/build-engine.sh"
    exit 77
fi
MODELS_ROOT="${2:-}"
# Resolved before the cd below. Left relative it would resolve against the engine's
# source tree, and a models root that is merely in the wrong place reports "no
# models" and exits 77 -- a skip, which reads as green.
if [ -n "$MODELS_ROOT" ]; then
    if [ ! -d "$MODELS_ROOT" ]; then
        echo "models root not found: $MODELS_ROOT"
        exit 1
    fi
    MODELS_ROOT="$(cd "$MODELS_ROOT" && pwd)"
fi
# Separation dominates the wall time and scales with threads; results are
# unaffected by the count. Both languages get the same number so the
# cross-language diff compares like with like.
THREADS="${3:-$( (nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4) )}"
# Multi-config generators -- Visual Studio, Xcode, Ninja Multi-Config -- put
# outputs in a per-configuration subdirectory, so bin/ alone finds nothing on a
# tree where the library is sitting right there. That reads as "not applicable"
# and passes, which is the failure mode worth avoiding.
BIN_ROOT="$(cd "$BUILD_DIR" && pwd)/bin"
# The VAD model and the sample clips the tests read belong to audio.cpp, not to
# this repository, which is a standalone consumer of the published ABI. The
# pinned submodule carries them, so the default run needs no path argument at all.
# For a build directory passed in from elsewhere, CMake records the source tree it
# was configured from, which is the tree whose assets match that build.
# CMAKE_HOME_DIRECTORY is the absolute path of whatever machine configured the
# build, so it is wrong for a build directory copied between machines or a source
# tree moved after configuring. The pinned submodule is right regardless of where
# the checkout lives, so it backstops the cache rather than the other way around:
# an external build directory still gets the tree it was actually configured from.
SOURCE_ROOT="${AUDIOCPP_SOURCE_ROOT:-$(sed -n 's/^CMAKE_HOME_DIRECTORY:INTERNAL=//p' "$BUILD_DIR/CMakeCache.txt" 2>/dev/null)}"
if [ ! -d "${SOURCE_ROOT:-}/assets" ] && [ -d "$ENGINE/assets" ]; then
    SOURCE_ROOT="$ENGINE"
fi
BIN=""
SEARCHED=""
for config in "" Release RelWithDebInfo MinSizeRel Debug; do
    candidate="$BIN_ROOT${config:+/$config}"
    SEARCHED="$SEARCHED  $candidate"$'\n'
    for name in libaudiocpp.so libaudiocpp.dylib audiocpp.dll; do
        if [ -e "$candidate/$name" ]; then
            BIN="$candidate"
            break 2
        fi
    done
done

if [ -z "$BIN" ]; then
    echo "no libaudiocpp found; build with -DAUDIOCPP_BUILD_C_API=ON. Looked in:"
    printf '%s' "$SEARCHED"
    echo "Skipping."
    exit 77
fi
echo "native: $BIN"

if [ ! -d "${SOURCE_ROOT:-}/assets" ]; then
    # Print what was tried -- a bare "skipping" is the same silent-green failure
    # the bin/ search above is written to avoid.
    echo "cannot locate the audio.cpp source tree."
    echo "  pinned engine: $ENGINE (not checked out?)"
    echo "  from: $BUILD_DIR/CMakeCache.txt"
    echo "  tried: ${SOURCE_ROOT:-<empty>}"
    echo "Set AUDIOCPP_SOURCE_ROOT to override. Skipping."
    exit 77
fi
echo "source: $SOURCE_ROOT"

VAD_MODEL="$SOURCE_ROOT/assets/framework/models/silero_vad"
SAMPLE_WAV="$SOURCE_ROOT/assets/resources/sample_16k.wav"
SEPARATION_WAV="$SOURCE_ROOT/tests/ace_step/assets/complete_source_demucs_8s.wav"

export AUDIOCPP_NATIVE_DIR="$BIN"

# Every stage below runs --no-build, so a test project missing from the solution is
# never compiled and dies with "No such file or directory" instead of running. That
# is not hypothetical: AudioCpp.AudioTest was dropped from the solution by an
# unrelated UI change and stayed silent for weeks, because the stale binary in bin/
# kept running until someone built from clean. Checked here rather than trusted,
# since this script is what assumes one build covers every project it then runs.
missing=""
for proj in "$BINDINGS_ROOT"/tests/*/*.csproj; do
    # An unmatched glob expands to itself, which would report a project named "*".
    [ -e "$proj" ] || continue
    rel="tests/$(basename "$(dirname "$proj")")/$(basename "$proj")"
    grep -qF "$rel" "$BINDINGS_ROOT/AudioCpp.slnx" || missing="$missing  $rel"$'\n'
done
if [ -n "$missing" ]; then
    echo "these test projects are not in AudioCpp.slnx, so --no-build cannot run them:"
    printf '%s' "$missing"
    echo "add them to the solution, or this script will skip past them with a confusing error."
    exit 1
fi

dotnet build "$BINDINGS_ROOT/AudioCpp.slnx" -v q --nologo || exit 1

# Run from the engine's source tree. A family whose GGUF predates the schema-v1
# contract -- bs_roformer is one -- resolves its spec by walking up from the
# working directory to a model_specs/ directory, and nothing else is consulted
# for that lookup: not the models root, not the library's own location. Running
# from this repository instead would silently skip those families.
cd "$SOURCE_ROOT"

run() { dotnet run --project "$BINDINGS_ROOT/$1" --no-build -- "${@:2}"; }

echo "threads=$THREADS"

# Cheapest check first, and the only one needing no models: if an entry point
# went unbound, every later failure would be a confusing symptom of it.
echo "== binding coverage =="
run tests/AudioCpp.BindingCoverage/AudioCpp.BindingCoverage.csproj
coverage_status=$?
[ $coverage_status -ne 0 ] && [ $coverage_status -ne 77 ] && exit 1

echo
# Model-free: binds a real socket and serves real requests. The server is where
# the bindings get used from threads the window never uses them from.
echo "== server =="
# The routes that need a model run when one is named. Point AUDIOCPP_TTS_MODEL
# at a TTS gguf (and optionally AUDIOCPP_VOICE_REF plus AUDIOCPP_VOICE_REF_TEXT
# at a reference clip and its transcript) to exercise them.
run tests/AudioCpp.ServerTest/AudioCpp.ServerTest.csproj
server_status=$?
[ $server_status -ne 0 ] && [ $server_status -ne 77 ] && exit 1

echo
# Also model-free: parses the specs and checks the install layout. Network
# checks are opt-in, so this stays fast and offline by default.
echo "== package catalog =="
run tests/AudioCpp.PackageTest/AudioCpp.PackageTest.csproj
package_status=$?
[ $package_status -ne 0 ] && [ $package_status -ne 77 ] && exit 1

echo
# Needs an audio backend rather than a model, and skips without one.
echo "== audio capture =="
run tests/AudioCpp.AudioTest/AudioCpp.AudioTest.csproj
capture_status=$?
[ $capture_status -ne 0 ] && [ $capture_status -ne 77 ] && exit 1

echo
echo "== C# path test =="
run tests/AudioCpp.PathTest/AudioCpp.PathTest.csproj \
    "$VAD_MODEL" "$SAMPLE_WAV" cpu
path_status=$?
[ $path_status -ne 0 ] && [ $path_status -ne 77 ] && exit 1

if [ -z "$MODELS_ROOT" ]; then
    echo "no models root given; skipping the model and cross-language checks"
    exit 0
fi

echo
echo "== C# model test =="
# Run once and keep the output: this is the expensive step, and running it a
# second time just to diff it would roughly double the suite's wall time.
cs_out="$(mktemp)"
trap 'rm -f "$cs_out" "${c_out:-}"' EXIT
run tests/AudioCpp.ModelTest/AudioCpp.ModelTest.csproj \
    "$MODELS_ROOT" "$SAMPLE_WAV" cpu \
    "$SEPARATION_WAV" "$THREADS" | tee "$cs_out"
model_status=${PIPESTATUS[0]}
[ $model_status -ne 0 ] && [ $model_status -ne 77 ] && exit 1
[ $model_status -eq 77 ] && exit 0

# The bindings and the C tests drive the same ABI, so for the same models they
# must report the same thing. Anything else is a marshalling bug.
C_MODEL_TEST="$BIN/audiocpp_c_api_model_test"
[ -x "$C_MODEL_TEST" ] || C_MODEL_TEST="$BIN/audiocpp_c_api_model_test.exe"
if [ ! -x "$C_MODEL_TEST" ]; then
    echo "audiocpp_c_api_model_test not built under $BIN; skipping the cross-language check"
    exit 0
fi

echo
echo "== C vs C# =="
c_out="$(mktemp)"
"$C_MODEL_TEST" "$MODELS_ROOT" "$SAMPLE_WAV" cpu \
    "$SEPARATION_WAV" "$THREADS" 2>/dev/null \
    | grep '^parity:' | sort > "$c_out"
grep '^parity:' "$cs_out" | sort > "$cs_out.sorted" && mv "$cs_out.sorted" "$cs_out"

if diff -u "$c_out" "$cs_out"; then
    echo "C and C# agree on $(wc -l < "$c_out") reported values"
else
    echo "C and C# DISAGREE"
    exit 1
fi
