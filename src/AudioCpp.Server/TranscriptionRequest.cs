using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// A transcription request, from JSON or from a multipart upload.
/// </summary>
/// <remarks>
/// Both forms exist because real clients use both: the JSON form names a file
/// the server can already reach, and the multipart form is OpenAI's Whisper
/// convention, which is what tools like Open WebUI send. Upstream routes on the
/// Content-Type header and so does this.
/// </remarks>
internal sealed record TranscriptionRequest
{
    public string Model { get; private init; } = "";
    public string Language { get; private init; } = "";

    /// <summary>A context prompt, not the audio's transcript.</summary>
    public string Context { get; private init; } = "";

    public bool Stream { get; private init; }

    /// <summary>Passed through to the engine, for whatever the family declares.</summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public (float[] Samples, int SampleRate, int Channels) Audio { get; private init; }

    public static async Task<TranscriptionRequest> ReadAsync(HttpRequest http)
    {
        var contentType = http.ContentType ?? "";
        return contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            ? await FromFormAsync(http)
            : await FromJsonAsync(http);
    }

    private static async Task<TranscriptionRequest> FromFormAsync(HttpRequest http)
    {
        var form = await http.ReadFormAsync();
        var file = form.Files["file"]
                   ?? throw new InvalidDataException("multipart transcription requires a 'file' part");

        // Decoded in memory rather than staged on disk, as upstream documents:
        // a temporary file would outlive the request if anything threw between
        // writing and deleting it.
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;

        var request = new TranscriptionRequest
        {
            Model = form["model"].ToString(),
            Language = form["language"].ToString(),
            Context = form["text"].ToString(),
            Stream = IsTrue(form["stream"].ToString()),
            Audio = Wav.Read(stream),
        };

        // Engine options ride in one JSON part rather than as loose form
        // fields. Forwarding every unrecognised field instead would look
        // tidier and would break real Whisper clients, which send
        // response_format and temperature and timestamp_granularities[] as a
        // matter of course -- the engine rejects options it does not declare,
        // so those would turn a valid request into a failed one.
        ReadOptions(form["options"].ToString(), request.Options);
        return request;
    }

    /// <summary>OpenAI clients send booleans as strings about as often as not.</summary>
    private static bool IsTrue(string value) => value is "true" or "1";

    private static void ReadOptions(string json, Dictionary<string, string> into)
    {
        if (json.Length == 0) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        ReadOptions(document.RootElement, into);
    }

    private static void ReadOptions(JsonElement options, Dictionary<string, string> into)
    {
        foreach (var option in options.EnumerateObject())
        {
            // GetRawText rather than ToString for numbers: a value that arrived
            // as 0.30000000000000004 must reach the engine as it was written.
            into[option.Name] = option.Value.ValueKind == JsonValueKind.String
                ? option.Value.GetString() ?? ""
                : option.Value.GetRawText();
        }
    }

    /// <remarks>
    /// The path named here is read on the server, from wherever the server can
    /// reach — which is upstream's behaviour for this field and a real exposure
    /// if the port is ever opened past loopback. The default host is loopback
    /// for that reason.
    /// </remarks>
    private static async Task<TranscriptionRequest> FromJsonAsync(HttpRequest http)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Body);

        string Text(string name) =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";

        // audio, then audio_path, then file: upstream's fallback order, and
        // clients written against any of the three work unchanged.
        var path = Text("audio");
        if (path.Length == 0) path = Text("audio_path");
        if (path.Length == 0) path = Text("file");
        if (path.Length == 0)
        {
            throw new InvalidDataException(
                "transcription request requires audio, audio_path, or file path");
        }

        var request = new TranscriptionRequest
        {
            Model = Text("model"),
            Language = Text("language"),
            Context = Text("text"),
            Stream = body.TryGetProperty("stream", out var stream)
                     && (stream.ValueKind == JsonValueKind.True
                         || (stream.ValueKind == JsonValueKind.String
                             && IsTrue(stream.GetString() ?? ""))),
            Audio = Wav.Read(path),
        };

        if (body.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            ReadOptions(options, request.Options);
        }

        return request;
    }
}
