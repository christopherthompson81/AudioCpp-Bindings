# `transcript.text.delta` — cumulative or incremental (#68)

#68 recorded that `transcript.text.delta` carries a running total for some
families and an increment for others, with nothing in the event saying which.
It proposed two server-side fixes: diff in the server, or add a `"cumulative"`
field so a client can branch.

Neither turned out to be the right fix. This log is how that changed.

## Run 1 — 2026-09-15 — looking for the flag, finding a contract

Question: does the engine already know which semantics a family uses, so the
server could label the event?

`engine::runtime::StreamEvent` (`include/engine/framework/runtime/session.h:205`)
carries `std::optional<Transcript> partial_text` and nothing else about it — no
kind, no flag. So the server cannot label what it is forwarding.

But the CLI answers the question the other way. `app/cli/partial_render.h`:

> Partials are the text newly decoded since the last one, so they concatenate
> into the transcript.

`PartialTextRenderer::render` appends each partial to the terminal. So there is
already exactly one contract — **increments** — and the CLI is written against
it.

Implication: this is not an ambiguity to resolve in the server. It is a family
violating a contract. Which reframes the whole issue.

## Run 2 — 2026-09-15 — who obeys it

Question: is the contract actually honoured, or is parakeet just the one that
got caught?

```
$ grep -rn "partial_text" src/models/*/session.cpp
higgs_audio_stt:80    transcript.text.substr(prefix_size)
vibevoice_asr:155     transcript.text.substr(prefix_size)
voxtral_realtime:504  streaming_text_.substr(streaming_published_bytes_)
nemotron_asr:464      delta
qwen3_asr:623         (delta)
```

Every one emits an increment. Two idioms: a common-prefix diff
(`emit_transcript_delta` in `vibevoice_asr`, duplicated in `higgs_audio_stt`)
and a published-bytes offset (`voxtral_realtime`).

`parakeet_tdt` is not in `src/models/` at all — it is `src/community_models/`,
which is why it did not show up next to its peers. And:

```cpp
// src/community_models/parakeet_tdt/session.cpp:915
auto decoded = merged_decode();
event.partial_text = runtime::Transcript{decoded.text, ""};
```

`merged_decode()` re-renders the whole transcript from every token so far. So
this family alone publishes cumulative text.

**Implication: fix the family, not the server.** That fixes the CLI and every C
ABI consumer too, where a server-side diff would have fixed only the SSE route,
and a `"cumulative"` flag would have fixed nothing — just documented the
breakage.

## Run 3 — 2026-09-15 — confirming the CLI is broken too

If the contract is real, the CLI must already be producing garbage for parakeet,
not just the server. Built `audiocpp_cli` from this tree and ran it, stashing the
fix first:

```
$ audiocpp_cli --task asr --mode streaming --family parakeet_tdt \
    --model .../parakeet-tdt-0.6b-v3-q8_0.gguf --backend cpu \
    --audio assets/resources/sample_16k.wav

partial_text=Some call me nat
partial_text=Some call me nature. Others call me
partial_text=Some call me nature. Others call me Mother Nature. I'
partial_text=Some call me nature. Others call me Mother Nature. I've been here for over four point
partial_text=... (each line the whole transcript again)
```

Confirmed. In interactive mode those append, so the terminal shows
`Some call me natSome call me nature...` — the same corruption #68 described for
an SSE client, from the same cause. The issue found it through the server, but
the server was never the problem.

## Run 4 — 2026-09-15 — the fix, and why a diff rather than a counter

`voxtral_realtime`'s published-bytes offset is the simpler idiom, and it is the
wrong one here. Parakeet re-decodes each window with right context, so a later
decode can **revise** earlier text rather than only extend it — `format_tokens`
re-renders the full token list with a moving `audio_end_frame`. A byte counter
assumes monotonic growth and would silently corrupt the stream the first time a
revision shortened or altered the prefix.

So: common-prefix diff, as `vibevoice_asr` does, plus one thing it does not do —
backing the prefix off to a UTF-8 boundary:

