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
