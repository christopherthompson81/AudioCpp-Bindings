# Package download blocks and under-declared `files` lists — re-examining #48

Issue #48 has two halves. The headline half (packages with no `download` block,
for files already hosted in `audio.cpp-gguf`) turns out to be fixed upstream.
The second half (safetensors packages whose `files` list is shorter than what
their own source requires, so they install cleanly and then fail to load) is
not, and is larger than the issue records.

## Run 1 — 2026-09-15 — a bad measurement of my own, corrected

I had commented on #48 that the gap had *grown*: "packages: 237, without
download block: 186", against 137 of 235 when filed. Both halves of that
comparison were wrong.

**Wrong measurement.** The count walked each package's own `download` key. But a
package with no `download` key inherits `package_defaults.download` from the
family — `src/AudioCpp.Packages/Catalog.cs:117` does exactly this:

```csharp
var download = entry.TryGetProperty("download", out var od)
    ? ParseDownload(od)
    : defaults;
```

So "no `download` key on the package" does not mean "not downloadable"; for most
packages it means the opposite, since the family default is the one block that
covers all of its variants.

**Wrong comparison.** Even measured correctly, "no download block" is not the
quantity #48 reported. The 137 was specifically *packages whose declared files
are hosted in `audio-cpp/audio.cpp-gguf` but which carry no block* — a subset.

Redone, replicating the inheritance and the `Supported` rule
(`kind == "huggingface_snapshot" && repo != ""`):

```
total packages: 237
supported (downloadable): 229
NOT supported: 8
```

which is where the package test's `downloadable: 229 of 237` comes from. That
figure was right and my reading of it was wrong.

**Implication:** #48's headline is fixed. 152 packages now point at
`audio-cpp/audio.cpp-gguf`, where the issue counted 11 at filing.

## Run 2 — 2026-09-15 — the 8 that remain are deliberate

Question: are the remaining 8 oversights, or the "deliberate omissions" #48
wondered about?

Every one carries an explicit unsupported block with a written reason:

```
sortformer_diar_v2  (3)  "NVIDIA Open Model License checkpoint: convert or stage
                          locally until redistribution approval is complete."
audio8_asr          (2)  "CC-BY-NC-4.0 checkpoint: convert locally with
                          audiocpp_gguf; no public audio.cpp GGUF distribution
                          is approved."
mms_forced_aligner  (2)  same CC-BY-NC-4.0 reason
sopro_tts           (1)  "No audio.cpp GGUF build of sopro-v2-turbo is published
                          yet: install the sopro_v2_turbo_safetensors package …"
```

`"kind": "unsupported"` with a `reason` string is new since #48 was filed, and
it answers the question the issue raised — MMS is the CC-BY-NC-4.0 case #48
itself guessed at. Three of the four families still offer a downloadable
safetensors package alongside; `sortformer_diar_v2` is local-only by licence.

Nothing to file here. This half is closed.

## Run 3 — 2026-09-15 — the other half is unfixed, and it is 7 packages

Question: are `chatterbox_safetensors` and `seed_vc_mlx_safetensors` still
under-declared, and is the class wider than those two?

Both are unchanged — `chatterbox_safetensors` still declares 4 files,
`seed_vc_mlx_safetensors` still 3.

So I built the consistency check #48 itself proposed ("every `model:`-rooted
file a source requires appears in the package's `files`") and ran it over all
237 packages, matching each package's `format` to the source of the same format
and collecting every `model:`-rooted path in that source's `files` and `tensors`.

Deliberately **not** `optional_files` / `optional_tensors`: those are separate
keys, and `src/framework/model_spec/package.cpp:378,390` loads them through
`add_optional_resource_map`, while a missing entry from `files` throws at
`package.cpp:283`:

```cpp
throw std::runtime_error("missing model package file '" + id + "': " + path.string());
```

That is the exact error #48 observed, so the check is aimed at the right list.

### `format=gguf` is all false positives — 173 of them

A GGUF source declares the same `model:`-rooted config files, but the GGUF
embeds them, so the package correctly ships one `.gguf` and nothing else. The
check is only meaningful for non-GGUF formats. Worth recording because it is the
trap anyone implementing this check upstream will hit first: run it
format-blind and it reports three quarters of the catalogue.

### `format=safetensors` — 7 real findings

