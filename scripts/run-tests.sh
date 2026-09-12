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
# Separation dominates the wall time and scales with threads; results are
# unaffected by the count. Both languages get the same number so the
# cross-language diff compares like with like.
THREADS="${3:-$( (nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4) )}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# Multi-config generators -- Visual Studio, Xcode, Ninja Multi-Config -- put
# outputs in a per-configuration subdirectory, so bin/ alone finds nothing on a
# tree where the library is sitting right there. That reads as "not applicable"
# and passes, which is the failure mode worth avoiding.
BIN_ROOT="$(cd "$BUILD_DIR" && pwd)/bin"
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

export AUDIOCPP_NATIVE_DIR="$BIN"
cd "$REPO_ROOT"

dotnet build bindings/csharp/AudioCpp.slnx -v q --nologo || exit 1

run() { dotnet run --project "$1" --no-build -- "${@:2}"; }

echo "threads=$THREADS"
echo "== C# path test =="
run bindings/csharp/AudioCpp.PathTest/AudioCpp.PathTest.csproj \
    assets/framework/models/silero_vad assets/resources/sample_16k.wav cpu
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
run bindings/csharp/AudioCpp.ModelTest/AudioCpp.ModelTest.csproj \
    "$MODELS_ROOT" assets/resources/sample_16k.wav cpu \
    tests/ace_step/assets/complete_source_demucs_8s.wav "$THREADS" | tee "$cs_out"
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
"$C_MODEL_TEST" "$MODELS_ROOT" assets/resources/sample_16k.wav cpu \
    tests/ace_step/assets/complete_source_demucs_8s.wav "$THREADS" 2>/dev/null \
    | grep '^parity:' | sort > "$c_out"
grep '^parity:' "$cs_out" | sort > "$cs_out.sorted" && mv "$cs_out.sorted" "$cs_out"

if diff -u "$c_out" "$cs_out"; then
    echo "C and C# agree on $(wc -l < "$c_out") reported values"
else
    echo "C and C# DISAGREE"
    exit 1
fi
