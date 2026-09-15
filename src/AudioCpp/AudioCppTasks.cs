using System.Collections.Concurrent;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>
/// The task vocabulary this build of the engine accepts, and the translation
/// from the names a model spec declares.
/// </summary>
/// <remarks>
/// A spec and the ABI spell the same kinds differently — a spec says "music",
/// "clone", "design" where the ABI takes "gen", "clon", "vdes" — and nine of
/// the fourteen are identical, which is what makes comparing them directly
/// appear to work. This used to be a hardcoded table on this side, because
/// every other task-aware call takes a loaded model and a picker built from
/// <c>model_specs/*.json</c> has nothing loaded yet. The engine answers both
/// questions itself as of audio.cpp's task-vocabulary change, so this is a
/// thin pass-through and the table is gone.
/// </remarks>
public static class AudioCppTasks
{
    private static readonly Lazy<IReadOnlyList<string>> Tokens = new(() =>
    {
        var count = (int)NativeMethods.audiocpp_task_count();
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = Utf8.ToString(NativeMethods.audiocpp_task_name((nuint)i));
        return names;
    });

    // Spec names are looked up once per catalogue entry per task, which is
    // every visible row on every tab switch. The engine's answer cannot change
    // within a process, so it is asked once per distinct name.
    private static readonly ConcurrentDictionary<string, string?> SpecNames = new(StringComparer.Ordinal);

    /// <summary>Every canonical task token this build accepts.</summary>
    public static IReadOnlyList<string> All => Tokens.Value;

    /// <summary>
    /// The canonical token for a model-spec task name ("music" → "gen"), or
    /// null when the name maps to no task kind this build can serve.
    /// </summary>
    /// <remarks>
    /// Null is a real answer, not an error: it is how a caller detects a spec
    /// declaring a task the engine would refuse to build a session for, which
    /// is worth surfacing rather than silently dropping the package.
    /// </remarks>
    public static string? FromSpecName(string specTask)
    {
        ArgumentNullException.ThrowIfNull(specTask);
        return SpecNames.GetOrAdd(specTask, static name =>
        {
            var token = NativeMethods.audiocpp_task_from_spec_name(name);
            return token == IntPtr.Zero ? null : Utf8.ToString(token);
        });
    }
}
