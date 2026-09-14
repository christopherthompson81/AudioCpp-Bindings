# Reimplementing audio.cpp's server API in C#

Chronological log of the run-look-decide loops while building `AudioCpp.Server`.
Route-by-route implementation work is not logged here; only the runs whose
output changed what was built next.

## Run 1 — 2026-09-13 16:55 — `POST /v1/audio/transcriptions` returns an empty 200

**Command**

```
AUDIOCPP_ASR_MODEL=.../parakeet-tdt-0.6b-v3-q8_0.gguf AUDIOCPP_ASR_FAMILY=parakeet_tdt \
AUDIOCPP_ASR_AUDIO=$A/sample_16k.wav AUDIOCPP_BACKEND=cuda \
dotnet tests/AudioCpp.ServerTest/bin/Debug/net10.0/AudioCpp.ServerTest.dll
```

**Question** — the route answers 200 but the client reads nothing. Did the
handler fail, or did the response get lost on the way out?

**Raw finding**

```
  ok    JSON transcription is 200
  empty body: status 200, type '', len 0
  server log:
    16:55:22  POST /v1/audio/transcriptions  asr  14.07s  5422 ms
```

The handler ran to completion — that log line is written after `session.Run`
and before `Results.Json`. So the transcript existed and was thrown away.

`/health` and `/v1/models` return anonymous types through `Results.Json` and
work, which ruled out the first guess (that `CreateSlimBuilder`'s trimming-
oriented JSON configuration cannot serialise anonymous types). The difference
is the route's own shape, not serialisation.

**Cause** — `MapPost` has both a `Delegate` and a `RequestDelegate` overload.
The handler was registered as

```csharp
app.MapPost("/v1/audio/transcriptions", (HttpContext http) =>
    TranscribeAsync(http, pool, log, detailed: false));
```

`Task<IResult>` converts to `Task`, so that lambda matches `RequestDelegate` —
whose return value is discarded. The route runs correctly and writes nothing.
`/v1/audio/speech` escaped this only because its lambda is `async` with a body,
which cannot bind to `RequestDelegate`.

Taking `HttpRequest` and a `CancellationToken` instead of `HttpContext` makes
the `RequestDelegate` overload inapplicable, so the compiler can only pick the
`Delegate` one. That is a deliberate signature choice, not a stylistic one, and
it is commented as such at the call site.

**Dead end worth keeping** — one rerun after the fix still showed the empty
body, which briefly looked like the diagnosis being wrong. It was a stale
artifact: only `AudioCpp.Server.csproj` had been rebuilt, and the test loads its
own `bin` copy of that DLL.

A second false signal came from checking the artifact with `strings`, which
reported the new code absent from a DLL that contained it — .NET string literals
live in the `#US` heap as UTF-16, so `strings` needs `-el`. Checking an artifact
is only worth doing if the check itself can see what it is looking for.

## Run 2 — 2026-09-13 17:05 — a declared option the engine refuses

**Question** — with the body fixed, one assertion still failed:

```
FAIL  a language the model may not declare does not break the request
      {"error":{"message":"audiocpp_session_run: unknown Parakeet TDT request
       option: language (runtime error)", ...
```

The route already guarded `SetOption("language", …)` behind the model's own
declared request options. Why did the option reach the engine anyway?

**Raw finding** — `src/capi/audiocpp.cpp:589`:

```c
audiocpp_status audiocpp_request_set_text(audiocpp_request * request,
                                          const char * text, const char * language) {
    ...
    if (language != nullptr && *language != '\0') {
        request->request.options["language"] = language;
    }
```

`set_text` sets the transcript language *and* injects the request option, to
mirror what the CLI's `--language` does. The guard covered `set_option` but the
`set_text` call two lines below it put the option back.

**Implication** — for this ABI, "pass the language as the text language" and
"pass the language as an option" are not two choices; they are one. A caller
that must not send the option to a model that does not declare it also cannot
send the transcript language. The route now derives one `language` value from
the declaration check and feeds it to `set_text`, and says why in a comment so
the next person does not re-separate them.

This is a reasonable ABI decision (CLI parity) with a consequence that is not
visible from the header, and it is worth raising upstream alongside the other
findings: there is no way to set `transcript.language` without also setting
`options["language"]`.

## Run 3 — 2026-09-13 17:40 — review of the transcription routes

Not a probe; the findings from reading the branch back before merging, kept
here because one of them was a bug that no test would have caught until it
mattered.

