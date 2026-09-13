using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// A forced-alignment request: uploaded audio plus the transcript to align
/// against it.
/// </summary>
/// <remarks>
/// Multipart only, which is upstream's decision rather than an omission here.
/// The route exists for the case where the server cannot see the client's
/// audio — a remote server, or one in a container — so accepting a path would
/// defeat the purpose of having it separate from the transcription routes.
/// A JSON body is refused with a 400 that says so.
/// </remarks>
internal sealed record AlignmentRequest
{
    public string Model { get; private init; } = "";
    public string Text { get; private init; } = "";
    public string Language { get; private init; } = "";
    public (float[] Samples, int SampleRate, int Channels) Audio { get; private init; }

    public static async Task<AlignmentRequest> ReadAsync(HttpRequest http)
    {
        var contentType = http.ContentType ?? "";
        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "audio alignment requests must use multipart/form-data");
        }

        var form = await http.ReadFormAsync();
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            throw new InvalidDataException(
                "multipart alignment request requires a non-empty 'file' field");
        }

        var model = form["model"].ToString();
        if (model.Length == 0)
        {
            throw new InvalidDataException(
                "multipart alignment request requires a 'model' field");
        }

        var text = form["text"].ToString();
        if (text.Length == 0)
        {
            throw new InvalidDataException(
                "multipart alignment request requires a non-empty 'text' field");
        }

        // Upstream checks the upload's filename, not its bytes, and refuses
        // anything but .wav. Matched here so a client that gets a 400 from one
        // server gets it from the other: a caller who uploads an mp3 should be
        // told the format is unsupported, not handed a decoder error about a
        // missing RIFF header.
        var name = file.FileName ?? "";
        if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "only WAV audio uploads are currently supported for alignment");
        }

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;

        return new AlignmentRequest
        {
            Model = model,
            Text = text,
            Language = form["language"].ToString(),
            Audio = Wav.Read(stream),
        };
    }
}
