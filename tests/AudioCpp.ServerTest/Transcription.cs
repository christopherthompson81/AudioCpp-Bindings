using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// The transcription routes, in both request forms.
/// </summary>
/// <remarks>
/// The interesting assertion is not that a transcript comes back — it is that
/// the JSON form and the multipart form produce the same one, and that /details
/// carries what the plain route drops. A server that decoded the upload
/// slightly differently would return a plausible transcript that quietly
/// disagreed with the other route.
/// </remarks>
internal static class Transcription
{
    public static async Task<int> RunAsync(string model, string family, string backend, string audio)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var config = new ServerConfig
        {
            Port = 19200 + Random.Shared.Next(300),
            Backend = backend,
            Models = [new ServerModel("asr", family, model, "asr")],
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
            Timeout = TimeSpan.FromMinutes(3),
        };

        async Task<HttpResponseMessage> PostJsonAsync(string route, object body) =>
            await http.PostAsync(route,
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        async Task<HttpResponseMessage> PostFormAsync(string route, bool stream = false)
        {
            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(audio);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", Path.GetFileName(audio));
            form.Add(new StringContent("asr"), "model");
            if (stream) form.Add(new StringContent("true"), "stream");
            return await http.PostAsync(route, form);
        }

        // 1. The JSON form, naming a path the server can reach.
        var json = await PostJsonAsync("/v1/audio/transcriptions", new { model = "asr", audio });
        var jsonBody = await json.Content.ReadAsStringAsync();
        Check("JSON transcription is 200", json.StatusCode == HttpStatusCode.OK, jsonBody);
        var fromJson = JsonDocument.Parse(jsonBody).RootElement;
        var jsonText = fromJson.GetProperty("text").GetString() ?? "";
        Check("with a transcript", jsonText.Length > 0, $"{jsonText.Length} chars");
        Check("and timing",
              fromJson.GetProperty("timing").GetProperty("rtf").GetDouble() > 0,
              $"rtf {fromJson.GetProperty("timing").GetProperty("rtf").GetDouble():F3}");
        Check("the plain route returns nothing else",
              fromJson.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["text", "timing"]),
              string.Join(", ", fromJson.EnumerateObject().Select(p => p.Name)));

        // 2. The multipart form, which is what OpenAI-shaped clients send.
        var form = await PostFormAsync("/v1/audio/transcriptions");
        var formBody = await form.Content.ReadAsStringAsync();
        Check("multipart transcription is 200", form.StatusCode == HttpStatusCode.OK, formBody);
        var formText = JsonDocument.Parse(formBody).RootElement.GetProperty("text").GetString() ?? "";

        // 3. The two forms must agree. Different decoding would give a
        //    plausible transcript that quietly differs.
        Check("both forms transcribe identically", formText == jsonText,
              formText == jsonText ? $"{formText.Length} chars"
                                   : $"json '{jsonText[..Math.Min(40, jsonText.Length)]}' vs "
                                     + $"form '{formText[..Math.Min(40, formText.Length)]}'");

        // 4. /details carries what the plain route drops.
        var details = await PostJsonAsync("/v1/audio/transcriptions/details",
                                          new { model = "asr", audio });
        var detailBody = await details.Content.ReadAsStringAsync();
        Check("details is 200", details.StatusCode == HttpStatusCode.OK, detailBody);
        var detail = JsonDocument.Parse(detailBody).RootElement;
        Check("details keeps text and timing identical to the plain route",
              (detail.GetProperty("text").GetString() ?? "") == jsonText);

        var fields = detail.EnumerateObject().Select(p => p.Name).ToHashSet();
        Console.WriteLine($"  details carries: {string.Join(", ", fields.Order())}");
        Check("details adds something the plain route did not",
              fields.Count > 2, string.Join(", ", fields.Order()));

        if (fields.Contains("words"))
        {
            var words = detail.GetProperty("words");
            var first = words[0];
            Check("words carry offsets and text",
                  first.GetProperty("word").GetString()?.Length > 0
                  && first.GetProperty("end_sample").GetInt64() > first.GetProperty("start_sample").GetInt64(),
                  $"{words.GetArrayLength()} words");
            Check("sample_rate rides along with the arrays",
                  fields.Contains("sample_rate")
                  && detail.GetProperty("sample_rate").GetInt32() > 0,
                  fields.Contains("sample_rate") ? $"{detail.GetProperty("sample_rate").GetInt32()} Hz" : "absent");

            // The offsets have to describe the audio, not overrun it.
            var duration = detail.GetProperty("timing").GetProperty("audio_duration_ms").GetDouble();
            var rate = detail.GetProperty("sample_rate").GetInt32();
            var last = words[words.GetArrayLength() - 1].GetProperty("end_sample").GetInt64();
            Check("the last word ends inside the recording",
                  last / (double)rate * 1000.0 <= duration + 50,
                  $"{last / (double)rate:F2}s of {duration / 1000.0:F2}s");
        }

        // 5. Errors.
        var noAudio = await PostJsonAsync("/v1/audio/transcriptions", new { model = "asr" });
        Check("no audio is a 400", noAudio.StatusCode == HttpStatusCode.BadRequest);
        var unknown = await PostJsonAsync("/v1/audio/transcriptions", new { model = "nope", audio });
        Check("an unknown model is a 404", unknown.StatusCode == HttpStatusCode.NotFound);
        var missing = await PostJsonAsync("/v1/audio/transcriptions",
                                          new { model = "asr", audio = "/no/such.wav" });
        Check("a missing file is a 400, not a 500", missing.StatusCode == HttpStatusCode.BadRequest);

        // stream=true on /details is upstream's documented 400: SSE has nowhere
        // to put the detail arrays.
        var streamedDetails = await PostFormAsync("/v1/audio/transcriptions/details", stream: true);
        var streamedBody = await streamedDetails.Content.ReadAsStringAsync();
        Check("stream on /details is refused with a reason",
              streamedDetails.StatusCode == HttpStatusCode.BadRequest
              && streamedBody.Contains("details", StringComparison.Ordinal),
              $"{(int)streamedDetails.StatusCode}");

        // 6. A language nobody asked for must not be forwarded. Clients send the
        //    field unconditionally, and a model that validates options strictly
        //    would reject the whole request over one the user never set.
        var withLanguage = await PostJsonAsync("/v1/audio/transcriptions",
                                               new { model = "asr", audio, language = "en" });
        Check("a language the model may not declare does not break the request",
              withLanguage.StatusCode == HttpStatusCode.OK,
              await withLanguage.Content.ReadAsStringAsync() is var lb && lb.Length > 120 ? lb[..120] : lb);

        await server.StopAsync();
        Console.WriteLine(failures == 0 ? "transcription OK" : $"transcription: {failures} failure(s)");
        return failures;
    }
}
