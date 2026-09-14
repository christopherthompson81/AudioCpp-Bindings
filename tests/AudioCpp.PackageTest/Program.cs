using AudioCpp.Packages;

namespace AudioCpp.PackageTest;

/// <summary>
/// Exercises the catalog and installer against audio.cpp's real model_specs.
/// Network tests are opt-in: pass --network to measure sizes and download the
/// smallest package we can find, so the default run stays offline and fast.
///
/// Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var specs = args.FirstOrDefault(a => !a.StartsWith("--"))
                    ?? Environment.GetEnvironmentVariable("AUDIOCPP_MODEL_SPECS")
                    ?? FindSpecs();

        if (specs is null || !Directory.Exists(specs))
        {
            Console.WriteLine("model_specs not found; skipping.");
            Console.WriteLine("Pass the directory, set AUDIOCPP_MODEL_SPECS, or keep an "
                              + "audio.cpp checkout beside this repo.");
            return 77;
        }

        var failures = 0;
        Console.WriteLine($"specs: {specs}");

        var catalog = Catalog.Load(specs);
        Console.WriteLine($"families: {catalog.Families.Count}   "
                          + $"packages: {catalog.Families.Sum(f => f.Packages.Count)}");

        foreach (var problem in catalog.Unreadable)
        {
            Console.Error.WriteLine($"  UNREADABLE {problem}");
            failures++;
        }

        if (catalog.Families.Count == 0)
        {
            Console.Error.WriteLine("no families parsed");
            return 1;
        }

        // Every package must name a target and at least one file, or install
        // would silently do nothing.
        foreach (var family in catalog.Families)
        {
            foreach (var package in family.Packages)
            {
                if (package.Id.Length == 0 || package.TargetDirectory.Length == 0
                    || package.Files.Count == 0)
                {
                    Console.Error.WriteLine(
                        $"  INCOMPLETE {family.Family}/{package.Id}: "
                        + $"target='{package.TargetDirectory}' files={package.Files.Count}");
                    failures++;
                }
            }
        }

        // strip_prefix must actually apply, otherwise files land nested under a
        // duplicated directory name.
        var root = Path.Combine(Path.GetTempPath(), "audiocpp-packagetest");
        foreach (var family in catalog.Families)
        {
            foreach (var package in family.Packages.Where(p => p.StripPrefix is { Length: > 0 }))
            {
                foreach (var file in package.Files)
                {
                    var local = PackageInstaller.LocalPath(root, package, file);
                    var expected = Path.Combine(root, package.TargetDirectory);
                    if (!local.StartsWith(expected, StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"  ESCAPES {family.Family}/{package.Id}: {local}");
                        failures++;
                    }
                    // Compare whole path segments: a file named after its own
                    // directory (vocos-mel-24khz/vocos-mel-24khz-orig.gguf) is
                    // normal, and a substring test calls it a duplication.
                    var segments = local.Split(Path.DirectorySeparatorChar);
                    if (segments.Zip(segments.Skip(1)).Any(
                            pair => pair.First == pair.Second
                                    && pair.First == package.StripPrefix))
                    {
                        Console.Error.WriteLine($"  DOUBLED {family.Family}/{package.Id}: {local}");
                        failures++;
                    }
                }
            }
        }

        // Spec-supplied paths must not be able to write outside the models root.
        // Absolute paths are the dangerous case: Path.Combine discards the root.
        foreach (var (label, target, file) in new[]
                 {
                     ("traversal target", "../escaped", "a.gguf"),
                     ("absolute target", OperatingSystem.IsWindows() ? "C:\\evil" : "/etc", "a.gguf"),
                     ("traversal file", "ok", "../../escaped.gguf"),
                     ("absolute file", "ok", OperatingSystem.IsWindows() ? "C:\\evil.gguf" : "/etc/passwd"),
                 })
        {
            var hostile = new PackageSpec("hostile", "Hostile", "gguf", "q8_0", target,
                                          [file], null, false,
                                          new DownloadSpec("huggingface_snapshot", "x/y", "main", false));
            // Either outcome is fine -- rejected outright, or neutralised into a
            // path inside the root. What must never happen is a write outside it.
            try
            {
                var resolved = PackageInstaller.LocalPath(root, hostile, file);
                var boundary = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(boundary, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"  ESCAPED via {label}: {resolved}");
                    failures++;
                }
            }
            catch (InvalidOperationException)
            {
                // Rejected, which is also correct.
            }
        }

        var parakeet = catalog.Families.FirstOrDefault(f => f.Family == "parakeet_tdt");
        if (parakeet is not null)
        {
            var package = parakeet.Packages.First(p => p.IsDefault);
            var local = PackageInstaller.LocalPath(root, package, package.Files[0]);
            var expected = Path.Combine(root, "Parakeet-TDT-0.6B-v3-GGUF",
                                        "parakeet-tdt-0.6b-v3-q8_0.gguf");
            Console.WriteLine($"layout:   {local}");
            if (local != expected)
            {
                Console.Error.WriteLine($"  expected {expected}");
                failures++;
            }
        }

        var supported = catalog.Families.SelectMany(f => f.Packages)
                               .Count(p => p.Download.Supported);
        Console.WriteLine($"downloadable: {supported} of "
                          + $"{catalog.Families.Sum(f => f.Packages.Count)}");

        if (args.Contains("--network"))
        {
            failures += await NetworkChecks(catalog, root);
        }
        else
        {
            Console.WriteLine("network checks skipped (pass --network to run them)");
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"{failures} problem(s)");
            return 1;
        }
        Console.WriteLine("package catalog OK");
        return 0;
    }

    /// <summary>
    /// Size lookup and a real download of the smallest package available, then a
    /// delete. Small enough to be polite to the host, real enough to prove the
    /// URL shape, the layout and the cleanup.
    /// </summary>
    private static async Task<int> NetworkChecks(Catalog catalog, string root)
    {
        var installer = new PackageInstaller();
        var failures = 0;

        var candidates = catalog.Families
            .SelectMany(f => f.Packages.Select(p => (Family: f, Package: p)))
            .Where(x => x.Package.Download.Supported && x.Package.Files.Count == 1)
            .ToList();

        (FamilySpec Family, PackageSpec Package)? smallest = null;
        long smallestSize = long.MaxValue;
        var unavailable = 0;
        foreach (var candidate in candidates.Take(25))
        {
            var probe = await installer.ProbeAsync(candidate.Package);
            if (!probe.Available)
            {
                // A declared package the repository does not serve. Reported, not
                // failed: the catalog is upstream's and may drift from reality.
                Console.WriteLine($"  unavailable {candidate.Family.Family}/"
                                  + $"{candidate.Package.Id}: {probe.Problem}");
                unavailable++;
                continue;
            }
            if (probe.Bytes > 0 && probe.Bytes < smallestSize)
            {
                smallestSize = probe.Bytes;
                smallest = candidate;
            }
        }
        Console.WriteLine($"probed {Math.Min(candidates.Count, 25)}, "
                          + $"{unavailable} unavailable");

        if (smallest is null)
        {
            Console.Error.WriteLine("  no measurable package found");
            return 1;
        }

        var (family, package) = smallest.Value;
        Console.WriteLine($"smallest: {family.Family}/{package.Id} "
                          + $"{smallestSize / 1024.0 / 1024.0:F1} MB");

        Directory.CreateDirectory(root);
        try
        {
            try
            {
            var reports = 0;
            var progress = new Progress<InstallProgress>(_ => Interlocked.Increment(ref reports));
            await installer.InstallAsync(root, package, progress);

            var state = PackageInstaller.StateOf(root, package);
            var bytes = PackageInstaller.BytesOnDisk(root, package);
            Console.WriteLine($"installed: {state}, {bytes} bytes, {reports} progress report(s)");

            if (state != InstallState.Installed) { Console.Error.WriteLine("  not Installed"); failures++; }
            if (bytes != smallestSize) { Console.Error.WriteLine($"  size {bytes} != {smallestSize}"); failures++; }
            if (reports == 0) { Console.Error.WriteLine("  no progress reported"); failures++; }

                PackageInstaller.Delete(root, package);
                if (PackageInstaller.StateOf(root, package) != InstallState.Missing)
                {
                    Console.Error.WriteLine("  delete left files behind");
                    failures++;
                }
                Console.WriteLine("deleted:  Missing");
            }
            catch (Exception exception) when (exception is HttpRequestException
                                              or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine($"  install failed: {exception.Message}");
                failures++;
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return failures;
    }

    private static string? FindSpecs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[]
                     {
                         // The pinned engine first: the catalogue should be checked
                         // against the specs this repository is built against, not
                         // whichever audio.cpp checkout happens to sit nearby.
                         Path.Combine(dir.FullName, "external", "audio.cpp", "model_specs"),
                         Path.Combine(dir.FullName, "audio.cpp", "model_specs"),
                         dir.Parent is null ? null
                             : Path.Combine(dir.Parent.FullName, "audio.cpp", "model_specs"),
                     })
            {
                if (candidate is not null && Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
