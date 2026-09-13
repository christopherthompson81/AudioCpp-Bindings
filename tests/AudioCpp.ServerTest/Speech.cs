using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AudioCpp;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// `POST /v1/audio/speech` against a real model.
/// </summary>
/// <remarks>
/// Every assertion is about what came back, not that something did. A route
/// that returns 200 and silently ignored the voice, the seed or the reference
/// is the failure this is looking for — the same shape as the desktop bugs
/// this session already found by transcribing output instead of trusting it.
/// </remarks>
internal static class Speech
{
    public static async Task<int> RunAsync(string model, string family, string backend)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var config = new ServerConfig
        {
            Port = 18900 + Random.Shared.Next(300),
            Backend = backend,
            Models = [new ServerModel("tts", family, model, "tts")],
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
            Timeout = TimeSpan.FromMinutes(2),
        };

        async Task<HttpResponseMessage> PostAsync(string json) =>
            await http.PostAsync("/v1/audio/speech",
                new StringContent(json, Encoding.UTF8, "application/json"));

        async Task<HttpResponseMessage> PostObjectAsync(object body) =>
            await PostAsync(JsonSerializer.Serialize(body));

        // 1. The ordinary case: wav bytes a client can play.
        var wav = await PostAsync("""{"model":"tts","input":"The harbour was quiet."}""");
        var bytes = await wav.Content.ReadAsByteArrayAsync();
        Check("speech returns 200", wav.StatusCode == HttpStatusCode.OK,
              wav.StatusCode == HttpStatusCode.OK ? "" : Encoding.UTF8.GetString(bytes));
        Check("with audio/wav", wav.Content.Headers.ContentType?.MediaType == "audio/wav",
              wav.Content.Headers.ContentType?.MediaType ?? "(none)");
        Check("and real WAV bytes",
              bytes.Length > 44 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF",
              $"{bytes.Length} bytes");

        using (var decoded = new MemoryStream(bytes))
        {
            var clip = Wav.Read(decoded);
            var seconds = (double)clip.Samples.Length / Math.Max(clip.Channels, 1)
                          / Math.Max(clip.SampleRate, 1);
            Check("that decode to audible audio",
                  seconds > 0.3 && clip.Samples.Any(s => Math.Abs(s) > 0.01f),
                  $"{seconds:F2}s at {clip.SampleRate} Hz");
        }

        // 2. response_format: json, base64 in a JSON envelope.
        var json = await PostAsync(
            """{"model":"tts","input":"The harbour was quiet.","response_format":"json"}""");
        var envelope = await json.Content.ReadFromJsonAsync<JsonElement>();
        Check("response_format json is JSON",
              json.Content.Headers.ContentType?.MediaType == "application/json");
        var inline = Convert.FromBase64String(envelope.GetProperty("audio").GetString() ?? "");
        Check("carrying the same WAV",
              inline.Length > 44 && Encoding.ASCII.GetString(inline, 0, 4) == "RIFF",
              $"{inline.Length} bytes, {envelope.GetProperty("sample_rate").GetInt32()} Hz");

        // 3. A seed has to reach the model. Same seed twice is the same audio;
        //    a different seed is different audio. A route that dropped the
        //    option would pass the first half and fail the second.
        var seeded1 = await PostAsync("""{"model":"tts","input":"Repeatable.","seed":1234}""");
        var seeded2 = await PostAsync("""{"model":"tts","input":"Repeatable.","seed":1234}""");
        var other = await PostAsync("""{"model":"tts","input":"Repeatable.","seed":9876}""");
        var a = await seeded1.Content.ReadAsByteArrayAsync();
        var b = await seeded2.Content.ReadAsByteArrayAsync();
        var c = await other.Content.ReadAsByteArrayAsync();
        Check("the same seed repeats", a.AsSpan().SequenceEqual(b),
              $"{a.Length} vs {b.Length} bytes");
        Check("a different seed differs", !a.AsSpan().SequenceEqual(c));

        // 4. A seed given as a string has to parse the same as a number: the
        //    reference server documents string seeds so a large value does not
        //    lose precision passing through a JSON double.
        //
        //    Range is a separate thing. The engine takes a uint32 -- upstream's
        //    own UI says "Seed must be -1 or an unsigned 32-bit integer" -- so a
        //    uint64 is refused, and refusing it clearly is correct behaviour
        //    rather than a gap. This check originally asserted uint64 was
        //    accepted, which the system never promised.
        var stringSeed = await PostAsync(
            """{"model":"tts","input":"Repeatable.","seed":"1234"}""");
        var stringSeedBytes = await stringSeed.Content.ReadAsByteArrayAsync();
        Check("a string seed parses like a number",
              stringSeed.StatusCode == HttpStatusCode.OK && stringSeedBytes.AsSpan().SequenceEqual(a),
              stringSeed.StatusCode == HttpStatusCode.OK
                  ? $"{stringSeedBytes.Length} bytes, identical to seed:1234"
                  : Encoding.UTF8.GetString(stringSeedBytes));

        var big = await PostAsync(
            """{"model":"tts","input":"Big seed.","seed":"18446744073709551615"}""");
        Check("a seed past uint32 is refused with a reason",
              big.StatusCode != HttpStatusCode.OK
              && (await big.Content.ReadAsStringAsync()).Contains("uint32", StringComparison.Ordinal));

