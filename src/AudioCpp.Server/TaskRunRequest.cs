using System.Text.Json;
using AudioCpp;

namespace AudioCpp.Server;

/// <summary>
/// A request in the framework's own JSON shape, as <c>POST /v1/tasks/run</c>
/// takes it.
/// </summary>
/// <remarks>
/// The fields are audiocpp_cli's request-sequence format rather than anything
/// designed for HTTP, which is the point of the route: a request that runs
/// under the CLI runs here unchanged. Most of them are conveniences that
/// resolve to a request option — <c>lyrics</c>, <c>speaker</c>,
/// <c>repaint_start</c> and the rest — so they are kept as a table rather than
/// eighteen near-identical branches. A missing entry is a field that silently
/// does nothing, which is why the table is the whole list and not the ones that
/// seemed worth having.
/// </remarks>
internal sealed record TaskRunRequest
{
    public string Model { get; private init; } = "";
    public string Text { get; private init; } = "";
    public string Language { get; private init; } = "";
    public (float[] Samples, int SampleRate, int Channels)? Audio { get; private init; }
    public string VoiceId { get; private init; } = "";
    public (float[] Samples, int SampleRate, int Channels)? VoiceReference { get; private init; }
    public string StyleLanguage { get; private init; } = "";
    public string Emotion { get; private init; } = "";
    public float? SpeakingRate { get; private init; }
    public float? PitchShift { get; private init; }
    public float? EnergyScale { get; private init; }
    public Dictionary<string, string> StyleTags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Request fields that are a spelling for an option, and the option each
    /// one sets.
    /// </summary>
    /// <remarks>
    /// Two of them differ in name from the option they set —
    /// <c>repaint_start</c> writes <c>repainting_start</c> — which is exactly
    /// the sort of thing a hand-written branch gets wrong once and nobody
    /// notices, because the request still succeeds and the repaint simply does
    /// not happen.
    /// </remarks>
    private static readonly (string Field, string Option)[] OptionAliases =
    [
        ("task_route", "route"),
        ("route", "route"),
        ("source_audio", "source_audio"),
        ("target_voice", "target_voice"),
        ("prosody_ref", "prosody_ref"),
        ("style_ref", "style_ref"),
        ("target_text", "target_text"),
        ("style_ref_text", "style_ref_text"),
        ("lyrics", "lyrics"),
        ("track_name", "track_name"),
        ("speaker", "speaker"),
        ("duration_seconds", "duration_seconds"),
        ("repaint_start", "repainting_start"),
        ("repaint_end", "repainting_end"),
        ("repaint_mode", "repaint_mode"),
        ("repaint_strength", "repaint_strength"),
        ("reference_text", "reference_text"),
        ("instruct", "instruct"),
        ("seed", "seed"),
        ("max_tokens", "max_tokens"),
    ];

    public static TaskRunRequest Parse(JsonElement body)
    {
        // The request may be nested under "request" or be the body itself.
        // Upstream accepts both and so does this: a client that learned the
        // flat form from the CLI should not have to rediscover the nested one.
        var fields = body.TryGetProperty("request", out var nested)
                     && nested.ValueKind == JsonValueKind.Object
            ? nested
            : body;

        var request = new TaskRunRequest
        {
            Model = String(body, "model"),
            Text = String(fields, "text"),
            Language = String(fields, "language"),
            VoiceId = String(fields, "voice_id"),
            StyleLanguage = String(fields, "style_language"),
            Emotion = String(fields, "emotion"),
            SpeakingRate = Number(fields, "speaking_rate"),
            PitchShift = Number(fields, "pitch_shift"),
            EnergyScale = Number(fields, "energy_scale"),
            Audio = ReadClip(fields, "audio"),
            VoiceReference = ReadClip(fields, "voice_ref"),
        };

        if (request.Model.Length == 0)
        {
            throw new InvalidDataException("a task run requires a 'model' id");
        }

        if (fields.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in options.EnumerateObject())
            {
                request.Options[option.Name] = Scalar(option.Value);
            }
        }
        if (fields.TryGetProperty("style_tags", out var tags)
            && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tags.EnumerateObject())
            {
                request.StyleTags[tag.Name] = Scalar(tag.Value);
            }
        }

        // After the explicit options map, so a caller who sets both wins with
        // the more specific spelling — the CLI's order, and the one that makes
        // "options" the escape hatch rather than a thing the aliases override.
        foreach (var (field, option) in OptionAliases)
        {
            if (fields.TryGetProperty(field, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                request.Options[option] = Scalar(value);
            }
        }

        // The transcript language also travels as an option under the CLI. The
        // route decides whether this model tolerates that; the language itself
        // reaches the transcript either way — see the route's language
        // handling.
        if (request.Language.Length > 0) request.Options.Remove("language");

        return request;
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static float? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle() : null;

    /// <summary>
    /// GetRawText for numbers, so a value reaches the engine as it was written.
    /// </summary>
    /// <remarks>
    /// A seed is the case that matters: round-tripping it through a double and
    /// back loses the low bits of a large one, and the request still succeeds —
    /// with a different seed than the caller asked for, which is unreproducible
    /// in the one place reproducibility is the whole point.
    /// </remarks>
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    /// <remarks>
    /// A path here is read on the server, from wherever the server can reach —
    /// upstream's behaviour for these fields, and a real exposure if the port
    /// is opened past loopback. The default host is loopback for that reason.
    /// </remarks>
    private static (float[], int, int)? ReadClip(JsonElement element, string name)
    {
        var path = String(element, name);
        return path.Length > 0 ? Wav.Read(path) : null;
    }

    /// <summary>Applies everything parsed here to a live request.</summary>
    /// <param name="optionLanguage">
    /// The language that may also travel as the "language" request option —
    /// empty for a model that has been seen to refuse it. The transcript
    /// language comes from <see cref="Language"/> either way, since only the
    /// option was ever the thing a strict family objected to.
    /// </param>
    public void ApplyTo(AudioCppRequest task, string optionLanguage)
    {
        if (Audio is { } audio) task.SetAudio(audio.Samples, audio.SampleRate, audio.Channels);
        if (VoiceReference is { } voice)
        {
            task.SetVoiceAudio(voice.Samples, voice.SampleRate, voice.Channels);
        }
        if (VoiceId.Length > 0) task.SetVoiceId(VoiceId);
        if (Text.Length > 0 || optionLanguage.Length > 0) task.SetText(Text, optionLanguage);
        if (Language.Length > 0) task.SetTextLanguage(Language);

        if (StyleLanguage.Length > 0) task.SetStyleLanguage(StyleLanguage);
        if (Emotion.Length > 0) task.SetEmotion(Emotion);
        if (SpeakingRate is { } rate) task.SetSpeakingRate(rate);
        if (PitchShift is { } pitch) task.SetPitchShift(pitch);
        if (EnergyScale is { } energy) task.SetEnergyScale(energy);
        foreach (var (key, value) in StyleTags) task.SetStyleTag(key, value);

        foreach (var (key, value) in Options)
        {
            if (value.Length > 0) task.SetOption(key, value);
        }
    }
}
