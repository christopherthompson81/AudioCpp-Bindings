# Pin bump to 487800f5, and the web UI changes since #80

Engine pin moved 2792c657 → 487800f5 (29 upstream commits). The last GUI sync
was #80, at engine 5db449ef, so the web UI diff is taken from there.

## Run 1 — 2026-09-23 ~08:40

Question: does the move need binding changes?

```
git diff --stat 2792c657..487800f5 -- include/audiocpp.h src/capi
```

Nothing. `audiocpp.h` and the C ABI are untouched. The rest is model work
(vietneu_tts renamed vieneu_v3_turbo, audio8_tts Falcon-H1, YuE2, Moonshine
chunking, CUDA kernels) and web UI. Nothing in this repo names vietneu.
Spec task names include `edit` and `sfx`, but the engine's
`audiocpp_task_from_spec_name` maps those, so no table here needs them.

Implication: build and run the suite. No binding changes are expected.

## Run 2 — 2026-09-23 ~09:05

```
./scripts/build-engine.sh --ccache --cuda-arch native -DENGINE_ENABLE_CUDA=ON
./scripts/run-tests.sh /mnt/data/models/audiocpp --backend cuda
```

The build is clean: 917 steps, only `compute_86,code=sm_86` generated, ccache
set for C/CXX/CUDA, 41% hits (the misses are upstream's changed files).

```
ran=7 skipped=0 failures=0
C and C# agree on 11 reported values
```

This is the same count as the previous pin. The package catalog check passes
with the renamed spec.

## Run 3 — 2026-09-23 ~08:50 (reading the web UI diff)

`git diff 5db449ef..487800f5 -- webui`: 21 files. Most of it is curation that
this app gets from the ABI and does not copy: new catalog rows (families
arrive through model_specs), model_params.json control lists, and per-family
branches in `+page.svelte`. What does translate:

| upstream | here |
| --- | --- |
| `workflow.music` → "Music / video generation" | resx English matched, locales re-imported |
| gen takes an optional source clip (AuK edit, LiveAvatar driving audio) | `ShowOptionalAudioInput`; never demanded |
| AuK/YuE2/LiveAvatar component selectors (hand-listed choices) | `*_gguf` string options list the model dir's GGUFs |
| LiveAvatar reference-image / YuE2 LoRA upload | Browse button on path options |
| LiveAvatar rgb24 frames → MP4 in browser (mediabunny) | ffmpeg on save, muxed with the source clip |
| confucius4_r2t2 added to live ASR list | nothing to do: live is attempted for any ASR model and the engine refuses if it cannot |
| `arena: false` on AuK | nothing to do: our Arena is path-based, not catalog-filtered |

## Run 4 — 2026-09-23 09:20

```
dotnet run --project src/AudioCpp.Bindings.Gui -- --live-check --shot \
  model=/mnt/data/models/audiocpp/Yue2-3B-GGUF backend=cuda task=music
```

Question: do the component and path editors attach to the right options?

Two things the printed option list showed that I had not expected:

- The ABI names options with the family prefix (`yue2.model_gguf`), so the
  name tests have to be suffix tests. They were.
- The ABI reports LoRA and file options as type `path`, although the spec
  JSON says `string`. `IsPath` now trusts `Type == "path"` first and keeps the
  name suffix only for specs that still say `string` (LiveAvatar's
  `reference_image_path`).

## Run 5 — 2026-09-23 09:24 (the window, driven with xdotool)

Opened the session options and then the `yue2.model_gguf` drop-down.

**It offered `yue2-vae-f16.gguf` as the main weights.** Every component
installs into one directory, so "all GGUFs in the directory" is wrong: picking
the VAE there is a load that fails, or one that runs. Upstream avoids this by
hand-listing each option's files.

Fix: strip precision suffixes to get a stem (`yue2-3b-q8_0` → `yue2-3b`), and
offer the files that share the declared default's stem. An option with no
default (AuK's `model_gguf`) gets the files no sibling default claims. Checked
against the full file sets the four component families' specs declare:

```
dotnet run --project src/AudioCpp.Bindings.Gui -- --component-check
```

yue2, auk, minimax_music3 and liveavatar each get exactly their own files, and
AuK's generator gets Base and Flash at every precision, not Qwen or VAE.
Re-opened in the window: `model_gguf` now lists only `yue2-3b-q8_0.gguf`.

## Run 6 — 2026-09-23 09:32

Set a style in the window and clicked Run. Yue2 on CUDA: session 1165 ms,
run 21455 ms, 79.92 s of audio, plus a `score` artifact (ABC,
`extension=abc`) that shows inline and saves.

The first attempt at this was lost: the harness's 900 s timeout killed the
window before the result was read. The rerun used a 3600 s timeout.

## Run 7 — 2026-09-23 09:35

```
--video-check  --save-check  --workflow-check  --strings-check  --settings-check
```

All OK. The video check writes odd-sized synthetic rgb24 frames (the engine
does not promise even sizes; yuv420p needs them), and ffprobe confirms 24 padded
frames at 66x50, with an audio track only when a source clip was given. A
truncated payload is refused rather than encoded.

Not verified: a real LiveAvatar or AuK run. Neither model is installed here,
and LiveAvatar's 14B denoiser is its own download.
