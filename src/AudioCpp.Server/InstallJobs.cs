using System.Collections.Concurrent;
using System.Diagnostics;
using AudioCpp.Packages;

namespace AudioCpp.Server;

/// <summary>
/// Package downloads running in the background, as the UI routes see them.
/// </summary>
/// <remarks>
/// The HTTP surface is start / poll / stop rather than one long request,
/// because a download takes minutes and a browser that reloads or a laptop that
/// sleeps would otherwise abandon it. That makes the job outlive the request
/// that created it, which is what this holds.
///
/// One job per package id at a time. A second start for the same id returns the
/// running job rather than launching a second downloader over the same files:
/// two writers on one directory is not a race that shows up in testing, it is
/// a corrupted model that shows up months later.
/// </remarks>
internal sealed class InstallJobs(string modelSpecsDirectory, Action<string> log)
{
    /// <summary>What a client polls for.</summary>
    /// <remarks>
    /// The field names and the <c>state</c> vocabulary are upstream's, including
    /// <c>progress_percent</c> being -1 when there is nothing to report rather
    /// than 0 — a client drawing a progress bar needs to tell "no progress yet"
    /// from "starting", and 0 says the second.
    /// </remarks>
    internal sealed record Job(string Id)
    {
        public string State { get; set; } = "queued";
        public string Message { get; set; } = "Queued";
        public int ExitCode { get; set; } = -1;
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public long StartedAtMs { get; set; }
        public long FinishedAtMs { get; set; }
        public CancellationTokenSource Cancel { get; } = new();

        public int ProgressPercent => State switch
        {
            "queued" => 0,
            "complete" => 100,
            _ when TotalBytes > 0 =>
                (int)Math.Min(100, DownloadedBytes * 100 / TotalBytes),
            _ => -1,
        };

        public Dictionary<string, object?> ToJson() => new()
        {
            ["id"] = Id,
            ["state"] = State,
            ["message"] = Message,
            ["exit_code"] = ExitCode,
            ["downloaded_bytes"] = DownloadedBytes,
            ["total_bytes"] = TotalBytes,
            ["progress_percent"] = ProgressPercent,
            ["started_at_ms"] = StartedAtMs,
            ["finished_at_ms"] = FinishedAtMs,
        };
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly PackageInstaller _installer = new();

    /// <summary>Where packages are installed. Settable, which is what models-root is for.</summary>
    public string ModelsRoot { get; set; } = "";

    public bool Busy => _jobs.Values.Any(job => job.State is "queued" or "running" or "cancelling");

    /// <summary>The catalogue, reloaded each time so a spec added on disk is seen.</summary>
    public Catalog Catalog() => AudioCpp.Packages.Catalog.Load(modelSpecsDirectory);

    public (FamilySpec Family, PackageSpec Package)? Find(string packageId)
    {
        foreach (var family in Catalog().Families)
        {
            foreach (var package in family.Packages)
            {
                // Both spellings, because a package id is only unique within its
                // family: a client that knows the family should be able to say
                // so, and one that does not should still find a unique id.
                if (package.Id == packageId || $"{family.Family}/{package.Id}" == packageId)
                {
                    return (family, package);
                }
            }
        }
        return null;
    }

    public Dictionary<string, object?> Start(string packageId, bool overwrite)
    {
        if (Find(packageId) is not { } found)
        {
            throw new InvalidDataException($"unknown package id: {packageId}");
        }

        // An id already running is returned as it stands. Reporting a fresh
        // "queued" over a download that is half finished would reset every
        // progress bar watching it.
        if (_jobs.TryGetValue(packageId, out var running)
            && running.State is "queued" or "running" or "cancelling")
        {
            return running.ToJson();
        }

        if (!overwrite
            && PackageInstaller.StateOf(ModelsRoot, found.Package) == InstallState.Installed)
        {
            var onDisk = PackageInstaller.BytesOnDisk(ModelsRoot, found.Package);
            var already = new Job(packageId)
            {
                State = "complete",
                Message = "Already installed",
                ExitCode = 0,
                DownloadedBytes = onDisk,
                TotalBytes = onDisk,
            };
            _jobs[packageId] = already;
            return already.ToJson();
        }

        var job = new Job(packageId)
        {
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Message = "Starting",
        };
        _jobs[packageId] = job;

        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                job.State = "running";
                job.Message = "Downloading";
                var progress = new Progress<InstallProgress>(update =>
                {
                    job.DownloadedBytes = update.BytesDone;
                    job.TotalBytes = update.BytesTotal;
                });
                await _installer.InstallAsync(ModelsRoot, found.Package, progress, job.Cancel.Token);
                job.State = "complete";
                job.Message = "Installed";
                job.ExitCode = 0;
                log($"installed {packageId} in {clock.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
                job.Message = "Stopped";
                job.ExitCode = 1;
                log($"install of {packageId} stopped");
            }
            catch (Exception error)
            {
                // Every failure is the client's to see, including the ones that
                // are really the repository's. A job stuck at "running" with no
                // message is the one outcome a poller cannot act on.
                job.State = "failed";
                job.Message = error.Message;
                job.ExitCode = 1;
                log($"install of {packageId} failed: {error.Message}");
            }
            finally
            {
                job.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        });