        // 5. Errors are a contract, not a status line.
        var missing = await PostAsync("""{"model":"tts"}""");
        Check("no input is a 400", missing.StatusCode == HttpStatusCode.BadRequest);
        var unknown = await PostAsync("""{"model":"nope","input":"x"}""");
        Check("an unknown model is a 404", unknown.StatusCode == HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Check("errors use OpenAI's shape",
              unknownBody.GetProperty("error").GetProperty("message").GetString()?.Length > 0,
              unknownBody.GetProperty("error").GetProperty("type").GetString() ?? "");
        var malformed = await http.PostAsync("/v1/audio/speech",
            new StringContent("{not json", Encoding.UTF8, "application/json"));
        Check("malformed JSON is a 400", malformed.StatusCode == HttpStatusCode.BadRequest);
        var streaming = await PostAsync(
            """{"model":"tts","input":"x","stream_format":"sse"}""");
        Check("an unimplemented stream says so rather than lying",
              streaming.StatusCode == HttpStatusCode.BadRequest);

        // 6. Two requests at once against one model. The session is not safe to
        //    share, so the pool has to serialise them rather than corrupt one.
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            var response = await PostAsync($$"""{"model":"tts","input":"Request {{i}}."}""");
            return (response.StatusCode, Length: (await response.Content.ReadAsByteArrayAsync()).Length);
        }));
        Check("four concurrent requests all succeed",
              concurrent.All(r => r.StatusCode == HttpStatusCode.OK && r.Length > 44),
              string.Join(", ", concurrent.Select(r => $"{(int)r.StatusCode}/{r.Length}B")));

        // 7. voice_ref, the richest option path: a server-side path, inline
        //    base64, a data: URI, and the size cap. Only meaningful against a
        //    model that clones.
        var referencePath = Environment.GetEnvironmentVariable("AUDIOCPP_VOICE_REF");
        if (referencePath is { Length: > 0 } && File.Exists(referencePath))
        {
            // Families that clone from inline audio may require the words
            // spoken in it: audio8_tts refuses without reference_text, and
            // says so. A client has to send both, so the checks do.
            var referenceText = Environment.GetEnvironmentVariable("AUDIOCPP_VOICE_REF_TEXT") ?? "";

            var noText = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.", voice_ref = referencePath,
            });
            if (noText.StatusCode != HttpStatusCode.OK)
            {
                // Pinned rather than treated as a failure: the refusal is the
                // model's contract, and the value of checking it is that the
                // reason reaches the client instead of a bare 500.
                var why = await noText.Content.ReadAsStringAsync();
                Check("a reference without its transcript explains itself",
                      why.Contains("reference_text", StringComparison.Ordinal),
                      $"{(int)noText.StatusCode}");
            }

            var plain = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.", voice_ref = referencePath,
                reference_text = referenceText,
            });
            Check("voice_ref as a bare path", plain.StatusCode == HttpStatusCode.OK,
                  plain.StatusCode == HttpStatusCode.OK ? "" : await plain.Content.ReadAsStringAsync());

            var typed = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.",
                voice_ref = new { type = "path", path = referencePath },
                reference_text = referenceText,
            });
            Check("voice_ref as a typed path object", typed.StatusCode == HttpStatusCode.OK);

            var raw = Convert.ToBase64String(await File.ReadAllBytesAsync(referencePath));
            var inlineRef = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.",
                voice_ref = new { type = "base64", data = raw },
                reference_text = referenceText,
            });
            Check("voice_ref inline as base64", inlineRef.StatusCode == HttpStatusCode.OK,
                  inlineRef.StatusCode == HttpStatusCode.OK
                      ? $"{raw.Length} base64 chars"
                      : await inlineRef.Content.ReadAsStringAsync());

            var uriRef = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.",
                voice_ref = new { type = "base64", data = $"data:audio/wav;base64,{raw}" },
                reference_text = referenceText,
            });
            Check("a data: URI is accepted too", uriRef.StatusCode == HttpStatusCode.OK);

            // The cap exists so a client cannot post a gigabyte inline. Over it
            // must be a 400 naming the limit, not a 500 or an OOM.
            var huge = Convert.ToBase64String(new byte[ServerLimits.MaxInlineReferenceBytes + 1024]);
            var tooBig = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.",
                voice_ref = new { type = "base64", data = huge },
            });
            var tooBigBody = await tooBig.Content.ReadAsStringAsync();
            Check("an oversize inline reference is refused",
                  tooBig.StatusCode == HttpStatusCode.BadRequest
                  && tooBigBody.Contains("limit", StringComparison.Ordinal),
                  $"{(int)tooBig.StatusCode}");

            var missingRef = await PostObjectAsync(new
            {
                model = "tts", input = "Cloned.", voice_ref = "/no/such/file.wav",
            });
            Check("a missing reference file is a 400, not a 500",
                  missingRef.StatusCode == HttpStatusCode.BadRequest,
                  $"{(int)missingRef.StatusCode}");
        }
        else
        {
            Console.WriteLine("  (no AUDIOCPP_VOICE_REF; voice_ref paths not checked)");
        }

        await server.StopAsync();
        Console.WriteLine(failures == 0 ? "speech OK" : $"speech: {failures} failure(s)");
        return failures;
    }
}
