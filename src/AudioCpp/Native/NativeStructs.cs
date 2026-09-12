using System.Runtime.InteropServices;

namespace AudioCpp.Native;

/// <summary>Mirrors <c>audiocpp_backend_config</c>. Fields are UTF-8 pointers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeBackendConfig
{
    public IntPtr Backend;
    public int Device;
    public int Threads;
}

/// <summary>Mirrors <c>audiocpp_model_config</c>. Fields are UTF-8 pointers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeModelConfig
{
    public IntPtr FamilyHint;
    public IntPtr ConfigId;
    public IntPtr WeightId;
    public IntPtr ModelSpecOverride;
}
