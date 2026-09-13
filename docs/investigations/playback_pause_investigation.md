# The playback pause flake

`scripts/run-tests.sh` reported `playback: 1 failure(s)` once, then passed
eleven times running. Chasing which assertion failed, whether it was a test
problem or a real one, and whether the fix was load-bearing.

All runs on Linux, PulseAudio, miniaudio 0.11.25, a 1 s 440 Hz tone at 16 kHz
mono. One buffer is about 300 frames, ~19 ms.

## Run 1 — 2026-09-13 17:40 — which assertion?

Question: the first run told me only that something in playback failed. I
assumed the harness was swallowing stderr. It is not — see Run 6 — I had piped
its output through `tail -25`, and the playback section was above the window.

    for i in $(seq 1 8); do ./scripts/run-tests.sh <build> ; done
    -> 8/8 passed

Not reproducible through the harness. Implication: run the test binary
directly and keep stderr.

## Run 2 — 2026-09-13 17:44 — reproduced in one attempt

    dotnet tests/AudioCpp.AudioTest/bin/Debug/net10.0/AudioCpp.AudioTest.dll
    -> paused playback advanced: 4200 -> 4500

300 frames. One buffer.

`audioio_player_pause` only clears a flag; the device is deliberately left
running, because starting one costs tens of milliseconds and clips the head of
every play. So a callback already in flight, or one that samples the flag just
before the store, advances `position` once more after pause returns.

Implication: pause is not immediate, and the reported position says so.

## Run 3 — 2026-09-13 17:52 — an exact pause without blocking

Two ways to make pause immediate: block the caller until the callback has
observed the flag (~19 ms on the UI thread), or snapshot the position at pause
and report the snapshot while paused. Took the second — non-blocking, exact,
and resuming from the snapshot replays at most one buffer, which is less
noticeable than a gap.

    ./scripts/build-native.sh && <15 runs>
    -> FAILED run 4: pause did not take: 4500 -> 4800

My new assertion was wrong, not the fix. It compared the position after pause
against a reading taken *before* the pause — but the clip was playing in
between, so it may legitimately have advanced. The guarantee is that nothing
moves *after* pause returns, which is a different statement.

Implication: the original assertion had the same flaw. Comparing against a
stale reading is what made it fail under load, when the gap between the two
reads is longest.

## Run 4 — 2026-09-13 18:01 — the corrected assertion, 25 runs

    <25 runs with the corrected test and the native fix>
    -> failures: 0/25

## Run 5 — 2026-09-13 18:06 — is the native fix load-bearing?

Question: with the assertion corrected, does the C change still matter, or was
this only ever a bad test? Worth asking before keeping a change to the audio
thread's contract.

    git stash -- native/audioio/audioio.c && ./scripts/build-native.sh
    <30 runs with the corrected test and the old pause>
    -> 0/30 failed

So the C change is not what fixed the flake. The whole observed failure was the
assertion comparing against a stale reading.

Dropped it. The race it closed is real but microseconds wide — between `pause`
returning and the caller reading the position — against a buffer period of
~19 ms, so 30 runs could not have detected it either way, and a clean 30 is not
evidence that it cannot happen. It is not worth changing the audio thread's
contract, and making resume replay up to a buffer, for a playhead artefact
nobody can see.

Negative result worth keeping: a fix that passes is not thereby a fix that was
needed. The way to tell is to take it out and see whether the test still
notices.

## What shipped

Only the test change: assert that the position does not move *after* pause
returns, rather than that it still equals a reading taken before it.

No harness change. See Run 6.

## Run 6 — 2026-09-13 18:20 — the harness was never the problem

Question: Run 1 assumed `run-tests.sh` swallowed stderr, and the #40 comment
said so. Worth checking before "fixing" something that works.

Wrote an unconditional `Console.Error.WriteLine("  STDERR-PROBE")` into the
playback check and ran the harness both ways:

    ./scripts/run-tests.sh <build> 2>/dev/null | grep -c STDERR-PROBE  -> 0
    ./scripts/run-tests.sh <build> 2>&1       | grep -c STDERR-PROBE  -> 1

`run()` is a plain `dotnet run` with no redirection, so stderr reaches the
terminal intact. The assertion message was there on the first failing run; I
cropped it out looking at the tail of the output. Corrected the comment on #40.

Negative result worth keeping: the second wrong assumption in this
investigation was about my own tooling, and both times the cost of checking
was one command.

## Run 7 — 2026-09-13 18:35 — the corrected assertion had the same race

Reviewing the diff before merging: the new reading was taken immediately after
`Pause()`, so the in-flight callback could still land between that read and the
check 200 ms later. Narrow enough that a few dozen runs would not find it, wide
enough to fail eventually — the same shape as the bug being fixed, which makes
shipping it circular.

The reading is now taken after a 60 ms settle, which asserts the durable
property (the position stops moving once paused) rather than sub-callback
timing. That is the honest contract given the device is deliberately left
running.

    <2 x 10 runs>
    -> 0/10, 0/10

Batches of ten rather than thirty: the 30-run loops were being killed as
"low memory" while 103 GB was available — the detector reads `free`, which is
low because 94 GB is page cache.

## Still open

`audioio_player_pause` clears a flag and returns while the device keeps
running, so the underlying position can advance one more buffer. If a visible
playhead jump after pause is ever reported, the snapshot approach tried in
Run 3 is written up above and took about ten lines.