```
chatterbox/chatterbox_safetensors      requires  9, declares 4, missing  5
index_tts2/index_tts2_safetensors      requires 20, declares 3, missing 18
omnivoice/omnivoice_safetensors        requires  7, declares 3, missing  4
qwen3_tts/qwen3_tts_1_7b_base_safetensors requires 8, declares 6, missing 2
seed_vc/seed_vc_mlx_safetensors        requires 28, declares 3, missing 25
supertonic/supertonic_3_safetensors    requires 13, declares 3, missing 10
voxcpm2/voxcpm2_safetensors            requires  6, declares 4, missing  2
```

#48 knew about the first and the fifth. The other five are new.

## Run 4 — 2026-09-15 — is it under-declaration or missing hosting?

Question: the fix is a one-line `files` edit only if the files are already
published where the package points. Checked against the HuggingFace API for the
five new findings, comparing each missing path against the repo the package
already downloads from:

```
supertonic/supertonic_3_safetensors: missing 10 | hosted 10 | NOT in repo 0
omnivoice/omnivoice_safetensors:     missing  4 | hosted  4 | NOT in repo 0
qwen3_tts/…_base_safetensors:        missing  2 | hosted  2 | NOT in repo 0
index_tts2/index_tts2_safetensors:   missing 18 | hosted 18 | NOT in repo 0
voxcpm2/voxcpm2_safetensors:         missing  2 | hosted  1 | NOT in repo 1
                                                   absent: audiovae.safetensors
```

**36 of 37 missing files are already hosted at exactly the paths the spec
declares**, in the repo the package already points at. So for six of the seven
the fix is editing the `files` list — no new hosting, no new download block.

`voxcpm2`'s `audiovae.safetensors` is the one exception and a different problem:
the spec requires a file `OpenBMB/VoxCPM2` does not publish, so that package
cannot be completed by declaration alone.

## Where this leaves #48

| half of #48 | state |
| --- | --- |
| 137/235 packages with no download block | **fixed** — 229 of 237 downloadable |
| the 8 that remain | **deliberate**, each with a written `reason` |
| under-declared safetensors `files` | **open**, and 7 packages rather than 2 |
| a spec-vs-package consistency check | **still absent** upstream |

My correction comment on #48 needs correcting in turn — it claimed the gap had
grown when it had closed.

## Run 5 — 2026-09-15 — the proposed fix, staged in the fork

Question: does adding the missing entries actually clear the check, and does it
stay a minimal diff?

Applied to `~/Programming/audio.cpp` (the fork) on branch
`specs/safetensors-files-lists`, based on `b46fe6b`. Existing entries keep their
order; missing ones are appended in the order the source lists them, and only
the `files` arrays are touched, so the diff is 70 insertions across six files
with no reformatting:

```
 model_specs/chatterbox.json |  7 ++++++-
 model_specs/index_tts2.json | 20 +++++++++++++++++++-
 model_specs/omnivoice.json  |  6 +++++-
 model_specs/qwen3_tts.json  |  4 +++-
 model_specs/seed_vc.json    | 27 ++++++++++++++++++++++++++-
 model_specs/supertonic.json | 12 +++++++++++-
```

Re-running the check against the patched specs:

```
STILL UNDER-DECLARED: voxcpm2/voxcpm2_safetensors missing 2: ['audiovae.safetensors', 'special_tokens_map.json']
remaining non-gguf under-declared packages: 1
```

`voxcpm2` is deliberately untouched. Completing its list would not make it
loadable — `audiovae.safetensors` is not published by `OpenBMB/VoxCPM2` — so
fixing only the half that is hosted would turn a clear "missing file" failure
into a package that still fails, with one fewer clue as to why.

`index_tts2` goes 3 → 21 rather than → 20: it already declared `bpe.model`,
which the safetensors source does not name in `files` or `tensors`. Kept rather
than pruned — it ships today, and removing a file the package already fetches is
a different decision from adding ones it needs.

Our own catalogue still parses the patched specs (`package catalog OK`,
`downloadable: 229 of 237`).

**Not verified end to end.** Nothing here has been installed and loaded against
the patched lists. `supertonic` is the cheapest candidate at ~414 MB, and the
evidence so far is static: the spec says the file is required, and the repo the
package points at publishes it at that path. That is the same standard #48's
original report met for `chatterbox`, but it is not a load.

## Outcome

- #48 closed, with my bad measurement retracted in the same comment.
- #79 opened for the under-declaration, with the seven packages and the
  false-positive traps the check has to avoid.
- Fix committed in the fork, unpushed, pending review.
