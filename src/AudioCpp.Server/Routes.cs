using Microsoft.AspNetCore.Builder;
using System.Diagnostics;
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
    /// <summary>
    /// An error in OpenAI's shape, which is what clients of this API parse.
    /// </summary>
    private static IResult Problem(int status, string type, string message) =>
        Results.Json(new { error = new { message, type } }, statusCode: status);


    public static void Map(WebApplication app, ModelPool pool, AudioCppServer server,
                           Action<string> log)
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
