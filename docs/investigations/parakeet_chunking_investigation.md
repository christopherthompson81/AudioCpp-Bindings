# Parakeet long-clip chunking

Why a long recording fails on the shipped Parakeet GGUF, and what the demo
should do about it. All runs against a real long-form conversational
recording (~10 min, 16 kHz mono) and a 90s excerpt of the same, decoded on
an RTX 3090 unless noted.

## Run 1 — 2026-09-12 — does audio_chunk_mode=vad do anything at all?

Question: an earlier read of the code suggested the option was erased before
use, so the assumption going in was that it did nothing.

`normalize_request_options` erases unknown keys from `validation_options`,
not from `options`, and the session reads `options`. So the erase only
filters *validation*. Confirmed empirically: identical rebuild counts and
word counts running the stock GGUF against one patched to declare the
options. The stale embedded spec costs discoverability, not function.

Implication: the option works. The failure is elsewhere.

## Run 2 — 2026-09-12 — where the long clip actually dies

    --smoke <gguf> <10min.wav> asr parakeet_tdt "" backend=cuda chunking=false
    -> failed: Parakeet TDT encoder relative position frames exceed maximum

`encoder.cpp:87` builds a relative positional encoding and throws when
`frames > max_position_embeddings` (5000 in the shipped config). One encoder
frame is 80ms after the FastConformer's 8x subsampling at a 10ms hop, so the
wall is 400s.

The underlying defect is in `session.cpp`: `prepare()` sizes the encoder
graph from `offline_mode` without consulting `chunk_mode`, so requesting vad
chunking alone still sizes the graph for the whole recording. Setting
`parakeet_tdt.offline_mode=long_form` makes `prepare()` agree with the path
`run()` takes. That is a workaround for a real upstream bug, not a
configuration nicety.

## Run 3 — 2026-09-12 — vad vs fixed vs in-app segmentation

10-minute clip, CUDA:

| path | wall | words |
|---|---|---|
| engine defaults | fails | — |
| long_form + chunk_mode=vad, 10s | 5.7s | 1483 |
| long_form + chunk_mode=fixed, 10s | 7.7s | 1372 |
| separate VAD model, segmented in-app | 35s | — |

`fixed` cuts on a timer and visibly loses clauses at every boundary
("sharpens your reflexes" -> "sharpenses"; whole sentences absent). `vad`
cuts between utterances and is also the faster of the two.

Note the engine's own `audio_chunk_duration_sec` default is 2s, which slices
mid-utterance badly enough to be worth overriding to 10s.

## Run 4 — 2026-09-12 — the preset is a pessimisation on short clips

90s clip, CUDA:

    preset on  -> 226 words, 1.9s
    preset off -> 236 words, 1.4s

Negative result, and the reason the preset is gated on duration rather than
applied always. Under the 400s ceiling the engine already switches to
bounded windows past `audio_chunk_threshold_sec` (30s default), and its own
choice is better than ours. Gate set at 300s, leaving margin for a
checkpoint configured with a different `max_position_embeddings`.

## Run 5 — 2026-09-12 — vad chunking has a hidden file dependency

    -> failed: Silero VAD model path does not exist:
       assets/framework/models/silero_vad

`default_vad_model_path()` returns that *relative* path, so it resolves only
when the process happens to run from an audio.cpp checkout. Earlier fast
numbers were measured from that directory, which hid the problem.

`parakeet_tdt.vad_model_path` overrides it and survives the validation erase
(Run 1), so the demo searches for the checkpoint — explicit setting first,
then the working directory, then walking up from `AUDIOCPP_NATIVE_DIR`,
since a build tree sits beside the assets directory it was built from — and
falls back to `fixed` when there is none. Verified by running from a neutral
working directory: 5.5s, 1483 words, matching the explicit-path run.

## For upstream

1. `prepare()` should consult `chunk_mode`, not only `offline_mode`
   (~5 lines, tested). Without it `audio_chunk_mode=vad` is unusable on
   exactly the clips it exists for.
2. Regenerate the shipped GGUF so the chunking options and `offline_mode`
   appear in the embedded spec. They work today but cannot be discovered.
3. `normalize_request_options` silently drops unknown keys from validation.
   It should warn; the silence is what made Run 1's wrong assumption
   plausible for as long as it was.
