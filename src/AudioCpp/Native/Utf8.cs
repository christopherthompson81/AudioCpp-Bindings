using System.Runtime.InteropServices;
using System.Text;

namespace AudioCpp.Native;

/// <summary>UTF-8 marshalling for the places the source generator cannot reach.</summary>
internal static class Utf8
{
    /// <summary>
    /// Copies a borrowed native string. Every <c>const char *</c> the ABI
    /// returns belongs to the handle that produced it, so it has to be copied
    /// on the way out rather than held.
    /// </summary>
    internal static string ToString(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    /// <summary>
    /// Allocates a NUL-terminated UTF-8 copy for a struct field. Struct fields
    /// cannot use the generated string marshalling, so these are allocated and
    /// released explicitly around the call that reads them.
    /// </summary>
    internal static IntPtr Allocate(string? value)
    {
        if (value is null) return IntPtr.Zero;
        var bytes = Encoding.UTF8.GetBytes(value);
        var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        Marshal.WriteByte(buffer, bytes.Length, 0);
        return buffer;
    }

    internal static void Free(IntPtr value)
    {
        if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
    }
}