```cpp
while (size > 0 && (static_cast<unsigned char>(rhs[size]) & 0xC0) == 0x80) {
    --size;
}
```

Without it a revision landing mid-character splits a code point across two
deltas, which is invalid UTF-8 in the SSE JSON. Parakeet v3 is multilingual, so
this is reachable rather than theoretical. **`vibevoice_asr` has the same latent
bug**; left alone rather than widening the PR, and noted for a follow-up.

After, same build and same audio:

```
partial_text=Some call me nat
partial_text=ure. Others call me
partial_text= Mother Nature. I'
partial_text=ve been here for over four point
partial_text= five billion years
partial_text=, twenty two thousand five
partial_text= hundred times longer than you.
```

Concatenated, byte for byte equal to the run's own `text_output`.

### Why the server needed no change

Checked rather than assumed, because the fix now emits events with no partial
text at all when a window decodes nothing new:

- `run_transcription_stream` already returns early on
  `!event.partial_text.has_value()`, so no empty delta is written.
- `emit_if_nonempty` (`app/streaming/streaming.cpp:13`) drops an event only when
  *every* field is empty, so a window with word timestamps and no new text still
  delivers them.

## Outcome

- Fixed in `parakeet_tdt` alone; server untouched.
- Fork PR christopherthompson81/audio.cpp#6 for review.
- Follow-up worth filing: `vibevoice_asr`'s `common_prefix_size` has the same
  UTF-8 splitting bug, and `emit_transcript_delta` is duplicated between it and
  `higgs_audio_stt` — a shared helper would stop the next family getting this
  wrong, which is the actual root cause of #68.

## Run 5 — 2026-09-15 — reviewing the fix, and finding it half right

Question: does the UTF-8 handling in Run 4 actually do what its comment claims?

It does not. The backoff guards the point where the diff *diverges* — the start
of a delta — and nothing bounds the end. Traced by hand:

```
emitted_text_ = "abc"
decoded.text  = "abc\xE4"        (a 3-byte character, one byte so far)

common_prefix_size -> 3
rhs[3] = 0xE4, and 0xE4 & 0xC0 == 0xC0, not 0x80
    -> not a continuation byte, so nothing is backed off
emit decoded.text.substr(3) = "\xE4"
```

A bare lead byte goes out as the delta, which is invalid UTF-8 by the time it
reaches the SSE JSON. The comment in Run 4 claimed this case was covered; it
covered the opposite one. The likelier trigger is the end of a decode, not a
revision, because the tokenizer falls back to bytes for text its vocabulary does
not cover — so this was the more reachable of the two and the one that was
missed.

Fixed with `complete_utf8_end`, bounding a delta at the last complete sequence,
and `emitted_text_` now records what actually *went out* rather than the whole
decode, so a held-back character is reconsidered against the window that
completes it instead of being skipped. Nothing is lost if a stream ends with a
character still held: `finalize()` reports the full `text_output` regardless.

Exercised by replaying decode sequences through the two helpers:

```
  ok    ascii growth                       got=Some call me nature. Others
  ok    byte-fallback char assembled       got=ab一
  ok    incomplete tail held back          got=ab
  ok    cyrillic growth                    got=При
  ok    shrink is a no-op                  got=abcdef
  note  revision yields: Some call me natone called me
```

The old code fails `byte-fallback char assembled` by emitting a lone `\xE4`.

### A claim that needed walking back

That `note` line is the second finding. Run 4 justified the diff over a byte
counter by saying a counter "would silently corrupt the stream on a revision"
while the diff "re-sends from the divergence point" — which reads as though the
diff makes revisions safe. It does not. An append-only protocol cannot retract,
so on a real revision the client's transcript is wrong under either scheme; the
diff bounds the damage to the diverged tail rather than repairing it. The PR now
says so with the replay output as evidence, because the original phrasing would
have let a reviewer merge it believing revisions were handled.

Whether parakeet revises at all is still open. `decode_text` is
`tokenizer->decode_ids()` over an append-only token list, which is prefix-stable
for ordinary detokenization; the reachable exception is byte-fallback, where a
completed character can replace bytes already published. Not observed in any run
here — every partial on the English sample was a pure extension.

