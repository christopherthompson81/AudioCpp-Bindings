# Reference transcripts

**Not tracked.** Reference transcripts are verbatim renderings of whatever audio they
were produced from, so for a licensed corpus they are derivative of it and cannot live
here. Generate your own locally; this directory is where the harness docs assume they
sit.

What a reference is for: comparing batched runs only against each other shows
self-consistency, which cannot separate "every batch size is equally good" from "every
batch size drifted together". A transcript from an independent implementation at higher
precision is an independent opinion — not ground truth, but enough to measure error
rather than agreement.

## Generating one

Any second implementation works. An F32 ONNX Parakeet pipeline makes a good contrast
with a q8_0 GGUF one, since it differs in both precision and runtime:

```bash
# whatever your reference stack is, transcribing the same clip
<reference-tool> --audio my_clip.wav --export-format txt \
    --output tools/perf/reference/my_clip.reference.txt
```

`compare.py` strips `[speaker_1] 00:00:00 - 00:00:08` style headers, so a
speaker-segmented export can be compared without preprocessing.

A same-engine CPU batch-1 transcript is also worth keeping as a cross-backend baseline
— it measures how far a GPU run drifts from CPU on identical code at identical batch
size, with batching out of the picture.

## Using one

```bash
python3 tools/perf/compare.py \
    tools/perf/reference/my_clip.reference.txt /tmp/out_8.txt reference batch8
```

Flat agreement across batch sizes means the error is consistent, which at q8_0 is the
question that matters — bit-identical output is not expected when reduction order and
tile shape vary with M.

## Audio

Bring your own, 16 kHz mono WAV (`Wav.cs` is a minimal reader for the test fixtures,
not a general decoder — it will not resample). Nothing in this repo ships audio: the
recordings these harnesses were developed against come from a licensed corpus and are
not redistributable.
