namespace AudioCpp.Packages;

/// <summary>What a package's files amount to locally.</summary>
public enum InstallState
{
    /// <summary>No file of this package is present.</summary>
    Missing,

    /// <summary>Some files present, or a download left part-way.</summary>
    Partial,

    /// <summary>Every declared file is present.</summary>
    Installed,
}

/// <param name="BytesDone">Bytes written across all files so far.</param>
/// <param name="BytesTotal">Total where known; 0 when the server would not say.</param>
public readonly record struct InstallProgress(
    string File, int FileIndex, int FileCount, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : 0;
}

/// <summary>
/// Downloads model packages from the repositories their specs name.
///
/// This reimplements what audio.cpp's native model manager does, because the C
/// ABI exposes no model management and enabling the native one means building
/// the engine with BoringSSL (issue #10). The protocol is not engine-specific:
/// a spec names a HuggingFace repository, a revision, and a list of files.
/// </summary>
public sealed class PackageInstaller(HttpClient? client = null)
{
    // Files land here first and are moved into place only once complete, so an
    // interrupted download can never be mistaken for an installed package.
    private const string PartialSuffix = ".part";

    private readonly HttpClient _client = client ?? new HttpClient
    {
        // Large weights on a slow link; the default 100s applies to the whole
        // response, not just the headers, and would abort mid-download.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Where a package's files belong under <paramref name="modelsRoot"/>.</summary>
    public static string TargetDirectory(string modelsRoot, PackageSpec package) =>
        Contain(modelsRoot, Path.Combine(modelsRoot, package.TargetDirectory),
                package.TargetDirectory);

    /// <summary>
    /// Assert a path stays under the models root.
    /// </summary>
    /// <remarks>
    /// Specs are data fetched from a repository, so neither target_directory nor
    /// a file path gets to decide where this writes. Absolute paths matter more
    /// than "..": Path.Combine("/models", "/etc") discards the root silently and
    /// returns "/etc", so a check on segments alone would not catch it.
    /// </remarks>
    private static string Contain(string modelsRoot, string candidate, string offending)
    {
        var root = Path.GetFullPath(modelsRoot);
        var full = Path.GetFullPath(candidate);

        var boundary = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(boundary, StringComparison.Ordinal)
            && !string.Equals(full, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"package path escapes the models root: '{offending}'");
        }
        return full;
    }

    /// <summary>
    /// Local path for one of a package's repository-relative files, with the
    /// package's strip_prefix removed.
    /// </summary>
    public static string LocalPath(string modelsRoot, PackageSpec package, string file)
    {
        var relative = file.Replace('\\', '/');
        if (package.StripPrefix is { Length: > 0 } prefix)
        {
            var head = prefix.TrimEnd('/') + "/";
            if (relative.StartsWith(head, StringComparison.Ordinal))
                relative = relative[head.Length..];
        }

        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Contain(
            modelsRoot,
            Path.Combine([TargetDirectory(modelsRoot, package), .. parts]),
            file);
    }

    public static InstallState StateOf(string modelsRoot, PackageSpec package)
    {
        if (package.Files.Count == 0) return InstallState.Missing;

        var present = package.Files.Count(f => File.Exists(LocalPath(modelsRoot, package, f)));
        if (present == package.Files.Count) return InstallState.Installed;
        if (present > 0 || HasPartials(modelsRoot, package)) return InstallState.Partial;
        return InstallState.Missing;
    }

    private static bool HasPartials(string modelsRoot, PackageSpec package)
    {
        var directory = TargetDirectory(modelsRoot, package);
        return Directory.Exists(directory)
               && Directory.EnumerateFiles(directory, "*" + PartialSuffix,
                                           SearchOption.AllDirectories).Any();
    }

    /// <summary>Bytes already on disk for this package.</summary>
    public static long BytesOnDisk(string modelsRoot, PackageSpec package) =>
        package.Files
               .Select(f => LocalPath(modelsRoot, package, f))
               .Where(File.Exists)
               .Sum(p => new FileInfo(p).Length);

    private static Uri FileUri(PackageSpec package, string file) => new(
        $"https://huggingface.co/{package.Download.Repo}/resolve/"
        + $"{package.Download.Revision}/{file.Replace('\\', '/')}");

    /// <summary>
    /// Whether a package can actually be fetched, and how big it is.
    /// </summary>
    /// <remarks>
    /// Availability is not a formality. The shipped specs declare packages whose
    /// files are absent from the repository -- chatterbox_turbo_f16 is one -- and
    /// a 404 answers HEAD with a short error body. Counting that body as the file
    /// size reports a plausible-looking package that fails the moment it is
    /// installed, so status is checked before any length is believed.
    /// </remarks>
    public sealed record PackageProbe(bool Available, long Bytes, string? Problem);

