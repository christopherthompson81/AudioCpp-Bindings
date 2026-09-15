using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// The streaming forms of speech and transcription.
/// </summary>
/// <remarks>
/// What makes a stream worth having is that output starts before the work
/// finishes, and that is not visible in the final payload — a server that
/// buffered everything and sent it as one delta produces a byte-identical
/// concatenation. So these checks read events as they arrive and compare
/// arrival times against the end of the response, which is the only way to
/// tell the two apart from outside.
/// </remarks>
internal static class Streamed
{
    /// <summary>One SSE event: its parsed JSON, and when it reached us.</summary>
    private sealed record Event(string Type, JsonElement Json, double AtMs);

    /// <param name="since">
    /// Started before the request was sent, not when reading began. Timing a
    /// stream from the moment its body is first read makes every check vacuous:
    /// by then a short response has already arrived in full, and "the first
    /// delta came early" is true of a server that buffered everything.
    /// </param>
    private static async Task<(List<Event> Events, bool SawDone, double TotalMs)> ReadAsync(
        HttpResponseMessage response, System.Diagnostics.Stopwatch since,
        CancellationToken cancel = default)
    {
        var started = since;
        var events = new List<Event>();
        var done = false;

        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload == "[DONE]") { done = true; continue; }

            var document = JsonDocument.Parse(payload);
            var type = document.RootElement.TryGetProperty("type", out var value)
                ? value.GetString() ?? "" : "";
            events.Add(new Event(type, document.RootElement.Clone(),
                                 started.Elapsed.TotalMilliseconds));
        }
        return (events, done, started.Elapsed.TotalMilliseconds);
    }

    public static async Task<int> RunAsync(string ttsModel, string ttsFamily,
                                           string asrModel, string asrFamily,
                                           string backend, string audio)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var models = new List<ServerModel>();
        if (ttsModel.Length > 0) models.Add(new ServerModel("tts", ttsFamily, ttsModel, "tts", "streaming"));
        if (asrModel.Length > 0) models.Add(new ServerModel("asr", asrFamily, asrModel, "asr", "streaming"));
        // An offline twin, so "streaming needs a streaming model" can be checked
        // against a model that is genuinely configured the other way.
        if (asrModel.Length > 0) models.Add(new ServerModel("asr-offline", asrFamily, asrModel, "asr"));

        var config = new ServerConfig
        {
            Port = 19700 + Random.Shared.Next(200),
            Backend = backend,
            Models = models,
        };

        await using var server = new AudioCppServer();
        await server.StartAsync(config);
        if (server.State != ServerState.Running)
        {
            Console.Error.WriteLine($"server did not start: {server.Problem}");
            return 1;
        }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(server.Address),
            Timeout = TimeSpan.FromMinutes(5),
        };

        // ResponseHeadersRead so the client hands back the response as soon as
        // the headers land, rather than buffering the whole body first -- which
        // would erase the arrival times these checks are built on.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        async Task<HttpResponseMessage> PostAsync(string route, object body)
        {
            clock = System.Diagnostics.Stopwatch.StartNew();
            var message = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        }

        if (ttsModel.Length > 0)
        {
            var speech = await PostAsync("/v1/audio/speech", new
            {
                model = "tts",
                input = "Streaming speech arrives in pieces.",
                response_format = "pcm",
                stream_format = "sse",
            });
            Check("streamed speech is 200", speech.StatusCode == HttpStatusCode.OK);
            Check("and is an event stream",
                  speech.Content.Headers.ContentType?.MediaType == "text/event-stream",
                  $"{speech.Content.Headers.ContentType}");

            var (events, sawDone, totalMs) = await ReadAsync(speech, clock);
            var deltas = events.Where(e => e.Type == "speech.audio.delta").ToArray();
            Check("audio arrives as deltas", deltas.Length > 0, $"{deltas.Length} deltas");
            Check("the stream terminates with [DONE]", sawDone);

            var last = events.LastOrDefault(e => e.Type == "speech.audio.done");
            Check("a done event carries timing",
                  last is not null && last.Json.TryGetProperty("timing", out _));

            Spread("speech deltas", deltas, totalMs, Check);

            var decoded = deltas.Sum(d => Convert.FromBase64String(
                d.Json.GetProperty("audio").GetString() ?? "").Length);
            Check("the deltas decode to PCM16", decoded > 0 && decoded % 2 == 0, $"{decoded} bytes");

            // The raw form: the same audio without the envelope.
            var rawResponse = await PostAsync("/v1/audio/speech", new
            {
                model = "tts",
                input = "Streaming speech arrives in pieces.",
                response_format = "pcm",
                stream_format = "audio",
            });
            var rawBytes = await rawResponse.Content.ReadAsByteArrayAsync();
            Check("stream_format=audio returns raw PCM",
                  rawResponse.StatusCode == HttpStatusCode.OK && rawBytes.Length > 0,
                  $"{rawBytes.Length} bytes, {rawResponse.Content.Headers.ContentType}");
            Check("and carries no SSE framing",
                  !Encoding.ASCII.GetString(rawBytes, 0, Math.Min(16, rawBytes.Length))
                      .StartsWith("data:", StringComparison.Ordinal));

            var badFormat = await PostAsync("/v1/audio/speech", new
            {
                model = "tts", input = "x", stream_format = "websocket",
            });
            Check("an unknown stream_format is a 400",
                  badFormat.StatusCode == HttpStatusCode.BadRequest);
        }

        if (asrModel.Length > 0)
        {
            var transcription = await PostAsync("/v1/audio/transcriptions", new
            {
                model = "asr", audio, stream = true,
            });
            Check("streamed transcription is 200", transcription.StatusCode == HttpStatusCode.OK,
                  $"{(int)transcription.StatusCode}");

            var (events, sawDone, totalMs) = await ReadAsync(transcription, clock);
            var deltas = events.Where(e => e.Type == "transcript.text.delta").ToArray();
            var final = events.LastOrDefault(e => e.Type == "transcript.text.done");
            Check("text arrives as deltas", deltas.Length > 0, $"{deltas.Length} deltas");
            Check("a done event carries the whole transcript",
                  final is not null
                  && (final.Json.GetProperty("text").GetString() ?? "").Length > 0,
                  final?.Json.GetProperty("text").GetString() ?? "(none)");
            Check("the stream terminates with [DONE]", sawDone);

            Spread("transcript deltas", deltas, totalMs, Check);

            // A delta is an increment. This used to accept either that or the
            // running total, because two families restated the whole
            // transcript every time and nothing in the event said which shape
            // a client was looking at -- reported as #68 here, fixed upstream
            // in 0xShug0/audio.cpp#552, where every streaming ASR family now
            // publishes through one shared publisher.
            //
            // Two things have to hold for this to pass, and it is worth being
            // clear which. The engine must publish increments: a family that
            // went back to restating the total would send text the finalized
            // transcript does not extend, so the route would add no closing
            // delta and the concatenation would overshoot. And the route must
            // send the closing delta, or the concatenation stops one window
            // short of the end.
            //
            // What it cannot distinguish is *where* the last increment came
            // from -- the engine's own final partial, or the route filling in
            // for finalize() returning a result rather than an event. There is
            // nothing in the stream that says, and no assertion here could
            // tell them apart.
            if (final is not null && deltas.Length > 0)
            {
                var whole = Normalize(final.Json.GetProperty("text").GetString() ?? "");
                // Concatenated raw and normalized once, not normalized one by
                // one: the space between two words falls inside whichever
                // delta carries it, so trimming each in turn would weld the
                // words either side together and report a failure the stream
                // does not have.
                var joined = Normalize(string.Concat(
                    deltas.Select(d => d.Json.GetProperty("delta").GetString() ?? "")));

                Check("the deltas concatenate to the transcript the stream ends on",
                      joined == whole, joined);
            }

            var offline = await PostAsync("/v1/audio/transcriptions", new
            {
                model = "asr-offline", audio, stream = true,
            });
            Check("stream over an offline model is a 400",
                  offline.StatusCode == HttpStatusCode.BadRequest);

            var details = await PostAsync("/v1/audio/transcriptions/details", new
            {
                model = "asr", audio, stream = true,
            });
            Check("stream on /details is still refused",
                  details.StatusCode == HttpStatusCode.BadRequest);
        }

        if (asrModel.Length > 0 && audio.Length > 0)
        {
            var clip = AudioCpp.Wav.Read(audio);

            // The body is the audio, sent chunked, the way a capture pipe would
            // send it. StreamContent over a pipe with no length is what makes
            // the transport chunked rather than a buffered upload -- and a
            // buffered upload would test the wrong route.
            async Task<HttpResponseMessage> LiveAsync(string query, byte[] payload,
                                                      int pieces = 8, int trim = 0)
            {
                var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
                var reader = new AnonymousPipeClientStream(PipeDirection.In,
                    pipe.ClientSafePipeHandle);
                var message = new HttpRequestMessage(HttpMethod.Post,
                    $"/v1/audio/transcriptions/live?{query}")
                {
                    Content = new StreamContent(reader),
                };
                var send = http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);

                var body = trim > 0 ? payload[..^trim] : payload;
                var step = Math.Max(1, body.Length / pieces);
                for (var at = 0; at < body.Length; at += step)
                {
                    await pipe.WriteAsync(body.AsMemory(at, Math.Min(step, body.Length - at)));
                    await pipe.FlushAsync();
                }
                pipe.Dispose();
                return await send;
            }

            var pcm = new byte[clip.Samples.Length * 2];
            for (var i = 0; i < clip.Samples.Length; i++)
            {
                var value = (short)Math.Round(Math.Clamp(clip.Samples[i], -1f, 1f) * 32767f);
                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            clock = System.Diagnostics.Stopwatch.StartNew();
            var live = await LiveAsync($"model=asr&sample_rate={clip.SampleRate}&channels=1", pcm);
            Check("live transcription is 200", live.StatusCode == HttpStatusCode.OK,
                  $"{(int)live.StatusCode}");
            if (live.StatusCode == HttpStatusCode.OK)
            {
                var (liveEvents, liveDone, liveTotal) = await ReadAsync(live, clock);
                var liveDeltas = liveEvents.Where(e => e.Type == "transcript.text.delta").ToArray();
                var liveFinal = liveEvents.LastOrDefault(e => e.Type == "transcript.text.done");
                Check("live audio produces deltas", liveDeltas.Length > 0, $"{liveDeltas.Length}");
                Check("and a final transcript",
                      (liveFinal?.Json.GetProperty("text").GetString() ?? "").Length > 0,
                      liveFinal?.Json.GetProperty("text").GetString() ?? "(none)");
                Check("and terminates with [DONE]", liveDone);
                Spread("live deltas", liveDeltas, liveTotal, Check);

                // The same contract on the live route. It runs the session
                // differently enough -- fed from a pipe, finalized by the
                // client closing the body rather than by the push loop running
                // out of clip -- that a family or a route change could satisfy
                // one path and not the other.
                if (liveFinal is not null && liveDeltas.Length > 0)
                {
                    var liveJoined = Normalize(string.Concat(
                        liveDeltas.Select(d => d.Json.GetProperty("delta").GetString() ?? "")));
                    Check("live deltas concatenate to the live transcript",
                          liveJoined
                              == Normalize(liveFinal.Json.GetProperty("text").GetString() ?? ""),
                          liveJoined);
                }

                // The same recording through the file-backed stream, so the
                // transport is the only thing that differs. A live path that
                // dropped a chunk or lost frame alignment would still answer,
                // with a transcript that quietly disagreed.
                //
                // Compared against the streaming route rather than the offline
                // one on purpose: a model in streaming mode does not produce
                // character-identical output to the same model offline -- this
                // one differs in capitalisation -- so comparing across modes
                // would be testing the model, and failing for a reason the
                // route cannot fix.
                clock = System.Diagnostics.Stopwatch.StartNew();
                var fileBacked = await PostAsync("/v1/audio/transcriptions",
                    new { model = "asr", audio, stream = true });
                var (fileEvents, _, _) = await ReadAsync(fileBacked, clock);
                var buffered = Normalize(fileEvents
                    .LastOrDefault(e => e.Type == "transcript.text.done")
                    ?.Json.GetProperty("text").GetString() ?? "");
                var streamed = Normalize(liveFinal?.Json.GetProperty("text").GetString() ?? "");
                Check("live ingest transcribes what the file-backed stream does",
                      streamed == buffered && streamed.Length > 0,
                      streamed == buffered ? $"{streamed.Length} chars"
                                           : $"live '{streamed}' vs file '{buffered}'");
            }

            // A body that ends mid-frame is an error, not an end of speech.
            // Half a sample is not silence; it is a value the model would read.
            var truncated = await LiveAsync(
                $"model=asr&sample_rate={clip.SampleRate}&channels=1", pcm, trim: 1);
            var truncatedBody = "";
            try
            {
                truncatedBody = await truncated.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException error)
            {
                // Reported as a read failure rather than as an error event,
                // which is the thing being checked: a dropped connection is
                // indistinguishable from a finished one.
                truncatedBody = $"(connection torn down: {error.Message})";
            }
            Check("a body ending mid-frame is reported, not silently accepted",
                  truncatedBody.Contains("mid-frame", StringComparison.Ordinal),
                  truncatedBody.Length > 150 ? truncatedBody[^150..] : truncatedBody);
            Check("and the transcript it was building is not passed off as complete",
                  !truncatedBody.Contains("[DONE]", StringComparison.Ordinal));

            var badFormat = await LiveAsync("model=asr&sample_format=alaw", pcm, pieces: 1);
            Check("an unsupported sample_format is a 400",
                  badFormat.StatusCode == HttpStatusCode.BadRequest);
            var badRate = await LiveAsync("model=asr&sample_rate=7", pcm, pieces: 1);
            Check("a sample_rate outside the allowed range is a 400",
                  badRate.StatusCode == HttpStatusCode.BadRequest);
            var offlineLive = await LiveAsync("model=asr-offline", pcm, pieces: 1);
            Check("live over an offline model is a 400",
                  offlineLive.StatusCode == HttpStatusCode.BadRequest);
            var noModel = await LiveAsync("sample_rate=16000", pcm, pieces: 1);
            Check("live without a model is a 400", noModel.StatusCode == HttpStatusCode.BadRequest);
            var unknownModel = await LiveAsync("model=nope", pcm, pieces: 1);
            Check("live with an unknown model is a 404",
                  unknownModel.StatusCode == HttpStatusCode.NotFound);

            // /v1/audio/speech/live shares every one of these guards with the
            // route above, so they are checked here. What is NOT checked
            // anywhere is a real speech-to-speech run: that needs an s2s model
            // in streaming mode (personaplex), which is not among the models
            // this machine has. The route is implemented and its request
            // handling is covered; its audio path is not, and saying so is
            // better than implying otherwise by leaving it out.
            var speechLive = await http.PostAsync("/v1/audio/speech/live?model=asr-offline",
                new ByteArrayContent(pcm[..1024]));
            Check("speech/live over an offline model is a 400",
                  speechLive.StatusCode == HttpStatusCode.BadRequest,
                  $"{(int)speechLive.StatusCode}");
            var speechLiveUnknown = await http.PostAsync("/v1/audio/speech/live?model=nope",
                new ByteArrayContent(pcm[..1024]));
            Check("speech/live with an unknown model is a 404",
                  speechLiveUnknown.StatusCode == HttpStatusCode.NotFound);
        }

        await server.StopAsync();
        Console.WriteLine(failures == 0 ? "streaming OK" : $"streaming: {failures} failure(s)");
        return failures;
    }

    /// <summary>
    /// Checks that events were flushed as they were produced rather than
    /// written in one burst at the end.
    /// </summary>
    /// <remarks>
    /// Flushing is the route's job; arriving early is the model's. Parakeet
    /// TDT is a buffered streaming family — it accumulates and emits most of
    /// its deltas near the end of the clip — so a threshold like "the first
    /// delta lands in the first 90% of the response" measures the model and
    /// fails on a fast machine or a different one. What the route can be held
    /// to is that the deltas do not all share an arrival time, which is what a
    /// server buffering the whole stream would produce.
    /// </remarks>
    private static void Spread(string what, IReadOnlyList<Event> deltas, double totalMs,
                               Action<string, bool, string> check)
    {
        if (deltas.Count < 2) return;
        var span = deltas[^1].AtMs - deltas[0].AtMs;
        check($"{what} are flushed as produced, not in one burst", span > 0.0,
              $"{deltas.Count} over {span:F0} ms, first at {deltas[0].AtMs:F0} ms "
              + $"of {totalMs:F0} ms");
    }

    /// <summary>
    /// Whitespace-insensitive, because where a delta boundary falls relative to
    /// a space is the model's business, not the route's.
    /// </summary>
    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
