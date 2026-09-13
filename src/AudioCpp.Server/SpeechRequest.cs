using System.Text.Json;

namespace AudioCpp.Server;

/// <summary>
/// A `POST /v1/audio/speech` body, read the way the reference server reads it.
/// </summary>
/// <remarks>
/// Parsed by hand rather than deserialised into a record, because several
/// fields are deliberately polymorphic and a strict binder would reject bodies
/// the reference server accepts: `seed` may be a number or a string, and
/// `voice_ref` may be a bare path or an object with a type. Clients written
/// against audio.cpp send both shapes.
/// </remarks>
internal sealed record SpeechRequest
{
    /// <summary>Options passed straight through to the request.</summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    public string Model { get; private init; } = "";
    public string Input { get; private init; } = "";
    public string Language { get; private init; } = "";
    public string Voice { get; private init; } = "";
    public string ResponseFormat { get; private init; } = "wav";
    public string StreamFormat { get; private init; } = "";

    /// <summary>Reference audio for cloning, already decoded to PCM.</summary>
    public (float[] Samples, int SampleRate, int Channels)? VoiceReference { get; private init; }



    /// <summary>
    /// Scalar fields the reference server copies into options under the same
    /// name. Listed rather than inferred so an unknown field is ignored the way
    /// upstream ignores it, instead of becoming an option no model reads.
    /// </summary>
    private static readonly string[] ScalarOptions =
    [
        "seed", "temperature", "top_k", "top_p", "max_tokens", "max_steps",
        "repetition_penalty", "guidance_scale", "num_inference_steps",
    ];

    public static SpeechRequest Parse(JsonElement body)
    {
        string Text(string name, string fallback = "") =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        var request = new SpeechRequest
        {
            Model = Text("model"),
            Input = Text("input"),
            Language = Text("language"),
            Voice = Text("voice"),
            ResponseFormat = Text("response_format", "wav"),
            StreamFormat = Text("stream_format"),
            VoiceReference = ReadVoiceReference(body),
        };

        if (body.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in options.EnumerateObject())
            {
                request.Options[option.Name] = Scalar(option.Value);
            }
        }

        foreach (var name in ScalarOptions)
        {
            if (body.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null)
            {
                request.Options[name] = Scalar(value);
            }
        }

        // The body field is plural and the option is singular. Upstream's server
        // does the same rename, and a client sending "instructions" is what the
        // web UI sends.
        if (body.TryGetProperty("instructions", out var instructions)
            && instructions.ValueKind == JsonValueKind.String)
        {
            request.Options["instruction"] = instructions.GetString() ?? "";
        }

        if (body.TryGetProperty("reference_text", out var referenceText)
            && referenceText.ValueKind == JsonValueKind.String)
        {
            request.Options["reference_text"] = referenceText.GetString() ?? "";
        }

        return request;
    }

    /// <summary>
    /// A value as the engine wants it: a string.
    /// </summary>
    /// <remarks>
    /// A JSON number is rendered rather than round-tripped through double,
    /// which matters for seed: a full uint64 does not survive a double, which
    /// is why the reference server documents passing large seeds as strings.
    /// </remarks>
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };

    /// <summary>
    /// Decode the reference audio a request names.
    /// </summary>
    /// <remarks>
    /// A path here is read on the server, from wherever the server can reach —
    /// the same as the reference server, whose documented `voice_ref` is "a
    /// plain path string (server-side file)". That is a real exposure if the
    /// server is bound to anything but loopback: a client can make it open any
    /// file it has permission to read, and a WAV that parses comes back shaped
    /// as someone's voice.
    ///
    /// The default host is 127.0.0.1 for that reason. Binding wider is a
    /// deliberate act, and a config that does it should restrict references to
    /// a directory — which is what upstream's `voice_dir` is for, and is worth
    /// having before this is exposed to a network.
    /// </remarks>
    private static (float[], int, int)? ReadVoiceReference(JsonElement body)
    {
        if (!body.TryGetProperty("voice_ref", out var reference)) return null;

        if (reference.ValueKind == JsonValueKind.String)
        {
            var path = reference.GetString();
            return path is { Length: > 0 } ? Wav.Read(path) : null;
        }

        if (reference.ValueKind != JsonValueKind.Object) return null;

        var type = reference.TryGetProperty("type", out var kind) ? kind.GetString() : "path";
        if (type == "path")
        {
            var path = reference.TryGetProperty("path", out var p) ? p.GetString() : null;
            return path is { Length: > 0 } ? Wav.Read(path) : null;
        }

        if (type != "base64") return null;

        var data = reference.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        // A data: URI is accepted as well as bare base64, because browsers
        // produce the former and clients forward it unchanged.
        var comma = data.IndexOf(',');
        if (data.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
        {
            data = data[(comma + 1)..];
        }

        var bytes = Convert.FromBase64String(data);
        if (bytes.Length > ServerLimits.MaxInlineReferenceBytes)
        {
            throw new InvalidDataException(
                $"inline voice_ref is {bytes.Length} bytes, over the "
                + $"{ServerLimits.MaxInlineReferenceBytes} byte limit; use a path instead");
        }

        using var stream = new MemoryStream(bytes);
        return Wav.Read(stream);
    }
    /// <summary>Applies everything parsed here to a live request.</summary>
    /// <remarks>
    /// Shared by the buffered and streaming paths so the two cannot drift. They
    /// differ in how the audio comes back, not in what was asked for, and a
    /// second copy of this is how a voice reference ends up honoured by one and
    /// ignored by the other.
    /// </remarks>
    public void ApplyTo(AudioCpp.AudioCppRequest task)
    {
        task.SetText(Input, Language.Length > 0 ? Language : null);
        if (Voice.Length > 0) task.SetVoiceId(Voice);
        if (VoiceReference is { } reference)
        {
            task.SetVoiceAudio(reference.Samples, reference.SampleRate, reference.Channels);
        }
        foreach (var (name, value) in Options)
        {
            if (value.Length > 0) task.SetOption(name, value);
        }
    }

}
