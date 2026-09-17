# Supplied phonemes over list-valued options — adopting upstream #577

Upstream merged [0xShug0/audio.cpp#577](https://github.com/0xShug0/audio.cpp/pull/577),
which adds Kokoro's `phonemes` request option — the first family to read the
list-valued options this branch already binds ([#566](https://github.com/0xShug0/audio.cpp/pull/566)).
This branch's `SetOptionArray` and its tests were written against the PR as first
posted; two rounds of review changed the behaviour underneath them before it merged.
This log covers moving the pin onto the merged commit and reconciling the tests with
what actually landed.

## Run 1 — 2026-09-17 — what the merged version does that the posted one did not

Question: does anything on this branch now assert the wrong thing?

`gh pr view 577 --repo 0xShug0/audio.cpp --json commits,files`, then reading
`df0e09e:src/models/kokoro_tts/session.cpp`. Three behaviours arrived after review:

1. **A published package takes phonemes its spec predates.** A GGUF's embedded
   schema-v1 contract is authoritative, and the shipped `kokoro-82m-q8_0.gguf` was
   written before the option existed. Rather than wait for a regenerated package, the
   engine drops the key from its *validation copy* when the contract does not declare
   it (`validate_request_options`, same shape as `irodori_tts.codec_backend`).
   Unrelated unknown options still fail.

2. **Set-but-empty is a caller error, not a fallback.** An empty list, or an empty
   entry among good ones, used to fall through to the built-in G2P and speak the
   source text with no error. `["həlˈO", ""]` rendered the phonemes and then read
   "placeholder" aloud. Both are now refused; the override is
   `std::optional<std::string_view>` so absent and empty are different things.

3. **A failure names its entry.** `Kokoro supplied phoneme entry 200: Kokoro vocab is
   missing phoneme symbol: R`, reported from `prepare()` — every entry is sized before
   anything is rendered, so a bad entry in a long list costs 0.00s.

Implication for this branch: the `phonemes`-declaration probe in
`CheckSuppliedPhonemes` was the one thing that had to change. It skipped the whole
check when the package did not declare the option — which, after (1), is precisely
the package everyone has, so the test would have skipped itself into silence on the
default install and only run under `AUDIOCPP_KOKORO_SPEC`. It now reports which side
it ran and runs either way. (2) and (3) are new assertions; nothing on the branch
contradicted them, because nothing on the branch covered them.

The pin was at v0.8.0 (`4af1432`), which predates both #566 and #577 — so
`audiocpp_request_set_option_array` did not exist in the built library and these
tests had never run against an engine that could serve them. Pin moved to `df0e09e`.

## Run 2 — 2026-09-17 — the pin bump also brought #576, and we were not using it

Question: the same pin jump includes
[#576](https://github.com/0xShug0/audio.cpp/pull/576) (report what a CUDA build
costs; opt-in `--ccache`). Did the rebuild above take either?

No, on both counts, and the build directory hid it. Configure printed the advisory:

```
-- Using CMAKE_CUDA_ARCHITECTURES=50-virtual;61-virtual;...;120a-real  ... NATIVE=86-real
--   portable default: 9 architectures, so every .cu file is compiled 9 times
--   this host is 86-real; for a local build roughly 3x faster:
--       -DCMAKE_CUDA_ARCHITECTURES=native
```

Three findings:

- The rebuild was **not CPU-only**. `ENGINE_ENABLE_CUDA:BOOL=ON` was sticky in the
  existing `CMakeCache.txt`, so passing no backend flag inherited a CUDA build —
  nine architectures, which is #576's own 1361 s measurement on this class of host.
- `CMAKE_C/CXX_COMPILER_LAUNCHER=ccache` were in the cache from an earlier manual
  configure; `CMAKE_CUDA_COMPILER_LAUNCHER` was not. The expensive half cached
  nothing. Upstream's hint gates on C/CXX only (`CMakeLists.txt`, the
  `AUDIOCPP_CCACHE_PROGRAM` block), so it stays **silent** in exactly that state —
  which is why nothing said so.
- `scripts/build-engine.sh` offered neither knob. #576's `--ccache` and
  `--cuda-arch` are in audio.cpp's `scripts/build_linux.sh`, which this repo never
  calls: like the release path, it invokes cmake directly.

Both flags added to `scripts/build-engine.sh` in upstream's shape — opt-in, no
default changed, an environment launcher left alone and reported, `-U` before the
sticky `CMAKE_CUDA_ARCHITECTURES`, and ccache set for CUDA as well as C/C++. The
banner reads both back out of the cache rather than from the flags, so a rebuild
that passes nothing still reports what it inherited:

```
backends: CUDA=ON
compiler launcher: CXX=ccache CUDA=ccache
cuda architectures: native (1 entries)
```

Build restarted as `--ccache --cuda-arch native -DENGINE_ENABLE_CUDA=ON`. 790
targets became 147: the arch change invalidates every `.cu` object, the rest were
already up to date. This run is still cold for CUDA — nothing in the ccache was
compiled through the launcher — so the caching payoff is the *next* rebuild, not
this one.

## Run 3 — 2026-09-17 — the first run these tests have ever had

Question: with the pin on `df0e09e` and the engine rebuilt, do the branch's option-array
tests pass?

Not at first. `./scripts/run-tests.sh /mnt/data/models/audiocpp` died in
`CheckSuppliedPhonemes` before reaching a single assertion:

```
AudioCpp.AudioCppException: audiocpp_model_load: model spec override path does not exist:
   at ...Program.CheckSuppliedPhonemes(...) Program.cs:line 149
```

`AUDIOCPP_KOKORO_SPEC` unset became `ModelSpecOverride = ""`, and the ABI reads an empty
override as a path, then rejects it for not existing. Only `null` means "unset".
This is the direct evidence that the branch's tests had never executed: the crash is on
the first line that touches the ABI. Fixed by mapping empty to `null`.

(One false alarm worth recording: re-running `dotnet run` for the model test from the
repo root gives `cannot read assets/resources/sample_16k.wav`. The sample paths are
relative to the engine tree, which `run-tests.sh` `cd`s into — the runner is the entry
point, not a convenience wrapper.)

### Three sides, not two

`--- side 1: shipped package, no override ---`

```
option arrays: package declares a 'phonemes' request option: no
option arrays: supplied phonemes ok
ran=6 skipped=0 failures=0
C and C# agree on 11 reported values
```

`no`, and every assertion still passes: this is the validation-copy relaxation from #577
carrying a package whose embedded contract predates the option. Under the skip logic
this branch had before today, this line read "skipping" — on the package everyone has.

`--- side 2: shipped package + AUDIOCPP_KOKORO_SPEC ---`

```
option arrays: package declares a 'phonemes' request option: yes
option arrays: supplied phonemes ok
ran=6 skipped=0 failures=0
C and C# agree on 11 reported values
```

Identical, so relaxing the contract check relaxed nothing else.

`--- side 3: a package that declares it in the file, which #579 made reachable ---`

[0xShug0/audio.cpp#579](https://github.com/0xShug0/audio.cpp/pull/579) is what makes this
side exist. A re-encode used to drop every `voices/*.bin` sidecar to a filter meant for
source trees, exit 0, and report success — so re-emitting the package with a newer spec
produced a file with no voices and no way back, since the installed directory holds only
the `.gguf`. Carrying them by name from the input fixed it. Built `audiocpp_gguf` (not
one of the three targets `build-engine.sh` builds) and re-emitted:

```
note: reusing the 59 sidecars embedded in .../kokoro-82m-q8_0.gguf
embedded_sidecars=true
embedded_sidecar_count=59      (7.6s)
```

59 of 59, into `/mnt/data/models/audiocpp-respec/`. That is the state every package
reaches once regenerated, and the one the relaxation exists to cover until then.

Against that re-emitted root, with no override:

```
option arrays: package declares a 'phonemes' request option: yes
option arrays: supplied phonemes ok
ran=2 skipped=4 failures=0
C and C# agree on 4 reported values
```

`yes` from the file itself rather than from a load-time override. `ran=2 skipped=4`
is expected — that root holds only Kokoro. All three sides agree, so the option
behaves the same whether the contract declares it, is overridden into declaring it,
or never declares it at all.

### Conclusion

The branch is sound; what it lacked was an engine that could run it. Changes made:
the pin (`4af1432` → `df0e09e`), the declaration probe (skip → report),
`ModelSpecOverride` empty → `null`, three new assertions for the post-review
semantics (empty list, empty entry by index, entry named in a long list), and the
ABI-minor note on `SetOptionArray`.