### The other asymmetry

`partial_text` is now incremental while `word_timestamps` in the same event
stays cumulative. Checked whether that is a defect introduced by this change:
it is not. `word_timestamps` is not a delta field — it is the finalized set so
far, which is why the provisional last word is popped. Before the fix both
fields were cumulative and accidentally consistent; now each matches its own
contract and they differ. Commented in place rather than "fixed", since making
word timestamps incremental would break the one thing that field is for.

No other streaming family emits `word_timestamps` at all, so there was no
convention to check against — the reasoning had to come from the field's own use.

## Outcome (revised)

- Two commits on `fix/transcript-delta-increments`: the fix, and a review fix on
  the fix.
- Fork PR christopherthompson81/audio.cpp#6, body corrected to stop overstating
  what the diff buys.
- Follow-up still worth filing: `vibevoice_asr`'s `common_prefix_size` has
  *neither* UTF-8 guard, and `emit_transcript_delta` is duplicated between it and
  `higgs_audio_stt`. A shared, correct helper is the actual root-cause fix for
  #68 — this PR fixes the one family that was caught.

## Run 6 — 2026-09-15 — widening to the root cause

Question: if parakeet got this wrong, what stopped every other family getting it
wrong too? Answer: nothing. That is the actual defect.

Swept every `partial_text` assignment in the tree rather than the `src/models/`
subset Run 2 looked at — which is how the first sweep missed two families, both
in `community_models/`:

```
$ grep -rn "partial_text = " src/ --include=*.cpp
community_models/sense_asr/session.cpp:762     published-bytes offset
community_models/kroko_asr/session.cpp:906     event.partial_text = result.text_output
community_models/parakeet_tdt/session.cpp      (the Run 4 fix)
models/higgs_audio_stt/session.cpp:71,344      common-prefix diff + chunk deltas
models/vibevoice_asr/session.cpp:146,825,1205  common-prefix diff + chunk deltas
models/voxtral_realtime/session.cpp:505        published-bytes offset
models/qwen3_asr/session.cpp:623               published-bytes offset
models/nemotron_asr/session.cpp:464            decoder-native delta
```

**`kroko_asr` is a second instance of #68's bug**, never reported:

```cpp
const auto decoded = combined_decoded();
const auto result = make_result(decoded, ...);
event.partial_text = result.text_output;   // everything decoded so far
```

Identical shape to parakeet, including the cumulative `word_timestamps`
alongside. It would have survived the Run 4 fix untouched. Found by looking for
the pattern, not by running it — I have no kroko weights.

Three implementations of one idea across seven families: a common-prefix diff
duplicated *verbatim* (helper and caller both) in `higgs_audio_stt` and
`vibevoice_asr`; a published-bytes offset open-coded three times; and two
families doing no diffing at all. None of the five that did diff handled UTF-8.

### The fix

`engine::runtime::PartialTextPublisher` — one implementation, seven callers, and
`streaming_published_bytes_` gone from the tree entirely. Unit tested at
`tests/unittests/test_partial_text.cpp`, which the pre-fix logic fails on the
byte-fallback cases.

### Two mistakes of my own worth keeping

**A substring guard that matched the wrong thing.** The migration script skipped
adding the include when `'partial_text.h' not in s` — which matched
`event.partial_text.has_value()` in `vibevoice_asr`, so that file silently went
without its include. It compiled anyway (transitively included), which is how a
bug like this survives; caught by grepping the result rather than trusting the
script's own report.

**Running the bindings suite from the wrong branch.** First run failed with four
`NOT BOUND` entry points, which looked like the refactor breaking the ABI. It
was `transcript-delta-investigation` checked out — that branch is cut from
`main` and does not carry the `abi-task-vocabulary` bindings. Re-run from the
right branch: green across five families, C and C# agreeing on every value.
Worth recording because the failure named the ABI and pointed at the engine,
while the cause was which branch was checked out.

## Outcome (final)

- Three commits on `fix/transcript-delta-increments`: the family fix, the review
  fix, and the shared publisher.
