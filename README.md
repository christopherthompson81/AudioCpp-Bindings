# AudioCpp-Bindings

.NET bindings for [audio.cpp](https://github.com/0xShug0/audio.cpp) over its C ABI,
and a desktop app built on them.

The C ABI these bind to was contributed upstream and merged as
[0xShug0/audio.cpp#530](https://github.com/0xShug0/audio.cpp/pull/530). The bindings
live here rather than in that tree because the proposal that led to it
([#525](https://github.com/0xShug0/audio.cpp/issues/525)) deliberately scoped language
bindings out — the ABI belongs upstream, the language bindings do not.

## The app

`src/AudioCpp.Bindings.Gui` is an Avalonia desktop application that mirrors audio.cpp's
web UI: the same seven workflows (text to speech, transcription, music and video
generation, voice conversion, source separation, audio analysis, voice design), the same
model manager for browsing and installing packages, and its interface strings imported
from the web UI's own language files so the two read alike in English, Italian, Polish,
Russian and Simplified Chinese.

It also embeds a server that reimplements audio.cpp's HTTP API in C# — the OpenAI-shaped
speech and transcription routes, alignment, voices, live ingest, model management and the
`/v1/ui` endpoints — so an existing client can be pointed at it unchanged. That server
exists as much to exercise the bindings as to be useful: serving the same API as the
reference implementation is what turns "the binding compiles" into "the binding behaves",
and it has found several ABI limitations that nothing else surfaced.

| | |
|---|---|
| `src/AudioCpp` | the binding — `net10.0`, AOT-compatible, no package references |
| `src/AudioCpp.Packages` | the model catalogue and package installer |
| `src/AudioCpp.Server` | audio.cpp's HTTP API, reimplemented |
| `src/AudioCpp.Bindings.Gui` | the Avalonia app |
| `tests/AudioCpp.PathTest` | the ABI contract, offline and streaming |
| `tests/AudioCpp.ModelTest` | real families across five task types |
| `tests/AudioCpp.ServerTest` | the HTTP surface, against real models |
| `scripts/run-tests.sh` | runs the tests and checks they agree with the C tests |

## Prerequisites

audio.cpp itself is a submodule, pinned to the commit these bindings are built and
tested against, so the engine version is recorded in the tree rather than chosen by
whoever runs the build:

```bash
git clone https://github.com/christopherthompson81/AudioCpp-Bindings
cd AudioCpp-Bindings
./scripts/build-engine.sh          # add -DENGINE_ENABLE_CUDA=ON, or any other cmake flag
```

Two flags are worth knowing for a local rebuild, both opt-in and neither changing
what is built:

```bash
./scripts/build-engine.sh --ccache --cuda-arch native -DENGINE_ENABLE_CUDA=ON
```

`--ccache` sets a ccache launcher for C, C++ *and* CUDA. The engine forces ggml's
`GGML_CCACHE` off, so without this nothing is cached at all; a launcher already set
in the environment is left alone. `--cuda-arch native` compiles for this host's GPU
instead of the portable nine-architecture list, which is most of the cost of a CUDA
build — and makes the result run on this machine only, so it is for development, not
for anything shipped.

That builds `libaudiocpp` with the C ABI enabled into `external/audio.cpp/build/bin`,
where the binding looks for it by default — no environment variable, and no second
checkout to keep in step. To build against your own audio.cpp instead, point
`AUDIOCPP_NATIVE_DIR` at its `build/bin`; an explicit setting always wins. The pin is
the version that is tested, not the only one that works.

The build is CPU-only unless a backend is asked for. Use audio.cpp's own
`ENGINE_ENABLE_CUDA`, not ggml's `GGML_CUDA`: the engine *forces* `GGML_CUDA` from
`ENGINE_ENABLE_CUDA`, and `GGML_CUDA` only seeds that option's default, which
`option()` ignores once the cache exists. So `-DGGML_CUDA=ON` works on a fresh
configure and silently does nothing on a rebuild — cmake reports success, ninja
finds no work, and the library stays CPU-only. The app then fails at run time with
"CUDA backend requested but it is not registered in this build", which names
neither the flag nor the build.

A plain clone, not `--recurse-submodules`: that recurses into audio.cpp's own
submodule, whose URL is SSH-only, and the clone aborts for anyone without access
to it. The C ABI does not need it, so `build-engine.sh` fetches the engine and
nothing below it. If you have already cloned recursively and seen it fail, the
engine is checked out regardless and the build script will carry on from there.

Models come from audio.cpp's own model manager:

```bash
python3 external/audio.cpp/tools/model_manager_v2.py install kokoro_82m_q8_0 --models-root /path/to/models
```

## Running it

```bash
dotnet run --project src/AudioCpp.Bindings.Gui
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
dotnet run --project src/AudioCpp.Bindings.Gui -- --smoke \
    /path/to/models/Kokoro-82M-GGUF/kokoro-82m-q8_0.gguf "" tts kokoro_tts
```

Exit codes follow CTest: 0 pass, 1 fail, 77 skip.

## Installing it

```bash
./scripts/build-engine.sh -DENGINE_ENABLE_CUDA=ON   # or without, for CPU
./scripts/build-native.sh                           # the audio shim
./install.sh                                        # ~/.local/share/audiocpp-studio
```

That publishes the app self-contained, registers icons and a `.desktop` entry, and
copies the engine, the audio shim, the model specs and the VAD weights in beside it —
laid out exactly as they sit in a checkout, which is why an installed copy finds them
with no environment set and no code that knows about being installed.

`./uninstall.sh` reverses it, keeping settings unless given `--purge`, and never
touching downloaded models. `./package-macos.sh` builds a real `.app`;
`install-windows.ps1` and `uninstall-windows.ps1` do the same job on Windows. See
[docs/packaging.md](docs/packaging.md).

## Tests

```bash
./scripts/run-tests.sh /path/to/models
./scripts/run-tests.sh /path/to/models --backend cuda
```

Runs both C# tests and then checks the bindings report exactly what audio.cpp's own C
tests report for the same models — including against the C test binaries from the same
pinned build, so both languages are driving the same engine. Without a models argument
it runs only the path test, which needs no downloads.

`--backend` picks the compute backend both languages are driven with, `cpu` by default
and anything the build has otherwise; both get the same one, so the cross-language diff
keeps comparing like with like. `--threads` and `--build-dir` are there for a thread
count other than this machine's and for an engine built somewhere else.

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

## Licence

Apache 2.0, the same licence as audio.cpp — see [LICENSE](LICENSE).

`NOTICE` records what is taken from elsewhere: the Italian, Polish, Russian and
Simplified Chinese interface strings are imported verbatim from audio.cpp's web UI
language files, and `native/audioio/miniaudio.h` is vendored under its own terms.
