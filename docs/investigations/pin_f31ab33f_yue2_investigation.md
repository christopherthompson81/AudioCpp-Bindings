# Pin bump to f31ab33f, and YuE2 in the app

Engine pin moved 487800f5 → f31ab33f (111 upstream commits). The question
beyond the bump: what does the app need to offer YuE2 the way upstream's web
UI does (its dedicated `Yue2Panel.svelte`), rather than as a generic family?

## Run 1 — 2026-10-06 ~11:40

Question: does the move need binding changes, and what changed for YuE2?

```
git diff --stat 487800f5..f31ab33f -- include/audiocpp.h src/capi
git log --oneline 487800f5..f31ab33f -i --grep=yue
```

The C ABI is untouched. YuE2 engine changes are small: #767 lowers the six
host context/arena defaults from 1.5–6 GiB to 32 MiB each (the old NAR build
context was a single ~6 GiB host allocation that aborted long songs under
memory pressure; #816 documents it), plus `Ar`→`AR` type renames. Nothing the
app names.

The web UI's YuE2 panel predates the old pin (it was already there at
487800f5); #90 synced the generic parts but the app still treats YuE2 as one
more family. What the panel does, against what the app does today:

| web UI panel | app at 487800f5 |
| --- | --- |
| multi-line lyrics box, optional (instrumental since 88cd5712) | `lyrics` is a one-line request option; the generic Text box is also there and the engine reads it as a lyrics fallback; an empty Text box is refused ("Enter some text first."), so instrumental is impossible |
| style as the prompt | `style` is a one-line option among ten |
| ABC score editor (multi-line) | one-line `abc` box |
| semantic and ABC-planner sampling, 14 controls | **absent** — see below |
| AR/NAR LoRA with Browse | already: `IsPath` + Browse |
| main/VAE component pickers | already (#90) |
| SheetSage2 cover: song → ABC → conditioning | absent; SheetSage2 not installed here |
| abcjs sheet preview | absent; no ABC renderer in Avalonia |

**The sampling options are missing because the ABI does not declare them.**
`audiocpp_model_options` reports `yue2_cli_interface()` (`session.cpp`), which
lists 10 request options. `model_specs/yue2.json` lists 27, and
`request.cpp:apply_options` reads all of them (`<prefix>_temperature`,
`_top_p`, `_top_k`, `_repetition_penalty`, `_penalty_window`, `_min_tokens`,
`_max_tokens` for `abc` and `semantic`; also `semantic_prefix[_file]`,
`nar_noise_file`). The web UI gets them from its own hand-written
`model_params.json`.

Also seen: the spec says `cot` defaults to `full`, the ABI says `off`.

Implication: the app can read the spec's request options (it already parses
model_specs for the catalogue) and offer those the ABI omits. That is generic,
not a YuE2 table. Lyrics and a required style prompt get dedicated boxes.

## Run 2 — 2026-10-06 ~12:10

What was built, then checked headlessly:

- `Catalog` parses each spec's `options.request` (enum `values` joined with
  `|`, so they become choices the same way ABI types do).
- `DeclaredOption.SpecOnly` keeps the spec options the ABI did not name (bare
  name or `family.` prefix), flagged `FromSpec`; the app shows them in their own
  "More options from the model spec" expander.
- `PromptOption`: a required request option called style/prompt/caption/tags
  takes the Text box's place for generation. Only YuE2 has one, so only YuE2
  loses the Text box. `LyricsOption`: any `lyrics` request option gets a
  multi-line box (YuE2, MiniMax Music 3, HeartMuLa).
- `abc`, `lyrics`, `*_prefix` edit as multi-line text; an ABC artifact gets
  "Use as score", which fills `abc` and moves `cot` off `off`.
- Upstream added `lang_ja.json`; the importer picked it up, so Japanese is in
  the picker now.

```
dotnet run --project src/AudioCpp.Bindings.Gui -- --spec-options-check
```

All 14 checks pass. Against the real spec and the ABI's 10 YuE2 request
names, the spec-only set is 17: `semantic_prefix`, `semantic_prefix_file`,
`nar_noise_file`, and the 7 `abc_*` and 7 `semantic_*` sampling controls.
Nothing the ABI declares comes back twice.

## Run 3 — 2026-10-06 ~12:40

```
./scripts/build-engine.sh --ccache --cuda-arch native -DENGINE_ENABLE_CUDA=ON
./scripts/run-tests.sh /mnt/data/models/audiocpp --backend cuda
```

Clean build, 1017 steps. `ran=7 skipped=0 failures=0`, C and C# agree on 11
reported values, the same as at the previous pin.