- Fork PR christopherthompson81/audio.cpp#6, retitled for the wider scope.
- The follow-up this log kept naming — `vibevoice_asr`'s missing UTF-8 guards and
  the duplicated `emit_transcript_delta` — is no longer a follow-up; it is the
  third commit.
- Still open: only `parakeet_tdt` was run end to end, six migrations rest on
  compilation and the helper's tests, and `nemotron_asr` plus the two
  `vibevoice_asr` chunk-append sites take deltas produced upstream and were left
  alone.

## Run 7 — 2026-09-15 — actually running the six migrated families

Six of the seven migrations rested on compilation and reading. Installed each
family's cheapest package and drove it through the CLI on the same clip.

| family | package | size | partials | concat == text_output |
| --- | --- | --- | --- | --- |
| `kroko_asr` | community q8_0 | 168 MB | 11 | after a fix — see below |
| `sense_asr` | SenseVoice-Small q8 | 254 MB | 1 | yes |
| `qwen3_asr` | 0.6B q8_0 | 1.2 GB | 1 | yes |
| `voxtral_realtime` | Mini-4B q4_k | 3.1 GB | 33 | yes |
| `higgs_audio_stt` | v3-STT q8_0 | 3.2 GB | 4 | yes |
| `vibevoice_asr_streaming` | 7B q4_k | 5.9 GB | 5 | unchanged from baseline |

`voxtral_realtime` is the most valuable of these: 33 token-level partials
(`' Some'`, `' call'`, `' me'`, `' nature'`, `'.'`, …), so the publisher is
exercised once per token rather than once per window. `sense_asr` and `qwen3_asr`
are the weakest — the whole clip fits one window, so one partial goes out and the
diff never runs twice.

### kroko_asr's final result depended on the bug

Running it is the only reason this was found. The partials came out correctly as
eleven increments, and `text_output` came out as `" times longer than you"` —
the last increment alone. `finalize()` was:

```cpp
auto event = process_streaming_audio(true);
result.text_output = event.partial_text;
```

It read the whole transcript out of the partial, which worked only because the
partial restated the whole transcript every time. Fixing the partial broke the
final result, and **nothing in the types or the compiler noticed**: both are
`std::optional<Transcript>`, so the code is equally valid before and after.

Confirmed it was my change rather than a pre-existing fault by checking out
`origin/main`'s copy of the two kroko files, rebuilding, and re-running: baseline
emitted eleven copies of a growing transcript *and* a correct `text_output`. So
the baseline run served double duty — it confirmed the diagnosis of kroko as a
second instance of #68, and proved the regression was mine.

`finalize()` now builds its result from `combined_decoded()` through
`make_result()`, the same path the offline route already uses, which also
supplies the speech segments and word timestamps it had been assembling by hand.

Swept for the same shape elsewhere:

```
$ grep -rn "text_output = .*partial_text" src/ --include=*.cpp
src/capi/audiocpp.cpp:226:    result.text_output = std::move(event.partial_text);
```

That one is correct and intended — it presents a stream *event's* partial through
the event's own text accessor, which is what a delta is — and it is documented as
such in the comment above it. No other family reads its final transcript out of a
partial.

### Two process notes

`model_manager_v2.py` buffers its output, so a download in progress looks
identical to one that never started. I read an empty log as a failed start and
launched a second copy of the 5.9 GB vibevoice download; both ran, each into its
own staging directory. Killed the newer, removed its orphan, kept the older.
Check `ps` and the staging directory's size, not the log, to tell a slow download
from a dead one.

Also lost a download to a backgrounded `( ... ) & ( ... ) & wait` where only the
first subshell inherited the `cd` — the second failed with
`can't open file '.../AudioCpp-Bindings/tools/model_manager_v2.py'`. Absolute
paths in backgrounded subshells.

## Run 8 — 2026-09-15 — vibevoice_asr_streaming, and two bugs that are not mine

`vibevoice_asr_streaming` (served by the same `src/models/vibevoice_asr/session.cpp`
this PR migrated) produced output that looked broken:

