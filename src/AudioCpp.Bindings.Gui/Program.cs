using Avalonia;

namespace AudioCpp.Bindings.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A GUI cannot be driven headlessly, but the view model can, and it is where
        // every ABI call lives. --smoke exercises the same path the window does, so
        // "the demo works" is checkable without a display.
        if (args.Length > 0 && args[0] == "--smoke")
        {
            return Smoke.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        }

        // Every --*-check runs against a scratch config directory. The server
        // page writes a real server.json when it starts, so without this a
        // check would overwrite the config of whoever ran it -- and would then
        // inherit whatever the last check left behind, which is how a check
        // that passed on its own started failing in sequence.
        //
        // Set before Avalonia builds anything, because the view model restores
        // that config in its constructor.
        if (args.Any(argument => argument.EndsWith("-check", StringComparison.Ordinal)))
        {
            var scratch = Path.Combine(Path.GetTempPath(), $"audiocpp-check-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", scratch);
        }

        // Settings round-trip, in a scratch directory so a check never writes
        // over the settings of the person running it.
        if (args.Contains("--settings-check"))
        {
            return SettingsCheck.Run();
        }

        // Pure logic, so it runs without a display. The save dialog itself
        // cannot be driven headlessly; what broke was the mapping in front of
        // it, and that can be.
        if (args.Contains("--save-check"))
        {
            var failures = 0;
            foreach (var (suggested, title, description, extension) in new[]
                     {
                         ("output.wav", "Save audio", "WAV audio", "wav"),
                         ("vocals.wav", "Save audio", "WAV audio", "wav"),
                         ("transcript.srt", "Save subtitles", "SubRip subtitles", "srt"),
                         ("transcript.vtt", "Save subtitles", "WebVTT subtitles", "vtt"),
                         ("result.json", "Save data", "JSON", "json"),
                         ("result.mid", "Save MIDI", "MIDI file", "mid"),
                         ("clip.mp4", "Save video", "MP4 video", "mp4"),
                         ("notes.unknown", "Save file", "UNKNOWN", "unknown"),
                     })
            {
                var kind = SaveKind.For(suggested);
                Console.WriteLine($"{suggested,-18} {kind.Title,-15} {kind.Description,-18} {kind.Pattern}");
                if (kind.Title != title || kind.Description != description
                    || kind.Extension != extension)
                {
                    Console.Error.WriteLine($"  expected {title} / {description} / *.{extension}");
                    failures++;
                }
            }
            Console.WriteLine(failures == 0 ? "save kinds OK" : $"save kinds: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }

        // A LiveAvatar run takes minutes on a large GPU; whether its frames
        // survive the trip to a file does not need one. Synthetic frames in the
        // shape the engine reports, through the same save path.
        if (args.Contains("--video-check"))
        {
            return VideoCheck().GetAwaiter().GetResult();
        }

        if (args.Contains("--component-check"))
        {
            return ComponentCheck();
        }

        if (args.Contains("--spec-options-check"))
        {
            return SpecOptionsCheck();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static async Task<int> VideoCheck()
    {
        var failures = 0;
        void Expect(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }

        // Odd-sized on purpose: yuv420p needs even dimensions, and the engine
        // does not promise them.
        const int width = 65, height = 49, fps = 24, frames = 24;
        var payload = new byte[width * height * 3 * frames];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);

        var artifact = new ArtifactEntry("liveavatar_video_rgb24", "Custom", payload,
            $"format=rgb24, width={width}, height={height}, frames={frames}, fps={fps}");
        Expect($"rgb24 artifact is a video ({artifact.Video})", artifact.Video == new VideoFormat(width, height, fps));
        Expect($"suggested as {artifact.SuggestedFileName}", artifact.SuggestedFileName == "liveavatar_video_rgb24.mp4");
        Expect("save dialog offers MP4", SaveKind.For(artifact.SuggestedFileName).Description == "MP4 video");
        var midi = new ArtifactEntry("score", "Custom", [1], "format=midi, extension=mid");
        Expect("other artifacts are not videos", midi.Video is null && midi.SuggestedFileName == "score.mid");

        var dir = Directory.CreateTempSubdirectory("audiocpp-video-check").FullName;
        try
        {
            var tone = new float[16000];
            for (var i = 0; i < tone.Length; i++) tone[i] = 0.2f * MathF.Sin(i * 0.1f);
            (float[], int, int)? audio = (tone, 16000, 1);

            foreach (var source in new[] { audio, null })
            {
                var target = Path.Combine(dir, source is null ? "silent.mp4" : "voiced.mp4");
                var status = await VideoExport.SaveAsync(payload, artifact.Video!, source, target);
                Console.WriteLine($"  {status}");
                if (!File.Exists(target))
                {
                    Expect($"{Path.GetFileName(target)} written", File.Exists(Path.ChangeExtension(target, ".rgb")));
                    continue;
                }
                var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ffprobe", $"-v error -show_entries stream=codec_type,width,height,nb_frames -of csv=p=0 \"{target}\"")
                { RedirectStandardOutput = true })!;
                var streams = (await probe.StandardOutput.ReadToEndAsync()).Trim();
                await probe.WaitForExitAsync();
                Console.WriteLine($"  ffprobe: {streams.Replace('\n', ' ')}");
                Expect($"{Path.GetFileName(target)} has {frames} padded frames",
                       streams.Contains($"video,66,50,{frames}"));
                Expect($"{Path.GetFileName(target)} audio track {(source is null ? "absent" : "present")}",
                       streams.Contains("audio") == (source is not null));
            }

            // ffmpeg present but failing (here: a container it cannot choose)
            // must still leave the frames on disk.
            var failed = await VideoExport.SaveAsync(payload, artifact.Video!, null, Path.Combine(dir, "clip.nosuchformat"));
            Console.WriteLine($"  {failed}");
            Expect("a failed encode still writes the raw frames",
                   failed.StartsWith("ffmpeg failed", StringComparison.Ordinal)
                   && new FileInfo(Path.Combine(dir, "clip.rgb")).Length == payload.Length);

            try
            {
                await VideoExport.SaveAsync(payload[..^1], artifact.Video!, null, Path.Combine(dir, "short.mp4"));
                Expect("a truncated payload is refused", false);
            }
            catch (IOException)
            {
                Expect("a truncated payload is refused", true);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Console.WriteLine(failures == 0 ? "video export OK" : $"video export: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Component pickers offer each component its own files. The file sets and
    /// defaults are the ones the four component families' specs declare, with
    /// every package installed, since that is when the lists get confusable.
    /// </summary>
    private static int ComponentCheck()
    {
        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }
        var families = new (string Family, string[] Files, (string Option, string Default, string[] Expected)[] Options)[]
        {
            ("yue2",
                ["yue2-3b-bf16.gguf", "yue2-3b-q4_0.gguf", "yue2-3b-q8_0.gguf", "yue2-vae-f16.gguf", "yue2-vae-f32.gguf"],
                [("yue2.model_gguf", "yue2-3b-q8_0.gguf", ["yue2-3b-bf16.gguf", "yue2-3b-q4_0.gguf", "yue2-3b-q8_0.gguf"]),
                 ("yue2.vae_gguf", "yue2-vae-f16.gguf", ["yue2-vae-f16.gguf", "yue2-vae-f32.gguf"])]),
            ("auk",
                ["auk-base-f16.gguf", "auk-base-f32.gguf", "auk-base-q8_0.gguf", "auk-flash-f16.gguf",
                 "auk-flash-f32.gguf", "auk-flash-q8_0.gguf", "auk-vae-f32.gguf",
                 "qwen2.5-omni-3b-bf16.gguf", "qwen2.5-omni-3b-q8_0.gguf"],
                [("auk.model_gguf", "", ["", "auk-base-f16.gguf", "auk-base-f32.gguf", "auk-base-q8_0.gguf",
                                         "auk-flash-f16.gguf", "auk-flash-f32.gguf", "auk-flash-q8_0.gguf"]),
                 ("auk.qwen_gguf", "qwen2.5-omni-3b-bf16.gguf", ["qwen2.5-omni-3b-bf16.gguf", "qwen2.5-omni-3b-q8_0.gguf"]),
                 ("auk.vae_gguf", "auk-vae-f32.gguf", ["auk-vae-f32.gguf"])]),
            ("minimax_music3",
                ["condition_encoder.gguf", "language_model_bf16.gguf", "language_model_q4_0.gguf",
                 "language_model_q8_0.gguf", "rvq_depth_decoder_bf16.gguf", "rvq_depth_decoder_q8_0.gguf",
                 "transformer_bf16.gguf", "transformer_q4_0.gguf", "transformer_q8_0.gguf", "vocoder.gguf"],
                [("minimax_music3.language_model_gguf", "language_model_q4_0.gguf",
                    ["language_model_bf16.gguf", "language_model_q4_0.gguf", "language_model_q8_0.gguf"]),
                 ("minimax_music3.flow_transformer_gguf", "transformer_q4_0.gguf",
                    ["transformer_bf16.gguf", "transformer_q4_0.gguf", "transformer_q8_0.gguf"])]),
            ("liveavatar",
                ["Wan2.2-S2V-14B-NVFP4-LORA.gguf", "Wan2.2-S2V-Support-Q4_K_S-F16.gguf", "Wan2.2-S2V-VAE-F16.gguf"],
                [("liveavatar.support_gguf", "Wan2.2-S2V-Support-Q4_K_S-F16.gguf", ["Wan2.2-S2V-Support-Q4_K_S-F16.gguf"]),
                 ("liveavatar.vae_gguf", "Wan2.2-S2V-VAE-F16.gguf", ["Wan2.2-S2V-VAE-F16.gguf"])]),
        };

        foreach (var (family, files, options) in families)
        {
            var dir = Directory.CreateTempSubdirectory($"audiocpp-{family}").FullName;
            try
            {
                foreach (var file in files) File.WriteAllBytes(Path.Combine(dir, file), []);
                File.WriteAllBytes(Path.Combine(dir, "README.md"), []);
                var defaults = options.Select(o => o.Default).Where(d => d.Length > 0).ToList();
                var onDisk = DeclaredOption.GgufFiles(dir);
                foreach (var (option, @default, expected) in options)
                {
                    var offered = DeclaredOption.ComponentFiles(option, "string", @default, onDisk, defaults) ?? [];
                    var ok = offered.SequenceEqual(expected);
                    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {option}: {string.Join(", ", offered.Select(f => f.Length > 0 ? f : "(default)"))}");
                    if (!ok)
                    {
                        Console.Error.WriteLine($"  expected {string.Join(", ", expected)}");
                        failures++;
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // Not a component: a choice type already has its choices, and a name
        // that is not *_gguf is a string the user types.
        var notComponents = DeclaredOption.ComponentFiles("yue2.style", "string", "", ["a.gguf"], [])
                            ?? DeclaredOption.ComponentFiles("x.vae_gguf", "a|b", "", ["a.gguf"], []);
        Check("non-component options get no file list", notComponents is null);

        // A precision the stem does not know is still offered -- but never the
        // other component's files, which is what the fallback used to bring back.
        var unknown = DeclaredOption.ComponentFiles("yue2.model_gguf", "string", "yue2-3b-q8_0.gguf",
            ["yue2-3b-weird.gguf", "yue2-vae-f16.gguf"], ["yue2-3b-q8_0.gguf", "yue2-vae-f16.gguf"]) ?? [];
        Check($"unrecognised precision offered without the VAE ({string.Join(", ", unknown)})",
              unknown.SequenceEqual(["yue2-3b-q8_0.gguf", "yue2-3b-weird.gguf"]));
        var iq = DeclaredOption.ComponentFiles("yue2.model_gguf", "string", "yue2-3b-q8_0.gguf",
            ["yue2-3b-iq4_xs.gguf", "yue2-vae-f16.gguf"], ["yue2-3b-q8_0.gguf", "yue2-vae-f16.gguf"]) ?? [];
        Check($"iq quantisations share the stem ({string.Join(", ", iq)})",
              iq.SequenceEqual(["yue2-3b-q8_0.gguf", "yue2-3b-iq4_xs.gguf"]));

        // Choosing a Flash generator has to choose the Flash schedule too.
        var variant = new DeclaredOption("Session", "auk.variant", "base|flash", "\"base\"", "", false);
        var generator = new DeclaredOption("Session", "auk.model_gguf", "string", "", "", false,
            fileChoices: ["", "auk-base-f32.gguf", "auk-flash-q8_0.gguf"]);
        DeclaredOption.LinkVariants([variant, generator]);
        generator.Choice = "auk-flash-q8_0.gguf";
        Check($"a Flash file selects variant=flash (variant '{variant.Value}')", variant.Choice == "flash");
        generator.Choice = "auk-base-f32.gguf";
        Check($"a Base file selects variant=base (value '{variant.Value}', cleared as the default)",
              variant.Choice == "base" && variant.Value == "");

        Console.WriteLine(failures == 0 ? "component files OK" : $"component files: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The spec's request options fill in what the ABI leaves undeclared, and
    /// only that. The declared list is YuE2's <c>yue2_cli_interface()</c> as
    /// the pinned engine has it; the documented one is the real spec file.
    /// </summary>
    private static int SpecOptionsCheck()
    {
        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }

        var specs = MainWindowViewModel.FindModelSpecs();
        Check($"model_specs found ({specs})", specs is not null);
        if (specs is null) return 1;
        var yue2 = AudioCpp.Packages.Catalog.Load(specs).Families.FirstOrDefault(f => f.Family == "yue2");
        Check("yue2 spec parsed", yue2 is not null);
        if (yue2 is null) return 1;
        var documented = yue2.RequestOptions ?? [];
        Check($"spec documents {documented.Count} request options", documented.Count >= 27);
        var cot = documented.FirstOrDefault(o => o.Name == "cot");
        Check($"an enum becomes a choice ({cot?.Type}, default {cot?.Default})",
              cot is { Type: "off|melody|full", Default: "full" });
        var temperature = documented.FirstOrDefault(o => o.Name == "abc_temperature");
        Check($"numeric bounds kept ({temperature?.Min}..{temperature?.Max})",
              temperature is { Min: "0.0", Max: "5.0" });

        string[] request = ["style", "lyrics", "abc", "abc_file", "cot", "export_semantic", "stop_after",
                            "seed", "guidance_scale", "num_inference_steps"];
        var declared = request.Select(name => new DeclaredOption("Request", name, "string", "", "", false))
            .Append(new DeclaredOption("Session", "yue2.model_gguf", "string", "", "", false))
            .ToList();
        var extra = DeclaredOption.SpecOnly(documented, declared);
        Console.WriteLine($"     spec-only: {string.Join(", ", extra.Select(o => o.Name))}");
        Check($"none the ABI declares comes back ({extra.Count})",
              !extra.Any(o => request.Contains(o.Name)) && extra.All(o => o.FromSpec));
        Check("the fourteen sampling controls are among them",
              extra.Count(o => o.Name.StartsWith("abc_", StringComparison.Ordinal)
                               || o.Name.StartsWith("semantic_", StringComparison.Ordinal)
                                  && !o.Name.Contains("prefix")) == 14);
        var byName = extra.ToDictionary(o => o.Name);
        Check("a bounded float with no default is a spinner, not a slider", byName["abc_temperature"].IsNumber);
        Check("a half-bounded int is a spinner", byName["semantic_top_k"].IsNumber);
        Check("a token list is long text", byName["semantic_prefix"].IsLongText);
        Check("a file is a path", byName["nar_noise_file"].IsPath && !byName["nar_noise_file"].IsLongText);
        // A list copied from the spec (most families) offers nothing extra:
        // the engine would refuse an option its embedded spec does not name.
        var copied = documented.Take(5)
            .Select(o => new DeclaredOption("Request", o.Name, o.Type, o.Default, "", o.Required, o.Description))
            .ToList();
        Check("a spec-derived ABI list gets no spec-only options",
              DeclaredOption.SpecOnly(documented, copied).Count == 0);
        var noDefault = byName["semantic_max_tokens"];
        Check($"no default: a spinner, empty until set ({noDefault.Editor}, {noDefault.NumberValue?.ToString() ?? "null"})",
              noDefault.IsNumber && noDefault.NumberValue is null && !byName["abc_temperature"].IsSlider);
        noDefault.NumberValue = 250;
        noDefault.NumberValue = null;
        Check("clearing it hands the choice back to the engine", noDefault.Value == "");

        Check("a prefixed ABI name suppresses the bare spec one",
              !DeclaredOption.SpecOnly(documented, [new DeclaredOption("Request", "yue2.cot", "a|b", "", "", false)])
                  .Any(o => o.Name == "cot"));

        // The score round trip: a planner's ABC artifact fills the abc option.
        var score = new ArtifactEntry("score", "Text", "X:1\nK:C\nCDEF|"u8.ToArray(), "format=abc, extension=abc");
        Check("an ABC artifact is a score", score.IsScore);
        Check("a MIDI artifact is not", !new ArtifactEntry("midi", "Binary", [1], "extension=mid").IsScore);

        Console.WriteLine(failures == 0 ? "spec options OK" : $"spec options: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