## Run 4 — 2026-10-06 ~12:50 (YuE2 through the view model, CUDA)

```
dotnet run --project src/AudioCpp.Bindings.Gui -- --live-check --shot --yue2-check \
  model=/mnt/data/models/audiocpp/Yue2-3B-GGUF backend=cuda task=gen
```

- Empty style: refused before the engine with "Enter the style first."
- `cot=full`, `stop_after=abc`, short lyrics, seed 7: 4.9 s, one 688-byte ABC
  artifact (`X:1 … V: Vocal clef=treble`).
- "Use as score" filled `abc` with exactly the artifact's text.
- Then empty lyrics (instrumental), `stop_after=audio`, and the spec-only
  `semantic_max_tokens=250`: 3.5 s, **10.0 s of audio**. That is 250 tokens at
  25 Hz, so the spec-only option did reach the engine. At the default (9000)
  the same song ran to ~80 s at the last pin.

All checks pass.

## Run 5 — 2026-10-06 ~12:55 (the window)

Screenshot of the music workflow with YuE2 loaded. **Style and Lyrics appeared
twice**: in their new boxes and again among the option rows. The check had
passed because it set `task=gen` before loading, but this run passed
`task=music`. That is a workflow, so `Task` was still not `gen` when the rows
were built at load. `PromptOption` and `LyricsOption` only matched on the gen
task, so the rows kept both options.

Fix: find the two options regardless of task; `ShowPrompt`, `ShowLyrics` and
the run guard follow the task. The check now loads with `task=music` and
switches to ASR and back. Rerun: all pass. The second screenshot shows each
option once and `abc` as a multi-line box.

Left as is:

- The optional generation source clip is still offered to YuE2, which refuses
  `audio_input` with an engine error. Nothing in the ABI or spec says which
  generation families take a clip.
- No SheetSage2 "song → ABC" cover step: SheetSage2 is not installed here, so
  it could not be verified. Its ABC artifact would get "Use as score" like
  YuE2's own, but only within one loaded model's results.
- No sheet-music preview: the web UI renders ABC with abcjs, and Avalonia has
  no equivalent.

## Run 6 — 2026-10-06 ~14:00 (review of #91)

`/code-review high` raised ten findings. Verified against the code and the
engine source before fixing:

- **Use as score broke the score-only loop.** `request.cpp:192` refuses
  `stop_after=abc` with a score supplied, and a score-only run is how the score
  was made. UseScore now moves `stop_after` to `audio`; the live check no
  longer does it by hand, so it proves the button does.
- **Spec-only numbers with no default showed 0** (max tokens 0, a temperature
  slider at 0), and once set could not be unset. `NumberValue` is now nullable:
  empty means the model decides, and clearing returns to that. A bounded
  number with no default gets a spinner, not a slider, which would sit at its
  minimum and send it on the first touch.
- **The spec merge could offer options the engine refuses.** Confirmed: most
  families' ABI lists come from the GGUF's embedded spec via `cli_from_spec`
  (`metadata.cpp:262`), copying descriptions verbatim, and families like AuK
  and Parakeet call `validate_spec_backed_request_options`. With a model whose
  embedded spec is older than this checkout, the extras would fail the run.
  Measured: YuE2's hand-written list matches its spec on 1 description of 10
  (`seed`); a copied list matches all. `SpecOnly` now offers nothing when at
  least half the shared descriptions match.
- A language kept from an earlier model was still sent when YuE2 hid the
  picker. The ASR-style branch now requires `ShowSpeechLanguage`.
- The workflow check would have called gen "no input" with YuE2 loaded; it now
  counts the Style and Lyrics boxes.
- The pseudo-locale was stale (87 of 89 keys). Regenerated: 93 of 93.
- The expander header, "Use as score" and the Style heading are now resource
  strings (`options.fromSpec`, `action.useScore`, `request.style`;
  `request.prompt` is shared with upstream, so it carries translations).
  Errors and status lines stay English, like the rest of the app's.
- The empty-style check ran after the session was built. It now runs first.
  The live run shows the change: the score-only run now pays the 1.6 s session
  build that the refused run used to pay.
- Prompt and lyrics options are found once at load, not on each read, and the
  task setter no longer raises ShowText twice.

Not changed: the review's altitude point, that the real fix is upstream
(`yue2_cli_interface()` should declare what `apply_options` reads), is right.
The spec merge is a stopgap until then, and the description test keeps it to
families whose lists are written by hand.

All checks pass: spec-options (now with spec-derived and no-default cases),
component, save, settings, video, strings, workflow, and `--yue2-check` on
CUDA.
