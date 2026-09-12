using System.Runtime.InteropServices;
using AudioCpp.Native;

namespace AudioCpp;

/// <summary>One event produced while streaming.</summary>
public sealed class AudioCppStreamEvent : SafeHandle
{
    private readonly AudioCppResult _view;

    internal AudioCppStreamEvent(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
        // The view belongs to the event, so this wrapper must not own it. The
        // native side also treats freeing a borrowed view as a no-op, so a
        // mistake here cannot corrupt the heap either.
        _view = new AudioCppResult(NativeMethods.audiocpp_event_as_result(handle), ownsHandle: false);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Whether this is the last event of the stream.</summary>
    public bool IsFinal => NativeMethods.audiocpp_event_is_final(handle) != 0;

    /// <summary>
    /// The event's contents, read through the same accessors a result uses.
    /// Valid until this event is disposed.
    /// </summary>
    public AudioCppResult Result => _view;

    /// <summary>Voice-activity transitions carried by this event.</summary>
    public IReadOnlyList<VoiceActivityEvent> VoiceActivity
    {
        get
        {
            var count = (int)NativeMethods.audiocpp_event_voice_activity_count(handle);
            var events = new VoiceActivityEvent[count];
            for (var i = 0; i < count; i++)
            {
                AudioCppException.ThrowIfFailed(
                    NativeMethods.audiocpp_event_voice_activity(
                        handle, (nuint)i, out var kind, out var sample, out var probability),
                    nameof(NativeMethods.audiocpp_event_voice_activity));
                events[i] = new VoiceActivityEvent(kind, sample, probability);
            }
            return events;
        }
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        _view.SetHandleAsInvalid();
        NativeMethods.audiocpp_event_free(handle);
        return true;
    }
}
