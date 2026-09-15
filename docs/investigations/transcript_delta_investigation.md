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
