using Microsoft.AspNetCore.Builder;
using System.Diagnostics;
using AudioCpp.Native;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// The HTTP surface, matching audio.cpp's server.
/// </summary>
/// <remarks>
/// Response shapes follow the reference server's rather than anything more
/// convenient: the point of serving this API is that a client written against
/// audio.cpp works unchanged, and a field renamed for taste breaks that
/// silently.
/// </remarks>
internal static class Routes
{
    private static async Task<IResult> TranscribeAsync(HttpContext context, ModelPool pool,
                                                       Action<string> log, bool detailed,
                                                       CancellationToken cancel)
    {
        var http = context.Request;
        TranscriptionRequest request;
        try
        {
            request = await TranscriptionRequest.ReadAsync(http);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException
                                      or IOException or FormatException)
        {
            return Problem(400, "invalid_request_error", error.Message);
        }

        if (!pool.Knows(request.Model))
        {
            return Problem(404, "model_not_found", $"no model with id '{request.Model}'");
        }
        if (request.Stream)
        {
            if (detailed)
            {
                // Upstream's documented 400: SSE carries transcript deltas and
                // has nowhere to put the detail arrays.
                return Problem(400, "invalid_request_error",
                    "stream is not supported on /details; use /v1/audio/transcriptions");
            }
            if (pool.Spec(request.Model)?.Mode != "streaming")
            {
                return Problem(400, "invalid_request_error",
                    "transcription stream=true requires a model configured with mode=streaming");
            }
            try
            {
                await Streaming.TranscriptionAsync(context, pool, request, log, cancel);
                return Results.Empty;
            }
            catch (AudioCppException error) when (!context.Response.HasStarted)
            {
                // Before the first byte a failure can still be a status code.
                // After it, Streaming reports through the stream itself -- see
                // Sse.ErrorAsync.
                log($"POST /v1/audio/transcriptions  {request.Model}  stream failed: {error.Message}");
                return Problem(500, "engine_error", error.Message);
            }
        }

        var started = Stopwatch.StartNew();
        var clip = request.Audio;
        try
        {
            var result = await RunWithLanguageAsync(pool, request.Model, request.Language,
                (session, language) =>
            {
                using var task = new AudioCppRequest();
                task.SetAudio(clip.Samples, clip.SampleRate, clip.Channels);

                // set_text carries the language, and carrying it sets
                // options["language"] too -- the two are one action through
                // this ABI. Whether this model tolerates that is decided by
                // RunWithLanguageAsync, which hands back the language to
                // actually use.
                if (language.Length > 0 || request.Context.Length > 0)
                {
                    task.SetText(request.Context, language);
                }
                foreach (var (name, value) in request.Options)
                {
                    if (value.Length > 0) task.SetOption(name, value);
                }

                using var output = session.Run(task);
                return new Transcribed(
                    output.Text?.Text ?? "",
                    output.Text?.Language ?? "",
                    [.. output.Segments],
                    [.. output.SpeakerTurns],
                    [.. output.Words]);
            }, cancel);

            var seconds = (double)clip.Samples.Length / Math.Max(clip.Channels, 1)
                          / Math.Max(clip.SampleRate, 1);
            var wall = started.Elapsed.TotalMilliseconds;
            log($"POST /v1/audio/transcriptions{(detailed ? "/details" : "")}  "
                + $"{request.Model}  {seconds:F2}s  {wall:F0} ms");

            var timing = new
            {
                wall_ms = Math.Round(wall, 1),
                audio_duration_ms = Math.Round(seconds * 1000.0, 1),
                rtf = seconds > 0 ? Math.Round(wall / 1000.0 / seconds, 4) : 0.0,
            };

            if (!detailed)
            {
                // text and timing and nothing else, so an existing client sees
                // exactly what it sees today.
                return Results.Json(new { text = result.Text, timing });
            }

            // Everything below appears only when the model produced it, which is
            // what upstream documents. sample_rate rides along with the arrays
            // because it is what converts their offsets into seconds.
            var details = new Dictionary<string, object?>
            {
                ["text"] = result.Text,
                ["timing"] = timing,
            };
            if (result.Language.Length > 0) details["language"] = result.Language;
            if (result.Segments.Count > 0)
            {
                details["segments"] = result.Segments.Select(segment => new
                {
                    start_sample = segment.StartSample,
                    end_sample = segment.EndSample,
                    confidence = segment.Confidence,
                    text = segment.Text,
                }).ToArray();
            }
            if (result.SpeakerTurns.Count > 0)
            {
                details["speaker_turns"] = result.SpeakerTurns.Select(turn => new
                {
                    start_sample = turn.StartSample,
                    end_sample = turn.EndSample,
                    speaker_id = turn.SpeakerId,
                    confidence = turn.Confidence,
                }).ToArray();
            }
            if (result.Words.Count > 0)
            {
                details["words"] = result.Words.Select(word => new
                {
                    word = word.Word,
                    start_sample = word.StartSample,
                    end_sample = word.EndSample,
                    confidence = word.Confidence,
                }).ToArray();
            }
            if (result.Segments.Count > 0 || result.SpeakerTurns.Count > 0 || result.Words.Count > 0)
            {
                details["sample_rate"] = clip.SampleRate;
            }
            return Results.Json(details);
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (AudioCppException error)
        {
            log($"POST /v1/audio/transcriptions  {request.Model}  failed: {error.Message}");
            return Problem(500, "engine_error", error.Message);
        }
    }

    /// <summary>
    /// Forced alignment: audio in, the words of a known transcript located
    /// within it.
    /// </summary>
    private static async Task<IResult> AlignAsync(HttpRequest http, ModelPool pool,
                                                  Action<string> log, CancellationToken cancel)
    {
        AlignmentRequest request;
        try
        {
            request = await AlignmentRequest.ReadAsync(http);
        }
        catch (Exception error) when (error is InvalidDataException or IOException
                                      or FormatException)
        {
            return Problem(400, "invalid_request_error", error.Message);
        }

        // 400 rather than the transcription route's 404, because that is what
        // upstream answers here. The inconsistency is upstream's and is
        // reproduced deliberately: a client that branches on the status code
        // has to see the same code from both servers, and picking the tidier
        // one would be a difference that only shows up in someone's error
        // handling.
        var spec = pool.Spec(request.Model);
        if (spec is null)
        {
            return Problem(400, "invalid_request_error", $"unknown model id: {request.Model}");
        }
        if (spec.Task != "align")
        {
            return Problem(400, "invalid_request_error",
                "audio alignment requires a model configured with task=align");
        }
        if (spec.Mode != "offline")
        {
            return Problem(400, "invalid_request_error",
                "audio alignment requires a model configured with mode=offline");
        }

        var started = Stopwatch.StartNew();
        var clip = request.Audio;
        try
        {
            var result = await RunWithLanguageAsync(pool, request.Model, request.Language,
                (session, language) =>
            {
                using var task = new AudioCppRequest();
                task.SetAudio(clip.Samples, clip.SampleRate, clip.Channels);

                // An aligner is the case that rules out guessing from the
                // declared options: Qwen3's does not declare "language" and
                // requires it anyway. Sending it and learning from a refusal is
                // what serves both it and a strict family like Parakeet.
                task.SetText(request.Text, language);

                using var output = session.Run(task);
                return new Transcribed(
                    output.Text?.Text ?? "",
                    output.Text?.Language ?? "",
                    [.. output.Segments],
                    [.. output.SpeakerTurns],
                    [.. output.Words]);
            }, cancel);

            if (result.Words.Count == 0)
            {
                // Not a 500: the request was well formed and the model ran. It
                // is a 502-shaped situation with no better code, and upstream
                // surfaces it as an error rather than an empty word list, which
                // a client would read as "no words in the audio".
                return Problem(500, "engine_error", "alignment model produced no word timestamps");
            }

            var seconds = (double)clip.Samples.Length / Math.Max(clip.Channels, 1)
                          / Math.Max(clip.SampleRate, 1);
            var wall = started.Elapsed.TotalMilliseconds;
            log($"POST /v1/audio/alignments  {request.Model}  {seconds:F2}s  {wall:F0} ms");

            // Seconds and sample offsets both, which is this route's shape
            // rather than an embellishment: an aligner's caller is placing
            // words on a timeline and would otherwise divide every offset by a
            // rate it has to look up in another field.
            var rate = (double)Math.Max(clip.SampleRate, 1);
            var payload = new Dictionary<string, object?>();
            if (result.Text.Length > 0)
            {
                payload["text"] = result.Text;
                // Nested under text upstream, so an empty transcript carries no
                // language either. Kept that way rather than promoted.
                if (result.Language.Length > 0) payload["language"] = result.Language;
            }
            payload["words"] = result.Words.Select(word => new
            {
                word = word.Word,
                start = word.StartSample / rate,
                end = word.EndSample / rate,
                start_sample = word.StartSample,
                end_sample = word.EndSample,
                confidence = word.Confidence,
            }).ToArray();
            payload["timing"] = new
            {
                wall_ms = Math.Round(wall, 1),
                audio_duration_ms = Math.Round(seconds * 1000.0, 1),
                rtf = seconds > 0 ? Math.Round(wall / 1000.0 / seconds, 4) : 0.0,
            };
            return Results.Json(payload);
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (AudioCppException error)
        {
            log($"POST /v1/audio/alignments  {request.Model}  failed: {error.Message}");
            return Problem(500, "engine_error", error.Message);
        }
    }

    /// <summary>
    /// Runs work against a model, deciding whether the caller's language can go
    /// with it and retrying once without it if the model says no.
    /// </summary>
    /// <remarks>
    /// The retry exists because the question cannot be answered in advance —
    /// see <see cref="ModelPool.RefusesLanguage"/>. It happens at most once per
    /// model per server lifetime, and the refusal it recovers from is raised
    /// during option validation, before the model does any work.
    /// </remarks>
    private static async Task<T> RunWithLanguageAsync<T>(
        ModelPool pool, string id, string language,
        Func<AudioCppSession, string, T> work,
        CancellationToken cancel)
    {
        if (language.Length == 0 || pool.RefusesLanguage(id))
        {
            return await pool.UseAsync(id, session => work(session, ""), cancel);
        }

        try
        {
            return await pool.UseAsync(id, session => work(session, language), cancel);
        }
        catch (AudioCppException error) when (RefusedTheLanguageOption(error))
        {
            pool.NoteLanguageRefused(id);
            return await pool.UseAsync(id, session => work(session, ""), cancel);
        }
    }

    /// <summary>
    /// Whether this failure is the engine refusing an undeclared
    /// <c>language</c> request option, rather than some other failure that
    /// happens to mention a language.
    /// </summary>
    /// <remarks>
    /// The engine's wording is "unknown &lt;model&gt; request option: &lt;key&gt;",
    /// so the key is matched where it appears and only as a whole word. Without
    /// the boundary check a refusal of "language_hint" would match, and the
    /// retry would drop the caller's language for a reason that was never
    /// about it.
    /// </remarks>
    private static bool RefusedTheLanguageOption(AudioCppException error)
    {
        const string prefix = "request option: language";
        var at = error.Message.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return false;
        var after = at + prefix.Length;
        return after >= error.Message.Length
               || !(char.IsLetterOrDigit(error.Message[after]) || error.Message[after] == '_');
    }

    /// <summary>
    /// Frees the memory a named set of models holds, without forgetting them.
    /// </summary>
    /// <remarks>
    /// Three outcomes, and the difference between the last two is upstream's
    /// contract rather than an oversight: an unknown id is reported in
    /// <c>not_found</c>, a known id that was resident is reported in
    /// <c>unloaded</c>, and a known id that was already idle appears in
    /// neither. Nothing happened to it and nothing was wrong with asking.
    /// </remarks>
    private static async Task<IResult> UnloadModelsAsync(HttpRequest http, ModelPool pool,
                                                         Action<string> log, CancellationToken cancel)
    {
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Body, cancellationToken: cancel);
        }
        catch (JsonException error)
        {
            return Problem(400, "invalid_request_error", $"malformed JSON: {error.Message}");
        }

        if (!body.TryGetProperty("model_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return Problem(400, "invalid_request_error",
                "request requires a 'model_ids' string array");
        }

        var unloaded = new List<string>();
        var notFound = new List<string>();
        foreach (var element in ids.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return Problem(400, "invalid_request_error",
                    "each element of 'model_ids' must be a string");
            }
            var id = element.GetString() ?? "";
            if (!pool.Knows(id)) notFound.Add(id);
            else if (await pool.UnloadAsync(id, cancel)) unloaded.Add(id);
        }