```
partial_text= 
partial_text=others call me mother nature. 
partial_text=I've been here for over four point five 
partial_text=billion years. Twenty two thousand 
partial_text=five hundred times longer than you.
text_output= 
```

Two things wrong: the opening "Some call me nature." appears in no partial, and
`text_output` is a lone space. After kroko, the obvious reading was that I had
broken a second family the same way.

Checked instead of assuming: rebuilt with `origin/main`'s copy of
`session.cpp` and re-ran. The two outputs are **byte-identical** —

```
$ diff <(grep -E "^partial_text=|^text_output=" vibe-before.log) \
       <(grep -E "^partial_text=|^text_output=" vibe.log)
$ echo $?
0
```

So the migration is behaviour-preserving here, which is expected: this family
already emitted increments through `emit_transcript_delta`, and swapping that for
the shared publisher changes nothing on Latin text with no revisions. Both
defects are pre-existing and separate from #68. Not investigated, not fixed, and
called out in the PR rather than folded into it.

The general lesson, twice in two runs: **when a family looks broken after a
change, rebuild its file from upstream before concluding anything.** It cost one
rebuild each and settled both cases definitively — kroko was mine, vibevoice was
not.

## Outcome (tested)

Four commits on `fix/transcript-delta-increments`, all seven migrated families
run against real weights rather than read:

| family | partials | result |
| --- | --- | --- |
| `parakeet_tdt` | 7 | concatenate to `text_output` |
| `kroko_asr` | 11 | concatenate, after fixing its `finalize()` |
| `voxtral_realtime` | 33 | concatenate |
| `higgs_audio_stt` | 4 | concatenate |
| `sense_asr` | 1 | concatenate |
| `qwen3_asr` | 1 | concatenate |
| `vibevoice_asr_streaming` | 5 | identical to baseline; two pre-existing bugs |

Still uncovered: the byte-fallback path has no live-model test, only the unit
test, because nothing to hand drives a tokenizer into fallback. And
`nemotron_asr` plus the two `vibevoice_asr` chunk-append sites take deltas
produced upstream, so the publisher does not apply to them.

## Run 9 — 2026-09-15 — the vibevoice "bugs" were a measurement error

Opened a branch to fix the two defects Run 8 recorded in
`vibevoice_asr_streaming`: the first window's text never reaching a partial, and
`text_output` coming back as a lone space. Neither exists.

Reading the code first turned up nothing: `process_streaming_model_normalized_chunk`
accumulates through `append_streaming_transcript`, `finalize()` returns
`streaming_result_`, and `append_chunk_speech_metadata` — the other function
handed the accumulator — touches only spans, never `text_output`. Nothing resets
the result mid-stream.

So I instrumented it instead of reading further:

```
[DBG] chunk text=34 delta=34 total=34
[DBG] chunk text=30 delta=30 total=64
[DBG] chunk text=40 delta=40 total=104
[DBG] chunk text=35 delta=35 total=139
[DBG] chunk text=35 delta=35 total=174
[DBG] finalize returning total=174
text_output= 
```

`finalize()` returns all 174 bytes, and the CLI prints one space. The engine was
never wrong; the *reading* was. Printing the delta contents rather than their
lengths showed why — the line came out as `chunk text=[` followed by a line
break:

**This family's transcript begins `"\n Speaker 0:"`.** The CLI's non-interactive
format is `partial_text=<text>\n`, so a transcript containing a newline continues
onto the next line, and the `grep '^partial_text='` and
`re.findall(r'^partial_text=(.*)$', ...)` I had been extracting with both stop at
that newline. Everything past it was invisible to the check. Hence an apparently
empty first partial, an apparently one-space `text_output`, and a concatenation
that did not match.

Parsing on the `partial_text=` markers instead of per line:

```
partials: 5
    ' \n Speaker 0:Some call me nature, '
    'others call me mother nature. '
    "I've been here for over four point five "
    'billion years. Twenty two thousand '
    'five hundred times longer than you.'
concat == text_output: True
```

Correct, and correct before the migration too. Branch deleted, PR description
corrected, nothing filed.