    /// <summary>
    /// Size and availability, without downloading. HuggingFace answers HEAD on a
    /// resolve URL with a redirect carrying x-linked-size, so the size is known
    /// without following through to the CDN.
    /// </summary>
    public async Task<PackageProbe> ProbeAsync(
        PackageSpec package, CancellationToken token = default)
    {
        if (!package.Download.Supported)
            return new PackageProbe(false, 0, $"no automated download (kind '{package.Download.Kind}')");

        long total = 0;
        foreach (var file in package.Files)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, FileUri(package, file));
                response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);
            }
            catch (HttpRequestException exception)
            {
                return new PackageProbe(false, 0, $"{file}: {exception.Message}");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return new PackageProbe(false, 0, $"{file}: HTTP {(int)response.StatusCode}");

                if (response.Headers.TryGetValues("x-linked-size", out var linked)
                    && long.TryParse(linked.FirstOrDefault(), out var size))
                {
                    total += size;
                }
                else if (response.Content.Headers.ContentLength is { } length)
                {
                    total += length;
                }
            }
        }
        return new PackageProbe(true, total, null);
    }

    /// <summary>Total download size, or 0 if the package cannot be fetched.</summary>
    public async Task<long> MeasureAsync(PackageSpec package, CancellationToken token = default)
        => (await ProbeAsync(package, token)).Bytes;

    /// <summary>
    /// Fetch every file the package declares. Reports progress as it goes and
    /// honours cancellation between reads, so a cancelled install stops within
    /// a buffer rather than at the end of a file.
    /// </summary>
    public async Task InstallAsync(
        string modelsRoot,
        PackageSpec package,
        IProgress<InstallProgress>? progress = null,
        CancellationToken token = default)
    {
        if (!package.Download.Supported)
        {
            throw new InvalidOperationException(
                $"{package.Id} declares no automated download "
                + $"(kind '{package.Download.Kind}'); install its files manually.");
        }

        var probe = await ProbeAsync(package, token);
        if (!probe.Available)
        {
            throw new InvalidOperationException(
                $"{package.Id} cannot be downloaded: {probe.Problem}. "
                + "The spec declares it, but the repository does not serve it.");
        }

        var total = probe.Bytes;
        long done = 0;

        for (var index = 0; index < package.Files.Count; index++)
        {
            var file = package.Files[index];
            var destination = LocalPath(modelsRoot, package, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                done += new FileInfo(destination).Length;
                progress?.Report(new InstallProgress(
                    file, index + 1, package.Files.Count, done, total));
                continue;
            }

            var partial = destination + PartialSuffix;
            try
            {
                using (var response = await _client.GetAsync(
                           FileUri(package, file), HttpCompletionOption.ResponseHeadersRead, token))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"{package.Id}: {file} returned HTTP {(int)response.StatusCode}");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    await using var target = File.Create(partial);

                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        progress?.Report(new InstallProgress(
                            file, index + 1, package.Files.Count, done, total));
                    }
                }

                File.Move(partial, destination, overwrite: true);
            }
            catch
            {
                // Leave nothing that could pass for a complete file. The .part is
                // removed here rather than left for CleanPartials so a failure
                // does not silently consume disk.
                TryDelete(partial);
                throw;
            }
        }
    }

    /// <summary>Remove interrupted downloads, leaving completed files alone.</summary>
    public static int CleanPartials(string modelsRoot, PackageSpec package)
    {
        var directory = TargetDirectory(modelsRoot, package);
        if (!Directory.Exists(directory)) return 0;

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(
                     directory, "*" + PartialSuffix, SearchOption.AllDirectories))
        {
            if (TryDelete(path)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// Delete an installed package. Only the files the spec declares, plus any
    /// partials, and only directories left empty by that — a models root may
    /// hold a user's own files beside a package.
    /// </summary>
    public static void Delete(string modelsRoot, PackageSpec package)
    {
        foreach (var file in package.Files) TryDelete(LocalPath(modelsRoot, package, file));
        CleanPartials(modelsRoot, package);

        var directory = TargetDirectory(modelsRoot, package);
        if (!Directory.Exists(directory)) return;

        foreach (var path in Directory.EnumerateDirectories(
                     directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            RemoveIfEmpty(path);
        }
        RemoveIfEmpty(directory);
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException) { /* a file appeared, or it is in use; leave it */ }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
