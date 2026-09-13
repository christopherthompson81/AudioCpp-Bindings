#!/usr/bin/env bash
# Runs the C# tests against a built libaudiocpp, and checks that the bindings
# report exactly what the C tests report for the same models.
#
#   ./run-tests.sh <build-dir> [models-root]
#
# Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
set -uo pipefail

BUILD_DIR="${1:?usage: run-tests.sh <build-dir> [models-root] [threads]}"
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
BINDINGS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Multi-config generators -- Visual Studio, Xcode, Ninja Multi-Config -- put
# outputs in a per-configuration subdirectory, so bin/ alone finds nothing on a
# tree where the library is sitting right there. That reads as "not applicable"
# and passes, which is the failure mode worth avoiding.
BIN_ROOT="$(cd "$BUILD_DIR" && pwd)/bin"
# The VAD model and the sample clips the tests read belong to audio.cpp, not to
# this repository, which is a standalone consumer of the published ABI. CMake
# records the source tree it configured from, so the build directory the caller
# already passes is enough to find them -- no second path argument, and no
# assumption that this checkout sits inside the engine's tree.
SOURCE_ROOT="${AUDIOCPP_SOURCE_ROOT:-$(sed -n 's/^CMAKE_HOME_DIRECTORY:INTERNAL=//p' "$BUILD_DIR/CMakeCache.txt" 2>/dev/null)}"
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
    # CMAKE_HOME_DIRECTORY is the absolute path of whatever machine configured the
    # build, so a build directory copied between machines or a source tree moved
    # after configuring lands here. Print what was tried -- a bare "skipping" is the
    # same silent-green failure the bin/ search above is written to avoid.
    echo "cannot locate the audio.cpp source tree."
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
# Also model-free: parses the specs and checks the install layout. Network
# checks are opt-in, so this stays fast and offline by default.
echo "== package catalog =="
run tests/AudioCpp.PackageTest/AudioCpp.PackageTest.csproj
package_status=$?
[ $package_status -ne 0 ] && [ $package_status -ne 77 ] && exit 1

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
