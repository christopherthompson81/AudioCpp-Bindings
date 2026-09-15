using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// POST /v1/audio/alignments and GET /v1/audio/voices.
/// </summary>
/// <remarks>
/// The assertion that matters for alignment is not that words come back with
/// times on them — it is that the times are the ones the model reported. A
/// route that divided by the wrong rate, or that reported the upload's rate
/// while the model resampled, would return a monotonic, plausible, wrong
/// timeline. So the check is that seconds and samples agree with each other
/// and that the transcript's own words come back in order, inside the clip.
/// </remarks>
internal static class Alignment
{
    public static async Task<int> RunAsync(string model, string family, string backend,
                                           string audio, string transcript)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var config = new ServerConfig
        {
            Port = 19600 + Random.Shared.Next(300),
            Backend = backend,
            Models = [new ServerModel("align", family, model, "align")],
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

        async Task<HttpResponseMessage> PostAsync(
            string? file = "same", string? modelId = "align", string? text = "same",
            string filename = "", string? language = "en")
        {
            using var form = new MultipartFormDataContent();
            if (file is not null)
            {
                var bytes = file == "same"
                    ? await File.ReadAllBytesAsync(audio)
                    : Encoding.UTF8.GetBytes(file);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(part, "file",
                         filename.Length > 0 ? filename : Path.GetFileName(audio));
            }
            if (modelId is not null) form.Add(new StringContent(modelId), "model");
            if (text is not null)
            {
                form.Add(new StringContent(text == "same" ? transcript : text), "text");
            }
            if (language is not null) form.Add(new StringContent(language), "language");
            return await http.PostAsync("/v1/audio/alignments", form);
        }

        // 1. A real alignment.
        var response = await PostAsync();
        var body = await response.Content.ReadAsStringAsync();
        Check("alignment is 200", response.StatusCode == HttpStatusCode.OK,
              body.Length > 200 ? body[..200] : body);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            await server.StopAsync();
            return failures;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var words = root.GetProperty("words").EnumerateArray().ToArray();
        Check("words come back", words.Length > 0, $"{words.Length} words");

        // The clip's own length, so "inside the recording" is measured against
        // the audio rather than against a number typed here.
        var clip = AudioCpp.Wav.Read(audio);
        var duration = (double)clip.Samples.Length / Math.Max(clip.Channels, 1) / clip.SampleRate;

        var agree = true;
        var ordered = true;
        var inside = true;
        var previousEnd = -1.0;
        foreach (var word in words)
        {
            var start = word.GetProperty("start").GetDouble();
            var end = word.GetProperty("end").GetDouble();
            var startSample = word.GetProperty("start_sample").GetInt64();
            var endSample = word.GetProperty("end_sample").GetInt64();

            // Seconds are the samples divided by the rate, and nothing else.
            // A tenth of a millisecond of slack is for the JSON round trip, not
            // for a different rate: at 16 kHz one sample is 62 microseconds, so
            // an off-by-one-rate error is orders of magnitude larger than this.
            if (Math.Abs(start - startSample / (double)clip.SampleRate) > 1e-4) agree = false;
            if (Math.Abs(end - endSample / (double)clip.SampleRate) > 1e-4) agree = false;

            if (start < previousEnd - 1e-6 || end < start) ordered = false;
            if (start < -1e-6 || end > duration + 0.05) inside = false;
            previousEnd = end;
        }
        Check("seconds are the sample offsets over the rate", agree);
        Check("words run forwards and do not overlap", ordered);
        Check("every word lands inside the recording", inside, $"clip is {duration:F2}s");

        // The words asked for are the words returned. Alignment does not
        // transcribe, so a route that quietly ran ASR instead would still
        // produce plausible output — this is what separates the two.
        var expected = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':').ToLowerInvariant())
            .Where(w => w.Length > 0).ToArray();
        var got = words.Select(w => w.GetProperty("word").GetString() ?? "")
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':').ToLowerInvariant())
            .Where(w => w.Length > 0).ToArray();
        Check("the aligned words are the transcript's own",
              expected.Length == got.Length && expected.SequenceEqual(got),
              $"asked {expected.Length}, got {got.Length}");

        Check("timing rides along", root.TryGetProperty("timing", out var timing)
                                    && timing.TryGetProperty("wall_ms", out _));

        // 2. Errors.
        var noFile = await PostAsync(file: null);
        Check("no file is a 400", noFile.StatusCode == HttpStatusCode.BadRequest);
        var noText = await PostAsync(text: null);
        Check("no text is a 400", noText.StatusCode == HttpStatusCode.BadRequest);
        var noModel = await PostAsync(modelId: null);
        Check("no model is a 400", noModel.StatusCode == HttpStatusCode.BadRequest);

        // Upstream answers 400 here, not the 404 the transcription routes give
        // for the same mistake. Pinned because the inconsistency is the
        // contract: a client branching on the code must see what upstream sends.
        var unknown = await PostAsync(modelId: "nope");
        Check("an unknown model is a 400, as upstream answers here",
              unknown.StatusCode == HttpStatusCode.BadRequest,
              $"{(int)unknown.StatusCode}");

        var notWav = await PostAsync(filename: "speech.mp3");
        Check("a non-wav upload is refused by name", notWav.StatusCode == HttpStatusCode.BadRequest);

        // This aligner refuses without one, which is the case the language
        // guard has to get right: it declares no "language" request option and
        // needs the transcript language anyway. The two are set separately now,
        // so the language reaches it whether or not the option is welcome --
        // while they were one call, serving this family and a strict one like
        // Parakeet were mutually exclusive.
        var noLanguage = await PostAsync(language: null);
        var noLanguageBody = await noLanguage.Content.ReadAsStringAsync();
        Check("a model that needs a language says so rather than aligning wrongly",
              noLanguage.StatusCode != HttpStatusCode.OK
              && noLanguageBody.Contains("language", StringComparison.OrdinalIgnoreCase),
              $"{(int)noLanguage.StatusCode}");

        var asJson = await http.PostAsync("/v1/audio/alignments",
            new StringContent("""{"model":"align"}""", Encoding.UTF8, "application/json"));
        var jsonBody = await asJson.Content.ReadAsStringAsync();
        Check("a JSON body is a 400 that says multipart",
              asJson.StatusCode == HttpStatusCode.BadRequest
              && jsonBody.Contains("multipart", StringComparison.Ordinal));

        // 3. Voices. One model is configured, so an omitted id resolves to it.
        var voices = await http.GetAsync("/v1/audio/voices?model=align");
        var voicesBody = await voices.Content.ReadAsStringAsync();
        Check("voices is 200", voices.StatusCode == HttpStatusCode.OK, voicesBody);
        using (var voicesDocument = JsonDocument.Parse(voicesBody))
        {
            Check("voices is an array under 'voices'",
                  voicesDocument.RootElement.TryGetProperty("voices", out var list)
                  && list.ValueKind == JsonValueKind.Array);
        }

        var unknownVoices = await http.GetAsync("/v1/audio/voices?model=nope");
        var unknownBody = await unknownVoices.Content.ReadAsStringAsync();
        Check("an unknown model is an empty list, not an error",
              unknownVoices.StatusCode == HttpStatusCode.OK
              && unknownBody.Contains("\"voices\":[]", StringComparison.Ordinal),
              unknownBody);

        await server.StopAsync();
        Console.WriteLine(failures == 0 ? "alignment OK" : $"alignment: {failures} failure(s)");
        return failures;
    }
}