        log($"POST /v1/tasks/unload_models  {unloaded.Count} unloaded, {notFound.Count} unknown");
        return Results.Json(new { unloaded, not_found = notFound });
    }

    /// <summary>
    /// The framework's own request shape, for anything the task-specific routes
    /// do not cover.
    /// </summary>
    private static async Task<IResult> RunTaskAsync(HttpRequest http, ModelPool pool,
                                                    Action<string> log, CancellationToken cancel)
    {
        TaskRunRequest request;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                http.Body, cancellationToken: cancel);
            request = TaskRunRequest.Parse(body);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException
                                      or IOException or FormatException)
        {
            return Problem(400, "invalid_request_error", error.Message);
        }

        if (!pool.Knows(request.Model))
        {
            return Problem(404, "model_not_found", $"no model with id '{request.Model}'");
        }

        var started = Stopwatch.StartNew();
        try
        {
            var result = await RunWithLanguageAsync(pool, request.Model, request.Language,
                (session, language) =>
            {
                using var task = new AudioCppRequest();
                request.ApplyTo(task, language);
                using var output = session.Run(task);
                return Ran.From(output);
            }, cancel);

            var wall = started.Elapsed.TotalMilliseconds;
            log($"POST /v1/tasks/run  {request.Model}  {wall:F0} ms");
            return Results.Json(result.ToJson(wall, request.Audio?.SampleRate ?? 0));
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }
        catch (AudioCppException error)
        {
            log($"POST /v1/tasks/run  {request.Model}  failed: {error.Message}");
            return Problem(500, "engine_error", error.Message);
        }
    }

    private static async Task<IResult> LoadModelAsync(HttpRequest http, ModelPool pool,
                                                      Action<string> log, CancellationToken cancel)
    {
        if (!pool.ManagementEnabled)
        {
            return Problem(403, "forbidden", "dynamic model management is disabled");
        }

        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Body, cancellationToken: cancel);
        }
        catch (JsonException error)
        {
            return Problem(400, "invalid_request_error", $"malformed JSON: {error.Message}");
        }

        string Field(string name) =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";

        var id = Field("id");
        var path = Field("path");
        if (id.Length == 0) return Problem(400, "invalid_request_error", "load requires an 'id'");
        if (path.Length == 0) return Problem(400, "invalid_request_error", "load requires a 'path'");

        var spec = new ServerModel(
            id,
            Field("family"),
            path,
            Field("task") is { Length: > 0 } task ? task : "tts",
            Field("mode") is { Length: > 0 } mode ? mode : "offline");

        // Kept so a failed load can put back what was there. Rolling forward
        // into "forget it" would mean a reconfiguration with a typo in the path
        // deletes a registration that was working a moment ago.
        var previous = pool.Spec(id);
        try
        {
            var reconfigured = await pool.RegisterAsync(spec, cancel);
            // Loaded here rather than left for the first request, because the
            // caller asked for a load: the point of the route is to pay the
            // cost now, and reporting loaded:true over a model that has not
            // been opened would be a lie a client plans around.
            await pool.LoadAsync(id, cancel);
            log($"POST /v1/models/load  {id}{(reconfigured ? " (reconfigured)" : "")}");
            return Results.Json(new { id, loaded = true, reconfigured });
        }
        catch (AudioCppException error)
        {
            // The registration stands or falls with the load: a model that
            // cannot be opened must not be left in the list answering
            // /v1/models and failing every request sent to it. A reconfiguration
            // goes back to what it was instead of being dropped.
            if (previous is not null) await pool.RegisterAsync(previous, CancellationToken.None);
            else await pool.ForgetAsync(id, CancellationToken.None);
            log($"POST /v1/models/load  {id}  failed: {error.Message}");
            return Problem(400, "invalid_request_error", error.Message);
        }
    }

    private static async Task<IResult> UnloadModelAsync(HttpRequest http, ModelPool pool,
                                                        Action<string> log, CancellationToken cancel)
    {
        if (!pool.ManagementEnabled)
        {
            return Problem(403, "forbidden", "dynamic model management is disabled");
        }

        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Body, cancellationToken: cancel);
        }
        catch (JsonException error)
        {
            return Problem(400, "invalid_request_error", $"malformed JSON: {error.Message}");
        }

        var id = body.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        if (!pool.Knows(id)) return Problem(404, "not_found", $"unknown model id: {id}");

        await pool.UnloadAsync(id, cancel);
        log($"POST /v1/models/unload  {id}");
        return Results.Json(new { id, loaded = false });
    }

    /// <summary>
    /// The live routes, which share everything but which model runs and what
    /// comes back.
    /// </summary>
    private static async Task<IResult> LiveAsync(HttpContext context, ModelPool pool,
                                                 Action<string> log, bool speech,
                                                 CancellationToken cancel)
    {
        var id = context.Request.Query["model"].ToString();
        if (id.Length == 0)
        {
            return Problem(400, "invalid_request_error", "live requests require a 'model' parameter");
        }
        var spec = pool.Spec(id);
        if (spec is null) return Problem(404, "model_not_found", $"no model with id '{id}'");
        if (spec.Mode != "streaming")
        {
            return Problem(400, "invalid_request_error",
                "live ingest requires a model configured with mode=streaming");
        }

        try
        {
            if (speech)
            {
                await Streaming.LiveSpeechAsync(context, pool, id, pool.LiveIngest, log, cancel);
            }
            else
            {
                await Streaming.LiveTranscriptionAsync(context, pool, id, pool.LiveIngest, log, cancel);
            }
            return Results.Empty;
        }
        catch (Exception error) when (error is InvalidDataException or AudioCppException
                                      && !context.Response.HasStarted)
        {
            var invalid = error is InvalidDataException;
            log($"live {(speech ? "speech" : "transcription")}  {id}  failed: {error.Message}");
            return Problem(invalid ? 400 : 500,
                invalid ? "invalid_request_error" : "engine_error", error.Message);
        }
        catch (Exception error) when (error is InvalidDataException or AudioCppException)
        {
            // Past the first byte the status is already sent, so the only place
            // left to report is the stream. A client that sees an error event
            // instead of [DONE] knows the transcript it holds is incomplete --
            // which a silently closed connection would not tell it.
            log($"live {(speech ? "speech" : "transcription")}  {id}  failed mid-stream: {error.Message}");
            await Sse.Attach(context.Response).ErrorAsync(
                error.Message, error is InvalidDataException ? "invalid_request_error" : "engine_error",
                CancellationToken.None);
            return Results.Empty;
        }
    }

    private sealed record Transcribed(
        string Text,
        string Language,
        IReadOnlyList<SpeechSegment> Segments,
        IReadOnlyList<SpeakerTurn> SpeakerTurns,
        IReadOnlyList<WordTimestamp> Words);

    /// <summary>
    /// An error in OpenAI's shape, which is what clients of this API parse.
    /// </summary>
    private static IResult Problem(int status, string type, string message) =>
        Results.Json(new { error = new { message, type } }, statusCode: status);


    public static void Map(WebApplication app, ModelPool pool, Action<string> log)
    {
        app.MapGet("/health", () =>
        {
            log("GET /health");
            return Results.Json(new
            {
                status = "ok",
                models = pool.Models.Count,
            });
        });

        app.MapPost("/v1/audio/speech", async (HttpContext http) =>
        {
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
            }
            catch (JsonException error)
            {
                return Problem(400, "invalid_request_error", $"malformed JSON: {error.Message}");
            }

            SpeechRequest request;
            try
            {
                request = SpeechRequest.Parse(body);
            }
            catch (Exception error) when (error is InvalidDataException or FormatException
                                          or IOException)
            {
                // A bad reference is the client's mistake, not the server's, and
                // saying which is the difference between a 400 and a 500.
                return Problem(400, "invalid_request_error", error.Message);
            }

            if (request.Input.Length == 0)
            {
                return Problem(400, "invalid_request_error", "'input' is required");
            }
            if (!pool.Knows(request.Model))
            {
                return Problem(404, "model_not_found", $"no model with id '{request.Model}'");
            }
            if (request.StreamFormat.Length > 0)
            {
                if (request.StreamFormat is not ("sse" or "audio"))
                {
                    return Problem(400, "invalid_request_error",
                        "streaming speech stream_format must be sse or audio");
                }
                if (pool.Spec(request.Model)?.Mode != "streaming")
                {
                    // The model decides this, not the route: an offline session
                    // has no events to emit, so a stream over one would be a
                    // single delta pretending to be a stream.
                    return Problem(400, "invalid_request_error",
                        "streaming speech requires a model configured with mode=streaming");
                }
                try
                {
                    await Streaming.SpeechAsync(http, pool, request, log, http.RequestAborted);
                    return Results.Empty;
                }
                catch (AudioCppException error) when (!http.Response.HasStarted)
                {
                    log($"POST /v1/audio/speech  {request.Model}  stream failed: {error.Message}");
                    return Problem(500, "engine_error", error.Message);
                }
            }

            var started = Stopwatch.StartNew();
            try
            {
                var audio = await pool.UseAsync(request.Model, session =>
                {
                    using var task = new AudioCppRequest();
                    request.ApplyTo(task);

                    using var result = session.Run(task);
                    return result.Audio is { } output
                        ? (output.Samples, output.SampleRate, output.Channels)
                        : default((float[], int, int)?);
                }, http.RequestAborted);

                if (audio is not { } clip)
                {
                    return Problem(500, "no_audio", "the model produced no audio");
                }

                log($"POST /v1/audio/speech  {request.Model}  "
                    + $"{(double)clip.Item1.Length / Math.Max(clip.Item3, 1) / Math.Max(clip.Item2, 1):F2}s  "
                    + $"{started.ElapsedMilliseconds} ms");

                var wav = Wav.ToBytes(clip.Item1, clip.Item2, clip.Item3);
                return request.ResponseFormat == "json"
                    ? Results.Json(new
                    {
                        audio = Convert.ToBase64String(wav),
                        format = "wav",
                        sample_rate = clip.Item2,
                        channels = clip.Item3,
                    })
                    : Results.Bytes(wav, "audio/wav");
            }
            catch (OperationCanceledException)
            {
                // The client hung up. Nothing to send and nothing broken.
                return Results.Empty;
            }
            catch (AudioCppException error)
            {
                log($"POST /v1/audio/speech  {request.Model}  failed: {error.Message}");
                return Problem(500, "engine_error", error.Message);
            }
        });

        // The plain route returns text and timing. /details returns what the
        // plain one throws away -- a model that aligned every word or separated
        // speakers has that work discarded on the way out otherwise.
        // HttpRequest and a token rather than HttpContext, deliberately. MapPost
        // has both a Delegate and a RequestDelegate overload, and a lambda
        // taking HttpContext and returning Task<IResult> converts to
        // RequestDelegate -- whose return value is discarded. The route then
        // answers 200 with no body and no content type, having run correctly.
        // That is what it did: the handler logged a successful transcription
        // and the client got nothing.
        app.MapPost("/v1/audio/transcriptions",
            (HttpContext context, CancellationToken cancel) =>
                TranscribeAsync(context, pool, log, detailed: false, cancel));
        app.MapPost("/v1/audio/transcriptions/details",
            (HttpContext context, CancellationToken cancel) =>
                TranscribeAsync(context, pool, log, detailed: true, cancel));

        app.MapPost("/v1/audio/alignments",
            (HttpRequest request, CancellationToken cancel) =>
                AlignAsync(request, pool, log, cancel));

        // A GET with the model in the query string, because a client calls it
        // to populate a picker before it has anything to post.
        app.MapGet("/v1/audio/voices", (HttpRequest request) =>
        {
            var id = request.Query["model"].ToString();
            log($"GET /v1/audio/voices  {(id.Length > 0 ? id : "(unspecified)")}");
            return Results.Json(new { voices = pool.VoicesFor(id) });
        });

        // Live ingest: the request body is the audio, arriving chunked, and
        // the response can begin before it ends. A separate path rather than a
        // flag, because the transport differs -- every existing client of the
        // routes above is untouched by it.
        app.MapPost("/v1/audio/transcriptions/live",
            (HttpContext context, CancellationToken cancel) =>
                LiveAsync(context, pool, log, speech: false, cancel));
        app.MapPost("/v1/audio/speech/live",
            (HttpContext context, CancellationToken cancel) =>
                LiveAsync(context, pool, log, speech: true, cancel));

        // Residency, which a client managing several models has to be able to
        // see and change: loading is slow and unloading frees the device.
        app.MapPost("/v1/tasks/unload_models",
            (HttpRequest request, CancellationToken cancel) =>
                UnloadModelsAsync(request, pool, log, cancel));
        app.MapPost("/v1/tasks/unload_all_models", async (CancellationToken cancel) =>
        {
            var unloaded = await pool.UnloadAllAsync(cancel);
            log($"POST /v1/tasks/unload_all_models  {unloaded.Count} unloaded");
            return Results.Json(new { unloaded });
        });

        app.MapPost("/v1/tasks/run",
            (HttpRequest request, CancellationToken cancel) =>
                RunTaskAsync(request, pool, log, cancel));

        app.MapPost("/v1/models/load",
            (HttpRequest request, CancellationToken cancel) =>
                LoadModelAsync(request, pool, log, cancel));
        app.MapPost("/v1/models/unload",
            (HttpRequest request, CancellationToken cancel) =>
                UnloadModelAsync(request, pool, log, cancel));

        // OpenAI's model list shape: clients iterate data[] and read id.
        app.MapGet("/v1/models", () =>
        {
            log("GET /v1/models");
            return Results.Json(new
            {
                @object = "list",
                data = pool.Models.Select(m => new
                {
                    id = m.Id,
                    @object = "model",
                    owned_by = "audio.cpp",
                    task = m.Task,
                    mode = m.Mode,
                }).ToArray(),
            });
        });
    }
}
