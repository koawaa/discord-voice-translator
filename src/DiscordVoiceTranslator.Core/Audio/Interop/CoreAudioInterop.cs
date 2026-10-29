using System.Runtime.InteropServices;

namespace DiscordVoiceTranslator.Core.Audio.Interop;

// Déclarations minimales de l'API Windows Core Audio (WASAPI) nécessaires à la capture
// « process loopback ». Référence : exemple Microsoft « ApplicationLoopback ».

internal static class CoreAudioInterop
{
    // Périphérique virtuel qui, activé avec des paramètres PROCESS_LOOPBACK, ne mixe que
    // le son produit par un processus (et ses descendants).
    public const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

    public const ushort VT_BLOB = 65;
    public const ushort WAVE_FORMAT_PCM = 1;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern IActivateAudioInterfaceAsyncOperation ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    // AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, aplati
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

// PROPVARIANT réduit au cas VT_BLOB (disposition x64 : le BLOB commence à l'octet 8,
// son pointeur est aligné sur 8).
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlob
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public uint cbSize;
    [FieldOffset(16)] public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

// Marqueur exigé par ActivateAudioInterfaceAsync : le handler doit être « agile »
// (appelable depuis n'importe quel thread), sinon l'appel échoue.
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
        [In] ref WaveFormatEx format, IntPtr audioSessionGuid);
    void GetBufferSize(out uint bufferFrameCount);
    void GetStreamLatency(out long hnsLatency);
    void GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, [In] ref WaveFormatEx format, out IntPtr closestMatch);
    void GetMixFormat(out IntPtr deviceFormat);
    void GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint numFramesRead);
    void GetNextPacketSize(out uint numFramesInNextPacket);
}
