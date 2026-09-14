# Encoder batching measurement harnesses

Throwaway rigs used to measure Parakeet encoder batching against
`christopherthompson81/audio.cpp` branch `perf/asr-batching`. They drive the C ABI
directly rather than going through the demo, because the question is about the
engine, not the UI.

| | |
|---|---|
| `VadPipeline` | Silero VAD -> group -> Parakeet per group, one session reused. Mirrors Vernacula's VAD+Parakeet pipeline through the ABI. |
| `LongForm` | Single whole-file request. Used to reach `run_long_form` and `run_vad_chunks` in-engine. |
| `rusage.py` | Wraps a command and reports wall/user/sys, parallelism, page faults, context switches. |
| `compare.py` | Word-level diff of two transcripts, with agreement and WER-style divergence. Strips Vernacula's `[speaker_1] 00:00:00 - 00:00:08` headers. |

## Running

```bash
dotnet build tools/perf/VadPipeline

# VAD + Parakeet, per-segment, 8 threads
dotnet run --project tools/perf/VadPipeline -- \
    external/audio.cpp/assets/framework/models/silero_vad \
    /path/to/models/Parakeet-TDT-0.6B-v3-GGUF/parakeet-tdt-0.6b-v3-q8_0.gguf \
    en-US/en-US_sample_01_90s.wav 0 28 8
```

`VadPipeline` args: `<vad-model-dir> <gguf> <wav> [min-span-s] [max-span-s] [threads] [k=v ...]`.
Trailing `k=v` pairs become `parakeet_tdt.*` session options. `ASR_FAMILY` swaps the
family (e.g. `citrinet_asr`). `ASR_BACKEND` selects the backend for the ASR session
(default `cpu`; set `cuda`/`vulkan` to measure a GPU), and `VAD_BACKEND` the VAD one
independently — silero is tiny enough that a GPU round-trip can cost more than it saves. `SORT_BY_LENGTH=1` decodes shortest-first.

`LongForm` args: `<gguf> <wav> <opts> [threads]`, where `opts` is comma-separated:
a bare value means `offline_mode`, `k=v` is a `parakeet_tdt.*` session option, and
`req:k=v` is a request option. `LONGFORM_OUT` writes the transcript plus a
`.words` file of `start<TAB>end<TAB>word`. `ASR_BACKEND` applies here too.

## Engine env flags (branch `perf/asr-batching`, all off by default)

| flag | effect |
|---|---|
| `AUDIOCPP_PARAKEET_ENCODER_BATCH` | max clips per encoder batch |
| `AUDIOCPP_PARAKEET_BATCH_AREA_FRAMES` | padded-area budget, `(count+1)*longest <= N` |
| `AUDIOCPP_PARAKEET_FORCE_VAD_CHUNKS` | reach `run_vad_chunks` (the shipped GGUF contract omits `audio_chunk_mode`, so the request option is silently dropped) |
| `AUDIOCPP_PARAKEET_FRAME_BUCKETS` | static-shape buckets, comma-separated frame counts |
| `AUDIOCPP_PARAKEET_LOG_REBUILDS` | log every encoder graph rebuild to stderr |
| `GGML_COLLAPSE_BATCH_SGEMM` | `1` collapses broadcast-weight plane loops into one sgemm; `2` also logs hits and candidates |

## Gotchas

`offline_mode` defaults to `auto`, and `auto` picks full_context for a long clip —
which then tries to allocate a graph for the whole thing and fails with
"Parakeet TDT encoder relative position frames exceed maximum". That message means
"you left it on auto", not "you hit a ceiling". Pass `long_form` explicitly.

Batched output is not yet invariant, and the two paths fail differently:

| path | CPU | CUDA |
|---|---|---|
| `run_long_form`, 90s | identical N=1..32 | identical N=1..32 |
| `run_long_form`, 600s | identical N=1/4/8 | **5 transcripts, 32-word span** |
| `run_vad_chunks`, 600s | 1222 vs 1220 | **1214-1228, 14-word span** |

So `run_vad_chunks` drift is backend-independent (the padded-frame/conv-boundary
problem), while `run_long_form` drift appears only on CUDA at length. Different
causes. Both are deterministic at fixed batch size, so they are batch-dependent,
not nondeterministic.

## Measuring

Wall time on this hardware drifted ~50% across a session (thermal). Interleave the
cells and take medians rather than comparing runs made minutes apart:

```bash
for rep in 1 2 3; do for flag in 0 1; do ... ; done; done
```

CPU-seconds via `rusage.py` is the more stable signal than wall time, and is what
batching actually targets.

Short clips mislead about batch size, and so does a bad chunk duration. Measure on a
600s clip with `audio_chunk_duration_sec=10`, not on 90s and not at the 2s default.

