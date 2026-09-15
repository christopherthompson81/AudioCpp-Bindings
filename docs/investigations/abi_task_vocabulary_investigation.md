# ABI task vocabulary + transcript language — adopting upstream #544

Upstream merged [0xShug0/audio.cpp#544](https://github.com/0xShug0/audio.cpp/pull/544),
which was filed from this repo to close the ABI gaps recorded in issues #47, #49,
#50 and #65. This log covers moving the pin onto it, replacing the local
workarounds with the real ABI, and confirming the four issues are actually closed
by what landed rather than by what the PR description says.

## Run 1 — 2026-09-14 — what landed, and what it closes

Question: which of the open `upstream` issues does the merged PR actually close?

`gh pr view 544 --repo 0xShug0/audio.cpp --json files` — eleven files, the ones
that matter being:

```
include/audiocpp.h                                   +64 -3
include/engine/framework/runtime/task_vocabulary.h    +56 -0
src/framework/runtime/task_vocabulary.cpp             +88 -0
src/capi/audiocpp.cpp                                 +42 -0
model_specs/miocodec.json                              +0 -1
model_specs/moss_voicegen.json                         +1 -1
```

Four new entry points:

```c
size_t          audiocpp_task_count(void);
const char *    audiocpp_task_name(size_t index);
const char *    audiocpp_task_from_spec_name(const char * spec_task);
audiocpp_status audiocpp_request_set_text_language(audiocpp_request *, const char *);
```

Mapping to our issues:

| issue | closed by | how |
| --- | --- | --- |
| #47 specs declare task names the engine rejects | yes | `moss_voicegen` `vdes`→`design`; `miocodec` drops `codec` |
| #49 header documents task spellings that do not work | yes | header now lists the real fourteen tokens, drops `"diarization"`/`"alignment"` |
| #50 no ABI way to map spec task → ABI token | yes | `audiocpp_task_from_spec_name` + the enumerator |
| #65 no ABI way to set transcript language alone | yes | `audiocpp_request_set_text_language` |
| #48 packages with no download block | no | untouched by this PR |
| #68 `transcript.text.delta` cumulative vs incremental | no | untouched by this PR |

So #48 and #68 stay open; the other four are candidates for closing once this
side is actually built and tested against the new pin, not before.

Implication for the next step: the pin moves, then three things in this repo
become deletable or wrong — `SpecTasks.cs` (the hardcoded table #50 said to
delete), the `SupportsTask` comment in `CatalogEntry.cs`, and the `--workflow-check`
guard in `LiveCheck.cs` that was written against that table.

### Header wart spotted while reading

`include/audiocpp.h` now carries **two** stacked comment blocks on
`audiocpp_request_set_text` (lines 274-277 and 278-289) — the old one describing
the coupling and the new one describing how to undo it. The second supersedes the
first. Ours to have left behind; worth a follow-up upstream, not a blocker here.

## Run 2 — 2026-09-14 — pin move and engine rebuild

Question: does the new upstream head build in the configuration this repo tests in?

Pin moved `3eccab5` → `b46fe6b` (upstream master head), not to the merge commit
`7d5dc97` itself. Three commits ride along: #547 (32-bit context sizes), #522
(Higgs error text), #549 (chunked Qwen prefill with backend KV cache reuse). #549
is the only substantial one and it touches the Qwen path.

```
./scripts/build-engine.sh -DENGINE_ENABLE_CUDA=ON
```

CUDA because that is what the existing build directory was configured with
(`ENGINE_ENABLE_CUDA:BOOL=ON` in `CMakeCache.txt`), so the rebuild stays
comparable to what was passing before.

Result: pending — appended below when it finishes.

Result: exit 0. CUDA registered (`found 1 CUDA devices … RTX 3090`), so the
rebuild is comparable to the one that was passing before the pin moved.

## Run 3 — 2026-09-14 — replacing the workarounds with the ABI

Question: what on this side was standing in for the missing entry points, and
does the ABI actually answer the same questions?

Four changes, each deleting a workaround rather than adding a feature:

1. **`SpecTasks.cs` deleted.** The hardcoded spec→ABI table #50 said to delete,
   plus its `Unmappable` allowlist — which existed only to excuse the two broken
   specs #47 reported, both now fixed upstream. Replaced by `AudioCppTasks`
   (`src/AudioCpp/AudioCppTasks.cs`), a pass-through to `audiocpp_task_count` /
   `audiocpp_task_name` / `audiocpp_task_from_spec_name`. Spec-name lookups are
   memoised per process: `CatalogEntry.SupportsTask` is called once per visible
   row per tab switch, and the engine's answer cannot change within a process.

2. **`LiveCheck.cs --workflow-check` guard re-pointed.** Its meaning changes, and
   that is the interesting part. It used to ask "does this repo's table know
   this spec name?", where a miss was ambiguous — an upstream addition we had
   not tracked, or an upstream bug. Asking the engine makes a miss unambiguous:
   the engine itself cannot turn that name into a task kind, so the package
   would fail to load. That is worth failing the check on, so the `Unmappable`
   excuse list is gone rather than ported.

3. **`AudioCppRequest.SetTextLanguage`** added over
   `audiocpp_request_set_text_language`.

4. **The server stops trading one language for the other.** This is #65's
   workaround, and it cost more than the issue recorded:
   - `Routes.cs` — the learned-refusal retry stays, because whether a model
     wants `options["language"]` still cannot be asked in advance. What changes
     is that a refusal no longer takes the transcript language down with it:
     every route now sets the transcript language unconditionally and lets only
     the *option* be withdrawn. Previously "accepting that a model which does
     not declare the option gets no transcript language either" was the
     documented cost.
   - `Streaming.cs` — the live transcription route was setting empty text purely
     to carry a language (`SetText("", language)`), the only way through the old
     ABI, which sent the option as a side effect. There is no retry harness on a
     live route, so `?language=` against a strict family like `parakeet_tdt`
     failed outright. Now `SetTextLanguage`.
   - `Streaming.cs` live **speech** deliberately keeps `SetText("", language)`:
     speech families that read a language read it from `options["language"]` the
     way `--language` delivers it, so sending the transcript language alone
     would quietly stop reaching them. The asymmetry is commented at both sites.

## Run 4 — 2026-09-14 — the suite against the new pin

```
./scripts/run-tests.sh /mnt/data/models/audiocpp
```

Binding coverage first, since it is the check that would catch an entry point
added upstream and left unbound:

```
declared: 73   bound: 73
every declared entry point is bound (73 symbols)
```

73 rather than 69 — the four new entry points are bound, and nothing is bound
that the header does not declare.

`server OK`, `package catalog OK`, `capture OK`, `c# path test OK`.

### An aside that keeps #48 open

The package catalog reports `downloadable: 229 of 237`, which looks like #48
("137 of 235 packages have no download block") fixed itself. It has not —
that number counts packages this repo can derive a URL for, including from the
`repo` field. Counting explicit `download` blocks directly:

```
$ python3 -c "...count packages with no 'download' key..."
packages: 237  without download block: 186
```

So 186 of 237, against 137 of 235 when #48 was filed. The gap grew with the
catalogue rather than closing. #48 stays open, and the 229 figure is not
evidence about it either way.

### The model test died, and it was my fault, not the pin's

The first full run exited 1, dying silently inside `parakeet_tdt` — no exception,
no stack, output stopping after the declared-options dump. Nothing in the log
said why.

Wrong first instinct was to suspect the three commits riding along with the pin
(#549's chunked Qwen prefill especially). Checked that instead of assuming it:

```
$ ./build/bin/audiocpp_c_api_model_test /mnt/data/models/audiocpp … 16
ran=5 skipped=0 failures=0
c api model test OK          # exit 0, parakeet transcribed correctly
```

Upstream's own C test drives the same library through the same ABI and was fine,
which rules out the engine. Then the C# model test on its own:

```
ran=5 skipped=0 failures=0
c# model test OK             # exit 0
```

Also fine. The difference was that during the first run I was editing
`Routes.cs`, `Streaming.cs`, `TaskRunRequest.cs` and `Alignment.cs` while
`run-tests.sh` was partway through it — `dotnet run --no-build` against
assemblies being rewritten underneath it. Self-inflicted, and worth writing down
because the failure looked exactly like an engine regression: a silent native
death in the one family the riding-along commits could plausibly have touched.

Also worth noting the harness hid it: `run-tests.sh` exited 1, but the wrapper
`echo "exit=$?"` I piped it through reported its own status, so the run was
announced as passing. Read the suite's own exit line, not the wrapper's.

## Run 5 — 2026-09-14 — clean suite

Re-run with nothing else touching the tree:

```
== C vs C# ==
C and C# agree on 11 reported values
SUITE exit=0
```

Every stage green, the cross-language parity check included.

## Run 6 — 2026-09-14 — the behaviour #50 was actually about

Question: does the picker now show the packages it was hiding?

`--workflow-check` needs `--live-check` alongside it to arm at all (`LiveCheck.Arm`
is only called for `--live-check`); run without it the app just opens a window
and sits there, exits 0, and prints none of the check's output. That reads as a
pass.

```
$ dotnet run --project src/AudioCpp.Bindings.Gui -- \
    --live-check --workflow-check modelsRoot=/mnt/data/models/audiocpp

7 workflows, 14 engine tasks
  tts       Text to speech         tasks [tts clon]  -> tts   133 package(s)
  asr       ASR / Transcription    tasks [asr]  -> asr         47 package(s)
  music     Music generation       tasks [gen]  -> gen         33 package(s)
  vc        Voice conversion       tasks [vc svc s2s] -> vc    19 package(s)
  sep       Source separation      tasks [sep]  -> sep          6 package(s)
  analysis  Audio analysis         tasks [vad diar align spk midi] -> vad  13 package(s)
  design    Voice design           tasks [vdes] -> vdes        34 package(s)
  engine task tokens: vad asr diar sep gen tts clon vc s2s align vdes spk svc midi
  spec vocabulary in use: align asr clone design diar edit midi music s2s sep sfx svc tts vc
workflows OK
```

Three things this confirms, none of which the unit tests would have:

- **Music generation: 33 packages.** #50 reported 0. Voice design: 34, where #50
  reported 1 of 33. The counts come from `audiocpp_task_from_spec_name` now
  rather than from the deleted table, so they are the engine's own answer.
- **`engine task tokens` is fourteen, enumerated from the ABI** — the thing #49
  said a caller had no way to ask for.
- **`spec vocabulary in use` contains neither `vdes` nor `codec`.** Those were
  #47's two broken specs; `moss_voicegen` now says `design` and `miocodec` no
  longer says `codec`, so the guard has nothing to excuse and the `Unmappable`
  list was right to delete rather than port.

`no package declares 'vad'` / `'spk'` are pre-existing and unrelated — the VAD
model ships with the engine rather than as a catalogue package.

## Outcome

Pin `3eccab5` → `5db449e`. Issues #47, #49, #50 and #65 are closed by what
landed, verified against the built engine rather than the PR description. #48 and
#68 remain open and untouched.

## Run 7 — 2026-09-15 — re-pinned before proposing

The branch had sat unpushed while the upstream work went on, and upstream moved:
`b46fe6b` → `5db449e`, one commit, our own #550 (the safetensors `files` lists).
Re-pinned to current main rather than proposing one commit behind, and re-ran
everything rather than assuming a spec-only change was inert:

```
declared: 73   bound: 73
downloadable: 229 of 237
ran=5 skipped=0 failures=0
C and C# agree on 11 reported values
SUITE exit=0
```

Unchanged from the `b46fe6b` run, which is what #550 being `model_specs`-only
predicted — but the prediction was worth one suite run to confirm, since the
package catalogue reads those specs.

## Run 8 — 2026-09-15 — the server language paths, finally exercised

Reviewing the PR turned up a hole in the verification rather than in the code.
Every suite run so far had printed:

```
no AUDIOCPP_ASR_MODEL/AUDIOCPP_ASR_AUDIO; skipping transcription
no AUDIOCPP_ALIGN_MODEL/AUDIOCPP_ALIGN_AUDIO/AUDIOCPP_ALIGN_TEXT; skipping alignment
```

Those are the two routes #65 is about. The suite was green across five families
and had never run the code this branch changes most.

Re-ran `AudioCpp.ServerTest` with the exact pair the issue turns on — Parakeet
TDT as the ASR model (refuses a `language` option it does not declare) and
Qwen3's forced aligner (declares none and requires the transcript language
anyway):

```
ok    a language the model may not declare does not break the request
      {"text":"Some call me Nature. Others call me Mother Nature. ..."}
transcription OK
ok    alignment is 200
      {"text":"...","language":"en","words":[{"word":"Some","start":0.4,...}]}
ok    a model that needs a language says so rather than aligning wrongly  500
alignment OK
SERVERTEST exit=0     130 assertions, 0 failures
```

Both sides of #65's dilemma served by the same server, which is the thing the
issue said was impossible through the old ABI.

### Confirming the retry actually fired

The transcription request succeeded and no "refuses the 'language' request
option" line appeared, which could mean either the retry worked or the option
was never refused in the first place. Pool logs *are* captured elsewhere in the
run (`loaded second (parakeet_tdt) in 1633 ms`), so the absence was suspicious.

Settled it directly:

```
$ audiocpp_cli --task asr --family parakeet_tdt --model ... --request-option language=en
audiocpp_cli failed: unknown Parakeet TDT request option: language
```

So Parakeet does refuse it. A 200 with a correct transcript against that model
is therefore only reachable through the refusal-and-retry path — it ran, and the
log simply is not surfaced in the transcription test's captured output. Worth
recording: "no log line" was not evidence of "no refusal", and the check that
settled it took one CLI invocation.