### What this says about Run 7's table

Every family in Run 7 was checked with the same per-line regex. The six that
reported `concat == text_output: True` are still trustworthy — a false *pass* is
not a failure mode of this bug, since a truncated capture makes the
concatenation shorter and the comparison fail. It produces false **alarms**, not
false assurances, and vibevoice was the only family whose transcript carries a
newline. But the harness was wrong and happened not to matter, which is worth
less than a harness that is right.

### The lesson worth keeping

Run 8 concluded "pre-existing, not mine" from a baseline diff, and that part was
sound — the outputs were byte-identical. The error was in the step before it:
treating output my own extraction had mangled as evidence of what the program
produced. A byte-identical diff of two wrong readings is still two wrong
readings.

When a field looks empty, dump the raw bytes (`cat -A`, `xxd`) before concluding
anything about the code that produced it. One `cat -A` at the start of Run 8
would have shown the `\n Speaker 0:` continuation line immediately and saved
filing two phantom defects.

## Run 10 — 2026-09-15 — adopting the fix downstream, and what it uncovered

Upstream merged #552 as `3b90d6e`, tagged `v0.8.0` (`4af1432`). `include/audiocpp.h`
is byte-identical between the old pin `5db449e` and `4af1432`, so re-pinning was
an engine rebuild and no binding change: coverage stays `declared: 73 bound: 73`.

Question: with every streaming ASR family now publishing increments, can the
server test stop accepting either shape?

`tests/AudioCpp.ServerTest/Streamed.cs` had deliberately asserted only that the
stream *agreed with its own ending* — either each delta is the running total, or
they concatenate. Narrowed to the one assertion the engine now guarantees:

```
the deltas concatenate to the transcript the stream ends on
```

### The narrowed assertion failed, and it was ours

```
FAIL  the deltas concatenate to the transcript the stream ends on
      Some call me nature. Others call meMother Nature. … twenty two thousand five
```

Two separate defects in one line.

**1. The tail of the transcript never reached the client as a delta.** 6 deltas
arrived where the CLI reports 7 partials for the same clip. The last window a
streaming ASR decodes is decoded inside `finalize()`, and this ABI returns that
as a *result*: `audiocpp_stream_finish()` hands back a `TaskResult` and leaves
no event to poll. So the closing text was only ever in `transcript.text.done`,
and a client doing what the OpenAI shape tells it to — append each delta —
rendered a transcript missing its last window for the whole of that window.

Invisible until now. While partials restated the running total, the last one
before finalize already contained everything that mattered, so the gap was
covered by the very bug #68 reported.

Fixed in both routes with `ClosingDelta(sent, final)`: the increment the
finalized transcript adds to what was sent, or empty when `final` does not
extend it. Empty rather than a guess, because a family that *revised* earlier
text cannot be reconciled by appending, and synthesising a delta there would
make the stream disagree with itself rather than merely end early.

**2. The test's own normalization welded words together.** `Normalize` is
whitespace-insensitive, and the first version normalized each delta *before*
concatenating. The space between two words lives inside whichever delta carries
it — upstream's own example shows `partial_text= Mother Nature. I'` with a
leading space — so per-delta trimming produced `call meMother` and reported a
failure the stream did not have. Concatenate raw, normalize once.

The second defect was masking nothing, but it made the first one's evidence
unreadable: the same line showed both a missing tail and a bogus join.

### After both fixes

```
ok  text arrives as deltas  7 deltas
ok  the deltas concatenate to the transcript the stream ends on  …times longer than you.
ok  live audio produces deltas  5
ok  live deltas concatenate to the live transcript  …times longer than you.
```

7 deltas, matching the CLI's partial count for the same clip exactly. Full suite
against the `v0.8.0` pin with Parakeet TDT + Qwen3 forced aligner: 131
assertions, 0 failures, exit 0.

### Outcome

- #68 closed: fixed upstream in #552, adopted at the `v0.8.0` pin.
- The route gap it exposed is fixed here, and is a defect of ours rather than
  the engine's — worth recording separately, because nothing about #68's report
  predicted it.