## Path accuracy — read this before choosing a chunking path

Agreement against an independent F32 reference (see `reference/README.md`), 600s
conversational clip, q8_0, CPU:

| path | words | agreement |
|---|---|---|
| external VAD + per-segment ASR (`VadPipeline`) | 1536 | **90.9%** |
| `run_vad_chunks`, `audio_chunk_duration_sec=10` | 1482 | **89.7%** |
| `run_long_form` | 1357 | 81.3% |
| `run_vad_chunks`, default (2s) | 1235 | **66.3%** |

Two separate problems, neither of them about batching:

**1. `audio_chunk_duration_sec` defaults to 2 seconds**, and `run_vad_chunks` uses it as
the chunk cap — so it slices mid-utterance. Raising it to 10s moves agreement from
66.3% to 89.7%, level with an external pipeline. 10/20/30s are all equivalent
(89.7/89.5/89.2), so the default is simply too low rather than needing tuning.

**2. `long_form` loses whole utterances.** On this clip it drops three passages the F32
reference contains, and the same model on the same CPU via an external VAD pipeline
transcribes all three:

On a 600s conversational clip it dropped three passages (16, 7 and 2 words) that the
reference contains and that an external VAD pipeline — same model, same CPU, same
weights — transcribes in full. So the path loses them, not the backend.

Earlier evidence of the same thing: 36 tokens absorb 151s of a 600s clip, one spanning
18.0s against a 0.24s median word duration. The speech in those spans is never emitted.

CUDA's `long_form` drops fewer than CPU's (1372 vs 1347 words, with the CPU transcript a
strict subsequence of the CUDA one — zero substitutions), so the rate is
backend-sensitive, but the loss is not a backend bug: the same backend gets the words
via a different path.

**Recommendation**: VAD-segment, with chunks of 10s or more. Avoid `long_form` where
recall matters.

## Encoder batching — what it is worth

> **PROVISIONAL — every CUDA timing below was measured against a contended GPU.**
> A game held ~2.9GB and 33% utilisation throughout, with the SM clock at 1590 of
> 2115 MHz (sharing, not throttling). What that does and does not invalidate:
>
> - **Absolute times are upper bounds.** Do not quote them.
> - **The 1.96x chunk-fix ratio is the weakest number here** and must be redone. The
>   2s and 10s sweeps were two separate commands twelve minutes apart, so that ratio
>   compares across sittings with differing load — and the load demonstrably differed
>   (relative stdev was 6-16% in the earlier sweep, 3-8% in the later one). Each sweep
>   was internally interleaved across N; they were not interleaved with each other.
> - **Within-sweep shapes survive**, since alternating cells share a bursty load
>   roughly equally. The monotonic-to-32 and plateau-and-turn results are the part to
>   keep.
> - **Every accuracy result in this file is unaffected**, and not by luck: transcripts
>   were deterministic at fixed N across 3-5 reps, and contention changes *when*
>   kernels run, not *what* they compute. The kernel-routing findings came from a
>   dispatch trace rather than a timer, so they stand too.
>
> Redo list for a quiet machine: the two 600s sweeps, interleaved with each other, plus
> a same-basis Vernacula comparison. Vernacula's CLI `--benchmark` separates
> Diarization/VAD, ASR and Total with RTF, so load can be excluded from both sides.

600s clip, CUDA (RTX 3090), `run_vad_chunks`, 5 interleaved reps, medians:

| N | 2s chunks (the broken default) | 10s chunks |
|---|---|---|
| 1 | 15.30s | 7.80s |
| 4 | 12.30s (1.24x) | 7.10s (1.10x) |
| 8 | 11.10s (1.38x) | 6.90s (1.13x) |
| 16 | 11.00s (1.39x) | 6.60s (1.18x) |
| 32 | 11.40s (1.34x) | **6.30s (1.24x)** |

**The optimum moves with chunk size, and an earlier version of this file had it
backwards.** At the 2s default the curve plateaus at N=8-16 and N=32 is worse; at 10s
it is monotonic and N=32 is best. "Useful range 8-16" was an artefact of measuring on
the broken configuration. Untested beyond 32.

Proportions, which are the ship decision:

| change | effect |
|---|---|
| chunk default 2s -> 10s, no batching | 15.30s -> 7.80s (**1.96x**) |
| batching on top, N=32 | 7.80s -> 6.30s (1.24x) |
| both | 15.30s -> 6.30s (2.43x) |

The one-line default change is worth twice what the entire batching implementation is.
They compose, but they are not close in value.

CPU shows no batching gain at any N, chunk size or policy. Fixing the chunk duration
costs nothing there either (34.7s at 10s vs 33.6s at 2s, batch 1), so the accuracy fix
is free on both backends.
