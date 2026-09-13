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
    private static async Task<IResult> TranscribeAsync(HttpRequest http, ModelPool pool,
                                                       Action<string> log, bool detailed,
                                                       CancellationToken cancel)
    {
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
            // On /details this is upstream's documented 400: SSE carries
            // transcript deltas and has nowhere to put the detail arrays. On the
            // plain route it is simply not implemented yet, and saying so beats
            // returning one whole transcript to a client waiting for events.
            return Problem(400, detailed ? "invalid_request_error" : "not_implemented",
                detailed
                    ? "stream is not supported on /details; use /v1/audio/transcriptions"
                    : "streamed transcription is not implemented yet; omit 'stream'");
        }

        var started = Stopwatch.StartNew();
        var clip = request.Audio;
        try
        {
            var result = await RunWithLanguageAsync(pool, request.Model, request.Language,
                (model, session, language) =>
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
                (model, session, language) =>
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
        Func<AudioCppModel, AudioCppSession, string, T> work,
        CancellationToken cancel)
    {
        if (language.Length == 0 || pool.RefusesLanguage(id))
        {
            return await pool.UseModelAsync(id, (model, session) => work(model, session, ""), cancel);
        }

        try
        {
            return await pool.UseModelAsync(
                id, (model, session) => work(model, session, language), cancel);
        }
        catch (AudioCppException error) when (RefusedTheLanguageOption(error))
        {
            pool.NoteLanguageRefused(id);
            return await pool.UseModelAsync(id, (model, session) => work(model, session, ""), cancel);
        }
    }

    /// <summary>
    /// Matched on both halves, so an unrelated failure that happens to mention
    /// a language is not mistaken for this one.
    /// </summary>
    private static bool RefusedTheLanguageOption(AudioCppException error) =>
        error.Message.Contains("request option", StringComparison.Ordinal)
        && error.Message.Contains("language", StringComparison.Ordinal);

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
                // Honest 400 rather than a silent non-streaming response: a
                // client asking for SSE and getting one WAV would look like the
                // stream ended immediately.
                return Problem(400, "not_implemented",
                    "streaming speech is not implemented yet; omit 'stream_format'");
            }

            var started = Stopwatch.StartNew();
            try
            {
                var audio = await pool.UseAsync(request.Model, session =>
                {
                    using var task = new AudioCppRequest();
                    task.SetText(request.Input,
                        request.Language.Length > 0 ? request.Language : null);
                    if (request.Voice.Length > 0) task.SetVoiceId(request.Voice);
                    if (request.VoiceReference is { } reference)
                    {
                        task.SetVoiceAudio(reference.Samples, reference.SampleRate,
                                           reference.Channels);
                    }
                    foreach (var (name, value) in request.Options)
                    {
                        if (value.Length > 0) task.SetOption(name, value);
                    }

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
            (HttpRequest request, CancellationToken cancel) =>
                TranscribeAsync(request, pool, log, detailed: false, cancel));
        app.MapPost("/v1/audio/transcriptions/details",
            (HttpRequest request, CancellationToken cancel) =>
                TranscribeAsync(request, pool, log, detailed: true, cancel));

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
