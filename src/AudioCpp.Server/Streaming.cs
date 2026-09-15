using System.Diagnostics;
using System.Text;
using AudioCpp;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// The streaming halves of the speech and transcription routes.
/// </summary>
/// <remarks>
/// Two shapes, and the difference is where the input comes from rather than
/// what goes back. A file-backed stream has the whole request up front and
/// streams only its output; a live one reads the request body as it arrives and
/// can answer before it ends. They share the event vocabulary so a client can
/// use one reader for both.
/// </remarks>
internal static class Streaming
{
    /// <summary>PCM16 little-endian, which is what every one of these routes speaks.</summary>
    private static byte[] EncodePcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            // Clamped before scaling, not after: a model that overshoots by a
            // little would otherwise wrap to full-scale the other way, which is
            // heard as a click rather than as the clipping it is.
            var value = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)Math.Round(value * 32767.0f, MidpointRounding.AwayFromZero);
            bytes[i * 2] = (byte)(pcm & 0xFF);
            bytes[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// Drives a streaming session that has all of its input already, emitting
    /// each event as the model produces it.
    /// </summary>
    private static async Task PumpAsync(
        AudioCppSession session, Func<AudioCppResult, Task> onEvent, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            using var next = session.NextStreamEvent();
            if (next is null) break;
            await onEvent(next.Result);
            if (next.IsFinal) break;
        }
    }

    /// <summary>
    /// The increment a finalized transcript adds to the deltas already sent,
    /// or an empty string when it does not simply extend them.
    /// </summary>
    /// <remarks>
    /// The last window a streaming ASR decodes is decoded inside finalize(),
    /// and this ABI hands that back as a result rather than as one more event:
    /// audiocpp_stream_finish() returns a result and leaves nothing to poll.
    /// So the closing text reaches a client only in transcript.text.done, and
    /// a client that appends deltas -- which is what the OpenAI shape this
    /// mimics tells it to do -- renders a transcript missing its tail for the
    /// whole of the last window. Visible only since the engine's partials
    /// became true increments (0xShug0/audio.cpp#552); while they restated the
    /// running total the last one covered for it.
    ///
    /// Empty when the final transcript is not an extension of what was sent.
    /// A family that revised earlier text cannot be reconciled by appending,
    /// and inventing a delta there would make the stream disagree with itself
    /// rather than merely end early. done carries the whole transcript either
    /// way.
    /// </remarks>
    private static string ClosingDelta(string sent, string final) =>
        final.Length > sent.Length && final.StartsWith(sent, StringComparison.Ordinal)
            ? final[sent.Length..]
            : "";

    public static async Task SpeechAsync(HttpContext http, ModelPool pool, SpeechRequest request,
                                         ServerConfig config, Action<string> log,
                                         CancellationToken cancel)
    {
        var raw = request.StreamFormat == "audio";
        var started = Stopwatch.StartNew();
        double? ttft = null;
        var chunks = 0;

        // Headers go out before the model is touched only for the raw form,
        // where there is no envelope to carry an error anyway. The SSE form
        // starts its stream inside the gate so a failure to acquire the model
        // can still be a status code.
        Sse? sse = null;
        if (raw)
        {
            http.Response.StatusCode = 200;
            http.Response.ContentType = "audio/pcm";
        }

        await pool.UseStreamingAsync(request.Model, async session =>
        {
            using var task = new AudioCppRequest();
            request.ApplyTo(task, config);
            session.StartStream(task);

            sse = raw ? null : Sse.Begin(http.Response);
            await PumpAsync(session, async result =>
            {
                var buffers = new List<AudioBuffer>();
                if (result.Audio is { } audio) buffers.Add(audio);
                foreach (var named in result.NamedAudio)
                {
                    buffers.Add(new AudioBuffer(named.Samples, named.SampleRate, named.Channels));
                }

                foreach (var buffer in buffers)
                {
                    ttft ??= started.Elapsed.TotalMilliseconds;
                    var pcm = EncodePcm16(buffer.Samples);
                    if (sse is not null)
                    {
                        await sse.SendAsync(
                            new { type = "speech.audio.delta", audio = Convert.ToBase64String(pcm) },
                            cancel);
                    }
                    else
                    {
                        await http.Response.Body.WriteAsync(pcm, cancel);
                        await http.Response.Body.FlushAsync(cancel);
                    }
                    chunks++;
                }
            }, cancel);

            using var _ = session.FinishStream();
            return 0;
        }, cancel);

        log($"POST /v1/audio/speech  {request.Model}  stream {request.StreamFormat}  "
            + $"{chunks} chunk(s)  {started.Elapsed.TotalMilliseconds:F0} ms");

        if (sse is null) return;
        if (chunks == 0)
        {
            // A stream that produced nothing is a failure, not an empty
            // success: a client that got [DONE] with no deltas would render
            // silence and report that it worked.
            await sse.ErrorAsync("streaming speech model produced no audio delta events",
                                 "engine_error", cancel);
            return;
        }
        // Measured from the route rather than read from an engine TTFT event:
        // the first audio delta is that event as far as a client can tell, and
        // it includes the transport, which is what a caller waiting on the
        // first byte actually experiences.
        await sse.SendAsync(new { type = "speech.audio.done", timing = new { ttft_ms = Round(ttft) } },
                            cancel);
        await sse.DoneAsync(cancel);
    }

    public static async Task TranscriptionAsync(HttpContext http, ModelPool pool,
                                                TranscriptionRequest request, Action<string> log,
                                                CancellationToken cancel)
    {
        var clip = request.Audio;
        var started = Stopwatch.StartNew();
        double? ttft = null;
        Sse? sse = null;
        var final = "";
        var sent = new StringBuilder();

        await pool.UseStreamingAsync(request.Model, async session =>
        {
            // A streaming ASR session is fed, not handed a clip. Attaching the
            // audio to the request the way the offline path does gets as far as
            // finalize() and then fails with "requires streamed audio" -- the
            // request carries the *format*, and the samples arrive through
            // push. Pushing them is also what makes this a stream rather than
            // one delta at the end: the model sees the recording in order and
            // answers as it goes.
            using var contract = new AudioCppRequest();
            contract.SetAudio(new float[1], clip.SampleRate, clip.Channels);
            session.StartStream(contract);
            sse = Sse.Begin(http.Response);

            async Task SendDeltaAsync(string delta)
            {
                if (delta.Length == 0) return;
                sent.Append(delta);
                ttft ??= started.Elapsed.TotalMilliseconds;
                await sse.SendAsync(new { type = "transcript.text.delta", delta }, cancel);
            }

            Task EmitAsync(AudioCppResult result) => SendDeltaAsync(result.Text?.Text ?? "");

            // The engine names the window it wants; pushing in that size avoids
            // it re-buffering. A second is the fallback when it declines to say.
            var policy = session.GetStreamPolicy();
            var chunk = policy.PreferredChunkSamples > 0
                ? (int)policy.PreferredChunkSamples
                : clip.SampleRate;
            chunk = Math.Max(chunk, 1) * Math.Max(clip.Channels, 1);

            for (var at = 0; at < clip.Samples.Length && !cancel.IsCancellationRequested; at += chunk)
            {
                var take = Math.Min(chunk, clip.Samples.Length - at);
                using var pushed = session.PushStream(
                    clip.Samples.AsSpan(at, take), clip.SampleRate, clip.Channels,
                    at / Math.Max(clip.Channels, 1));
                if (pushed is not null) await EmitAsync(pushed.Result);
                await PumpAsync(session, EmitAsync, cancel);
            }

            using var result = session.FinishStream();
            final = result.Text?.Text ?? "";
            await SendDeltaAsync(ClosingDelta(sent.ToString(), final));
            return 0;
        }, cancel);

        log($"POST /v1/audio/transcriptions  {request.Model}  stream  "
            + $"{started.Elapsed.TotalMilliseconds:F0} ms");

        if (sse is null) return;
        if (final.Length == 0)
        {
            await sse.ErrorAsync("streaming transcription result did not contain transcript text",
                                 "engine_error", cancel);
            return;
        }
        await sse.SendAsync(
            new { type = "transcript.text.done", text = final, timing = new { ttft_ms = Round(ttft) } },
            cancel);
        await sse.DoneAsync(cancel);
    }

    /// <summary>
    /// How a live request declares the PCM it is about to send.
    /// </summary>
    /// <remarks>
    /// Query parameters rather than a JSON body, because the body is the audio.
    /// A headerless stream carries no format, so these are a contract the server
    /// cannot verify: declaring 16 kHz while sending 48 produces a confident,
    /// wrong transcript rather than an error. The ranges below are the one
    /// thing that can be checked — they size the buffer handed to the model
    /// while it holds the lock, so an absurd value would be an allocation
    /// request, not a mistake.
    /// </remarks>
    internal sealed record LiveFormat(int SampleRate, int Channels, bool Float32, string Language)
    {
        public int BytesPerFrame => (Float32 ? 4 : 2) * Channels;

        public static LiveFormat Parse(IQueryCollection query)
        {
            var rate = Int(query, "sample_rate", 16000);
            var channels = Int(query, "channels", 1);
            var format = query["sample_format"].ToString();
            if (format.Length == 0) format = "s16le";
            if (format is not ("s16le" or "f32le"))
            {
                throw new InvalidDataException("sample_format must be s16le or f32le");
            }
            if (rate is < 1000 or > 384000)
            {
                throw new InvalidDataException("sample_rate must be between 1000 and 384000");
            }
            if (channels is < 1 or > 16)
            {
                throw new InvalidDataException("channels must be between 1 and 16");
            }
            return new LiveFormat(rate, channels, format == "f32le", query["language"].ToString());
        }

        private static int Int(IQueryCollection query, string name, int fallback)
        {
            var raw = query[name].ToString();
            if (raw.Length == 0) return fallback;
            if (!int.TryParse(raw, out var value))
            {
                throw new InvalidDataException($"{name} must be an integer");
            }
            return value;
        }

        /// <summary>Decodes one buffer of interleaved PCM into float samples.</summary>
        public int Decode(ReadOnlySpan<byte> bytes, Span<float> into)
        {
            if (Float32)
            {
                var count = bytes.Length / 4;
                for (var i = 0; i < count; i++)
                {
                    into[i] = BitConverter.ToSingle(bytes[(i * 4)..]);
                }
                return count;
            }
            var samples = bytes.Length / 2;
            for (var i = 0; i < samples; i++)
            {
                into[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 32768.0f;
            }
            return samples;
        }
    }

    /// <summary>
    /// Reads the request body as it arrives, handing whole frames to the model
    /// and reporting what comes back.
    /// </summary>
    /// <remarks>
    /// The loop is bounded on four axes because the client decides when this
    /// request ends and the model is held for all of it. A partial frame at the
    /// end of a read is carried into the next one rather than pushed: half a
    /// sample is not silence, it is a value the model would interpret.
    ///
    /// Ending without the terminating chunk is an error, not an end of speech.
    /// A truncated transcript delivered as a normal completion is
    /// indistinguishable from the speaker having stopped, which is the one
    /// failure a caller cannot detect for themselves.
    /// </remarks>
    private static async Task<double> IngestAsync(
        HttpContext http, AudioCppSession session, LiveFormat format, LiveIngestLimits limits,
        Func<AudioCppResult, Task> onEvent, Stopwatch since, CancellationToken cancel)
    {
        var frame = format.BytesPerFrame;
        var window = Math.Max(frame, Math.Min(limits.MaxChunkBytes, 64 * 1024 * frame));
        var buffer = new byte[window];
        var carried = 0;
        var samples = new float[window / (format.Float32 ? 4 : 2) + format.Channels];
        var pushedFrames = 0L;
        var received = 0L;

        while (true)
        {
            if (limits.TotalTimeoutMs > 0 && since.ElapsedMilliseconds > limits.TotalTimeoutMs)
            {
                throw new InvalidDataException(
                    $"live request exceeded total_timeout_ms ({limits.TotalTimeoutMs} ms)");
            }

            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                if (limits.IdleTimeoutMs > 0) idle.CancelAfter(limits.IdleTimeoutMs);
                try
                {
                    read = await http.Request.Body.ReadAsync(
                        buffer.AsMemory(carried, buffer.Length - carried), idle.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new InvalidDataException(
                        $"live request stalled for more than idle_timeout_ms "
                        + $"({limits.IdleTimeoutMs} ms)");
                }
            }
            if (read == 0) break;

            received += read;
            if (limits.MaxBodyBytes > 0 && received > limits.MaxBodyBytes)
            {
                throw new InvalidDataException(
                    $"live request exceeded max_body_bytes ({limits.MaxBodyBytes})");
            }

            var available = carried + read;
            var whole = available - available % frame;
            if (whole > 0)
            {
                var count = format.Decode(buffer.AsSpan(0, whole), samples);
                using var pushed = session.PushStream(
                    samples.AsSpan(0, count), format.SampleRate, format.Channels, pushedFrames);
                pushedFrames += count / Math.Max(format.Channels, 1);
                if (pushed is not null) await onEvent(pushed.Result);
                await PumpAsync(session, onEvent, cancel);
            }

            carried = available - whole;
            if (carried > 0) buffer.AsSpan(whole, carried).CopyTo(buffer);
        }

        if (carried > 0)
        {
            throw new InvalidDataException(
                $"live request ended mid-frame with {carried} trailing byte(s)");
        }
        return since.Elapsed.TotalMilliseconds;
    }

    public static async Task LiveTranscriptionAsync(HttpContext http, ModelPool pool, string id,
                                                    LiveIngestLimits limits, Action<string> log,
                                                    CancellationToken cancel)
    {
        var format = LiveFormat.Parse(http.Request.Query);
        var since = Stopwatch.StartNew();
        double? ttft = null;
        var final = "";
        Sse? sse = null;
        var sent = new StringBuilder();

        await pool.UseStreamingAsync(id, async session =>
        {
            using var contract = new AudioCppRequest();
            contract.SetAudio(new float[1], format.SampleRate, format.Channels);
            // The transcript language alone, not the "language" request option.
            // Setting text purely to carry the language was the old way through
            // this ABI, and it sent the option as a side effect -- which a
            // family that validates its options strictly, parakeet_tdt among
            // them, refuses outright. There is no retry on a live route to
            // recover from that, so ?language= simply failed against those
            // models.
            if (format.Language.Length > 0) contract.SetTextLanguage(format.Language);
            session.StartStream(contract);
            sse = Sse.Begin(http.Response);

            async Task SendDeltaAsync(string delta)
            {
                if (delta.Length == 0) return;
                sent.Append(delta);
                ttft ??= since.Elapsed.TotalMilliseconds;
                await sse.SendAsync(new { type = "transcript.text.delta", delta }, cancel);
            }

            await IngestAsync(http, session, format, limits,
                              result => SendDeltaAsync(result.Text?.Text ?? ""), since, cancel);

            using var result = session.FinishStream();
            final = result.Text?.Text ?? "";
            await SendDeltaAsync(ClosingDelta(sent.ToString(), final));
            return 0;
        }, cancel);

        log($"POST /v1/audio/transcriptions/live  {id}  {since.ElapsedMilliseconds} ms");
        if (sse is null) return;
        await sse.SendAsync(
            new { type = "transcript.text.done", text = final, timing = new { ttft_ms = Round(ttft) } },
            cancel);
        await sse.DoneAsync(cancel);
    }

    public static async Task LiveSpeechAsync(HttpContext http, ModelPool pool, string id,
                                             LiveIngestLimits limits, Action<string> log,
                                             CancellationToken cancel)
    {
        var format = LiveFormat.Parse(http.Request.Query);
        var since = Stopwatch.StartNew();
        double? firstAudioMs = null;
        double inputEndMs = 0;
        var chunks = 0;
        Sse? sse = null;

        await pool.UseStreamingAsync(id, async session =>
        {
            using var contract = new AudioCppRequest();
            contract.SetAudio(new float[1], format.SampleRate, format.Channels);
            // Kept as set_text, unlike the transcription route above: this is a
            // speech family, and the ones that read the language read it from
            // options["language"] the way the CLI's --language delivers it.
            // Sending the transcript language alone would quietly stop reaching
            // them.
            if (format.Language.Length > 0) contract.SetText("", format.Language);
            session.StartStream(contract);
            sse = Sse.Begin(http.Response);

            async Task EmitAsync(AudioCppResult result)
            {
                var buffers = new List<AudioBuffer>();
                if (result.Audio is { } audio) buffers.Add(audio);
                foreach (var named in result.NamedAudio)
                {
                    buffers.Add(new AudioBuffer(named.Samples, named.SampleRate, named.Channels));
                }
                foreach (var buffer in buffers)
                {
                    firstAudioMs ??= since.Elapsed.TotalMilliseconds;
                    await sse.SendAsync(new
                    {
                        type = "speech.audio.delta",
                        audio = Convert.ToBase64String(EncodePcm16(buffer.Samples)),
                    }, cancel);
                    chunks++;
                }
            }

            inputEndMs = await IngestAsync(http, session, format, limits, EmitAsync, since, cancel);

            // Whatever the model still owes after the input ends.
            using var result = session.FinishStream();
            await EmitAsync(result);
            return 0;
        }, cancel);

        log($"POST /v1/audio/speech/live  {id}  {chunks} chunk(s)  {since.ElapsedMilliseconds} ms");
        if (sse is null) return;
        if (chunks == 0)
        {
            await sse.ErrorAsync("live speech model produced no audio delta events",
                                 "engine_error", cancel);
            return;
        }
        await sse.SendAsync(
            new { type = "speech.audio.done", timing = LiveTiming(firstAudioMs ?? 0, inputEndMs) },
            cancel);
        await sse.DoneAsync(cancel);
    }

    /// <summary>
    /// Live speech timing, which reports two different things depending on
    /// which came first.
    /// </summary>
    /// <remarks>
    /// A time to first token only means something when the input had finished:
    /// it is the wait after the last word went in. When output starts while the
    /// client is still speaking there is no such wait, so <c>ttft_ms</c> is
    /// null rather than negative, and <c>overlap_ms</c> reports how far ahead
    /// the model was instead. A negative ttft would be arithmetically right and
    /// read as a measurement error by everyone who saw it.
    /// </remarks>
    private static Dictionary<string, object?> LiveTiming(double firstAudioMs, double inputEndMs)
    {
        var afterInput = firstAudioMs - inputEndMs;
        var before = afterInput < 0.0;
        return new Dictionary<string, object?>
        {
            ["ttft_ms"] = before ? null : Math.Round(afterInput, 1),
            ["first_audio_before_input_end"] = before,
            ["request_start_to_first_audio_ms"] = Math.Round(firstAudioMs, 1),
            ["input_end_ms"] = Math.Round(inputEndMs, 1),
            ["overlap_ms"] = before ? Math.Round(-afterInput, 1) : 0.0,
        };
    }

    private static double? Round(double? value) =>
        value is { } number ? Math.Round(number, 1) : null;
}