**A data race in the model pool.** `UseModelAsync` answered "which model is
this session's?" from a `Dictionary<string, AudioCppModel>` kept beside the
pool's `ConcurrentDictionary` of entries. The gate that guards loading is *per
model id*, so two ids loading at once — which is exactly what a non-lazy config
does on startup, and what two requests to different models do — wrote to that
unsynchronised dictionary concurrently. It never failed in testing because the
test config loads one model.

The table was also unnecessary: `entry.Model` already held the answer inside
the gate. Removed, and `UseAsync` now delegates to `UseModelAsync` rather than
the reverse.

**Two gaps in the request parser**, both fixed with tests that fail without
the fix:

- A malformed upload's route to a 400 was assumed, not demonstrated. It holds —
  `Wav.Read` throws `InvalidDataException` on a bad magic number and
  `EndOfStreamException` (an `IOException`) on a truncation, and both are in
  the handler's filter — but "assumed to hold" is how a 500 gets shipped. Two
  assertions now cover both shapes.
- The multipart form silently dropped engine options that the JSON form
  accepted. Options now ride in one `options` JSON part. Forwarding every
  unrecognised form field instead would have looked tidier and broken real
  Whisper clients, which send `response_format`, `temperature` and
  `timestamp_granularities[]` as a matter of course — the engine rejects
  options it does not declare, so those would turn a valid request into a
  failed one.

## Run 4 — 2026-09-13 18:20 — the language guard was wrong in the other direction

**Command** — the alignment route's first end-to-end run, against
`qwen3_forced_aligner` on CUDA with `-F language=en` as upstream's own example
sends it.

**Question** — does the declared-options guard from Run 2 generalise?

**Raw finding**

```
FAIL  alignment is 200  {"error":{"message":"audiocpp_session_run: Qwen3 forced
  aligner run() requires transcript text and language (runtime error)", ...
```

The guard dropped the language, because the aligner does not declare it:

```json
// model_specs/qwen3_forced_aligner.json
"options": {"request": [{"name": "clamp_timestamps_to_audio", ...}]}
```

And yet `run()` refuses without it. So "does not declare `language`" means
**must not send it** for Parakeet TDT and **must send it** for this aligner.
The declaration does not distinguish the two, and Run 2's guard — which looked
correct against the only model it was tested on — is wrong for every model of
the second kind.

**Why the two differ.** 35 families call
`validate_spec_backed_request_options` (`include/engine/framework/runtime/spec_backed_model.h:59`),
which throws on any key not in the contract. Parakeet is one. The aligner is
not: it validates only its *session* options, so an undeclared `language`
reaches it harmlessly — it is the missing transcript language, not the extra
option, that it objects to.

**Implication** — there is no static signal that answers the question, so the
route stops guessing: it sends the language, and if the engine comes back with
`unknown … request option: language`, it records that for the model id and
retries once without. A strict family pays one failed validation per server
lifetime — raised before any inference — and every later request is clean. A
family that needs the language gets it.