        return job.ToJson();
    }

    /// <summary>
    /// One job, or all of them when no id is given.
    /// </summary>
    /// <remarks>
    /// An id that has never been started is "idle" rather than a 404: a UI polls
    /// this for every package it lists, and most of them have never been
    /// touched. Answering with an error for the normal case would make the
    /// normal case look broken.
    /// </remarks>
    public Dictionary<string, object?> Status(string packageId)
    {
        if (packageId.Length == 0)
        {
            return new Dictionary<string, object?>
            {
                ["data"] = _jobs.Values.Select(job => job.ToJson()).ToArray(),
            };
        }
        return _jobs.TryGetValue(packageId, out var job)
            ? job.ToJson()
            : new Job(packageId) { State = "idle", Message = "Not started" }.ToJson();
    }

    public Dictionary<string, object?> Stop(string packageId)
    {
        if (_jobs.TryGetValue(packageId, out var job)
            && job.State is "queued" or "running")
        {
            job.State = "cancelling";
            job.Message = "Stopping";
            job.Cancel.Cancel();
        }
        return Status(packageId);
    }

    public Dictionary<string, object?> CleanPartial(string packageId)
    {
        if (Find(packageId) is not { } found)
        {
            throw new InvalidDataException($"unknown package id: {packageId}");
        }
        var removed = PackageInstaller.CleanPartials(ModelsRoot, found.Package);
        return new Dictionary<string, object?> { ["id"] = packageId, ["removed"] = removed };
    }

    public Dictionary<string, object?> Remove(string packageId)
    {
        if (Find(packageId) is not { } found)
        {
            throw new InvalidDataException($"unknown package id: {packageId}");
        }
        if (_jobs.TryGetValue(packageId, out var job)
            && job.State is "queued" or "running" or "cancelling")
        {
            throw new InvalidDataException(
                $"{packageId} is being downloaded; stop it before removing it");
        }
        PackageInstaller.Delete(ModelsRoot, found.Package);
        _jobs.TryRemove(packageId, out _);
        return new Dictionary<string, object?> { ["id"] = packageId, ["removed"] = true };
    }

    /// <summary>
    /// What each package occupies on disk, for the packages that are installed.
    /// </summary>
    /// <remarks>
    /// Local only. Upstream also measures remote sizes here, which means a
    /// network round trip per package: for a catalogue of 235 packages that is
    /// slow enough that a UI cannot call it on a page load, which is when a UI
    /// wants it. What is on disk is the half that can be answered immediately,
    /// and it is the half a "reclaim space" view needs.
    /// </remarks>
    public Dictionary<string, object?> PackageSizes()
    {
        var sizes = new List<object>();
        foreach (var family in Catalog().Families)
        {
            foreach (var package in family.Packages)
            {
                var state = PackageInstaller.StateOf(ModelsRoot, package);
                if (state == InstallState.Missing) continue;
                sizes.Add(new
                {
                    id = package.Id,
                    family = family.Family,
                    state = state.ToString().ToLowerInvariant(),
                    bytes_on_disk = PackageInstaller.BytesOnDisk(ModelsRoot, package),
                    path = PackageInstaller.TargetDirectory(ModelsRoot, package),
                });
            }
        }
        return new Dictionary<string, object?>
        {
            ["state"] = "complete",
            ["message"] = "Package sizes are ready",
            ["data"] = sizes.ToArray(),
        };
    }
}
