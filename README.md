# AudioCpp-Bindings

.NET bindings for [audio.cpp](https://github.com/0xShug0/audio.cpp) over its C ABI,
plus an Avalonia sample that uses them.

The C ABI itself is proposed upstream in
[0xShug0/audio.cpp#530](https://github.com/0xShug0/audio.cpp/pull/530). These bindings
live here rather than in that tree: the upstream proposal
([#525](https://github.com/0xShug0/audio.cpp/issues/525)) deliberately scoped language
bindings out, and the maintainer would rather not carry them.

| | |
|---|---|
| `src/AudioCpp` | the binding — `net10.0`, AOT-compatible, no package references |
| `tests/AudioCpp.PathTest` | the ABI contract, offline and streaming |
| `tests/AudioCpp.ModelTest` | real families across five task types |
| `samples/AudioCpp.Demo` | an Avalonia app driving the ABI |
| `scripts/run-tests.sh` | runs both tests and checks they agree with the C tests |

## Prerequisites

A built `libaudiocpp` from an audio.cpp checkout with the C ABI enabled:

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DAUDIOCPP_BUILD_C_API=ON
cmake --build build --target audiocpp
```

Point the binding at it with `AUDIOCPP_NATIVE_DIR`, or place the library beside the
managed assembly. Models come from audio.cpp's own model manager:

```bash
python3 tools/model_manager_v2.py install kokoro_82m_q8_0 --models-root /path/to/models
```

## The sample

```bash
export AUDIOCPP_NATIVE_DIR=/path/to/audio.cpp/build/bin
dotnet run --project samples/AudioCpp.Demo
```

Load a model, and the left pane fills in from the model itself — family, capabilities,
languages, and every option it declares with type, default and bounds. None of that is
hardcoded per family; it comes from `ModelInspection::cli` through the ABI, which is
what lets one UI drive sixty-odd model families without knowing anything about them.

Then run a task. ASR and alignment give transcripts and word timings, diarization gives
speaker turns, VAD gives segments, separation gives named streams, and TTS gives audio
with a waveform preview and a Save WAV button. The session is created once per
task/backend/thread combination and reused across runs, which is the reason to embed
rather than shell out to `audiocpp_cli`.

A GUI cannot be driven headlessly, so the same view model has a `--smoke` entry point
that exercises every ABI call without a window:

```bash
dotnet run --project samples/AudioCpp.Demo -- --smoke \
    /path/to/models/Kokoro-82M-GGUF/kokoro-82m-q8_0.gguf "" tts kokoro_tts
```

Exit codes follow CTest: 0 pass, 1 fail, 77 skip.

## Tests

```bash
./scripts/run-tests.sh /path/to/audio.cpp/build /path/to/models
```

Runs both C# tests and then checks the bindings report exactly what audio.cpp's own C
tests report for the same models. Without a models argument it runs only the path test,
which needs no downloads.

## Notes

**Disposal order does not matter.** A session holds its model, which holds its registry,
so the ABI keeps parents alive. That is why the C ABI was built that way: .NET finalizer
order is not deterministic, so an API requiring an order would be unusable from here no
matter how carefully a caller wrote their `using` statements. `AudioCpp.PathTest`
disposes the model and registry and then keeps using the session.

**Everything is copied on the way out.** Every `const char *` and `const float *` the ABI
returns is borrowed from the handle that produced it, so accessors copy rather than wrap.
Returned strings are `IntPtr` in the interop layer for the same reason — letting the
marshaller build a `string` from a return value would also let it free a pointer it does
not own.

**Errors carry the native detail.** A non-OK status becomes an `AudioCppException` with
`Status` and `Detail`. The detail is thread-local natively and a later call may clear it,
so it is read synchronously on the calling thread.

**Threads.** The native library never calls `omp_set_num_threads` — process-global state —
so `BackendConfig.Threads` is the only place it is set.