This makes the upstream case (#65) stronger than it looked in Run 2: the
coupling in `set_text` is not merely undocumented, it is unresolvable from the
client side. Noted on that issue.

## Run 5 — 2026-09-13 19:30 — the reference server proves the ABI gap

Reading upstream's `/v1/tasks/run` while implementing #59 turned up the piece
that settles #65.

`app/server/runtime.cpp:3045`:

```cpp
engine::runtime::TaskRequest drop_unsupported_language_option(
    engine::runtime::TaskRequest request, const Value & request_json, bool accepts_language_option) {
    if (accepts_language_option) return request;
    ...
    const auto it = request.options.find("language");
    if (it != request.options.end() && it->second == top_level->as_string()) {
        request.options.erase(it);          // the option only
    }
    return request;
}
```

It erases `options["language"]` and leaves `text_input.language` alone. That is
why the C++ server needs no probing: Parakeet TDT never sees the option, the
forced aligner still gets its transcript language.

**A C ABI client cannot write that function.** `audiocpp_request_set_text` is
the only route to `text_input.language` and sets the option as a side effect,
so the two arrive together or not at all — the exact pair this function exists
to separate. The ABI is strictly less expressive than the engine it wraps, in a
way the reference server demonstrates is load-bearing. Added to #65; the
learned-refusal fallback from Run 4 stands as the only workaround available
from this side.

Upstream also caches the answer per model at registration
(`refresh_model_option_flags`), which is the same shape as the
`RefusesLanguage` cache here, reached from the other direction: upstream can
ask the contract because it also has the separation, so it never needs to
observe a refusal.

## Run 6 — 2026-09-13 21:10 — streaming ASR is fed, not handed a clip

**Question** — `stream=true` on `/v1/audio/transcriptions` returned 500 with the
audio attached to the request the way the offline path attaches it.

**Raw finding**

```
{"error":{"message":"audiocpp_stream_finish: Parakeet TDT buffered streaming
  finalize() requires streamed audio (runtime error)","type":"engine_error"}}
```

A streaming session's request carries the *format*, not the samples: a
one-frame buffer declares 16 kHz mono, and the audio arrives through
`PushStream`. That is what `LiveTranscription.cs` in the GUI already does; the
server route was written from the offline route's shape instead and inherited
the wrong assumption.

Pushing the clip in policy-sized windows also turns the route into a real
stream — 6 deltas over a 14 s clip instead of one at the end.

**A second finding, in the deltas themselves.** The engine's `partial_text` for
this family is the *cumulative* transcript, and upstream forwards it verbatim
into a field named `delta`:

```
delta: "Some call me nat"
delta: "Some call me nature. Others call me"
delta: "Some call me nature. Others call me Mother Nature. I've"
```

A client appending these — which is what OpenAI's `transcript.text.delta` means
— gets `Some call me natSome call me nature…`. Filed upstream. This side
forwards it as upstream does, and the test asserts only self-consistency: every
delta a prefix of the final transcript, each extending the one before. That
holds whichever convention a family follows.

## Run 7 — 2026-09-13 21:40 — two vacuous checks and a fatal error handler

Three things the first live-ingest run exposed, all in code I had just written.

**The timing assertion measured nothing.** `ReadAsync` started its stopwatch
when the *body* was first read, so a response that had already completed
reported "first delta at 0 ms of 0 ms" and passed. Starting the clock before
the request is sent gave the real number — first delta at 1198 ms of 1407 ms.

That number then showed the assertion was wrong anyway. Parakeet TDT is a
*buffered* streaming family: it accumulates and emits most deltas near the end,
so "the first delta lands in the first 90%" was measuring the model and sat 3%
from failing on a different machine. The route's actual responsibility is
flushing as produced, so the check is now that the deltas do not all share one
arrival time — 4 deltas over 159 ms for live ingest, which a buffering server
could not produce.

**The mid-stream error handler destroyed the connection it was explaining.** A
body ending mid-frame is detected after the SSE stream has started, and the
handler called `Sse.Begin`, which sets status and headers — illegal once a byte
is out. It threw inside the catch, the connection was torn down, and the client
reported `The response ended prematurely`. Which is exactly the failure the
error event exists to prevent: a truncated transcript indistinguishable from a
finished one. `Sse.Attach` writes to an already-started stream, and the client
now sees an `error` event and no `[DONE]`.

**Comparing across modes tested the model.** "Live ingest transcribes what the
file route does" compared streaming output against offline output, and the same
model in streaming mode capitalises differently (`nature` vs `Nature`). The
comparison now runs live against the file-backed *stream*, so the transport is
the only variable.

**Not verified:** `/v1/audio/speech/live` with real audio. It needs an s2s
model in streaming mode (personaplex), which is not on this machine. Its
request handling and guards are covered; its audio path is not.

## Run 8 — 2026-09-13 18:15 — a check that passed alone and failed in sequence

The server page now writes a real `server.json` when it starts and restores it
at construction. `--server-check` passed immediately after that change and
failed on the next full pass, with four failures and a connection refused.

Nothing had changed in between. The check was reading the config the *previous*
run of the same check had written: it restored a model row from last time,
added a second, and then asserted against a page that held two.

Two problems, not one:

- The check was stateful, which the `Models.Clear()` at its start fixes.
- More seriously, **a check was writing to the real config directory**.
  `~/.config/audiocpp-studio/server.json` had been created by a check run, so
  anyone running the checks would find their Server page holding
  `/models/not-loaded-because-lazy.gguf` afterwards. Removed, and every
  `--*-check` now runs with `XDG_CONFIG_HOME` pointed at a scratch directory,
  set before Avalonia builds anything because the view model restores that
  config in its constructor.

The settings check already did this for itself; the pattern was there and the
new code did not follow it. Worth noting that the failure only appeared because
the checks were run twice in a row — a single run is not enough to catch a
check that pollutes its own inputs.
