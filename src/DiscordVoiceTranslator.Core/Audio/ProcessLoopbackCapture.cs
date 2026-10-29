using System.Runtime.InteropServices;
using DiscordVoiceTranslator.Core.Audio.Interop;
using DiscordVoiceTranslator.Core.Timing;
using static DiscordVoiceTranslator.Core.Audio.Interop.CoreAudioInterop;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Audio;

/// <summary>
/// Échantillons audio et position QPC (en unités de 100 ns) du premier échantillon.
/// Le span n'est valable que pendant l'appel : le copier pour le garder.
/// </summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> samples, long qpcPosition100ns);

/// <summary>
/// Capte uniquement le son rendu par un processus et ses descendants (API « process
/// loopback », Windows 10 2004+ / Windows 11). Sortie : 48 kHz stéréo float entrelacé.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    // 100 ms de tampon côté Windows : large marge, sans effet sur la latence car on lit
    // dès que l'événement « paquet prêt » est signalé (~10 ms).
    private const long BufferDuration100ns = 100 * 10_000;

    private readonly uint _processId;
    private readonly AutoResetEvent _packetReady = new(false);
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _thread;
    private volatile bool _stopping;
    private short[] _pcm = new short[4096];
    private float[] _samples = new float[4096];

    public event AudioDataHandler? DataAvailable;

    /// <summary>Levé si la capture s'arrête sur une erreur (pas lors d'un Stop normal).</summary>
    public event Action<Exception>? Faulted;

    public ProcessLoopbackCapture(uint processId) => _processId = processId;

    public void Start()
    {
        _audioClient = Activate(_processId);

        // Process loopback ne connaît pas de « format de mixage » (GetMixFormat renvoie
        // E_NOTIMPL) : on impose le format voulu et Windows convertit (AUTOCONVERTPCM).
        var format = new WaveFormatEx
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = Channels * 2,
            nAvgBytesPerSec = SampleRate * Channels * 2,
        };
        _audioClient.Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
            BufferDuration100ns, 0, ref format, IntPtr.Zero);

        _audioClient.GetService(typeof(IAudioCaptureClient).GUID, out object service);
        _captureClient = (IAudioCaptureClient)service;
        _audioClient.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());

        _stopping = false;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Discord loopback capture",
            // Thread très léger mais qui ne doit jamais rater un paquet, même quand le jeu
            // sature le CPU (la priorité reste relative à la classe du processus).
            Priority = ThreadPriority.Highest,
        };
        _audioClient.Start();
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join();
        _thread = null;
        try { _audioClient?.Stop(); } catch (COMException) { /* client déjà invalidé */ }
    }

    public void Dispose()
    {
        Stop();
        if (_captureClient is not null) Marshal.ReleaseComObject(_captureClient);
        if (_audioClient is not null) Marshal.ReleaseComObject(_audioClient);
        _captureClient = null;
        _audioClient = null;
        _packetReady.Dispose();
    }

    private void CaptureLoop()
    {
        try
        {
            var capture = _captureClient!;
            while (!_stopping)
            {
                if (!_packetReady.WaitOne(200))
                    continue;

                capture.GetNextPacketSize(out uint packetFrames);
                while (packetFrames > 0)
                {
                    capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc);
                    int count = (int)frames * Channels;
                    EnsureCapacity(count);

                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                    {
                        Array.Clear(_samples, 0, count);
                    }
                    else
                    {
                        Marshal.Copy(data, _pcm, 0, count);
                        for (int i = 0; i < count; i++)
                            _samples[i] = _pcm[i] / 32768f;
                    }
                    capture.ReleaseBuffer(frames);

                    // Position QPC fournie par Windows ; à défaut, l'heure de réception (~10 ms plus tard).
                    long timestamp = qpc != 0 ? (long)qpc : QpcClock.Now100ns;
                    DataAvailable?.Invoke(_samples.AsSpan(0, count), timestamp);
                    capture.GetNextPacketSize(out packetFrames);
                }
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_samples.Length >= count) return;
        _pcm = new short[count];
        _samples = new float[count];
    }

    private static IAudioClient Activate(uint processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = processId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = paramsPtr },
                propVariantPtr, false);

            var handler = new ActivationHandler();
            var operation = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback, typeof(IAudioClient).GUID, propVariantPtr, handler);

            // Les paramètres doivent rester en mémoire jusqu'à la fin de l'activation.
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException(T("Audio capture activation did not answer within 5 s.", "L'activation de la capture audio n'a pas répondu en 5 s."));
            GC.KeepAlive(operation);
            return handler.Completed.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Completed => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activated);
                if (hr < 0)
                    _tcs.TrySetException(new COMException(T("Process loopback activation failed (Windows 11 required).", "Échec de l'activation process loopback (Windows 11 requis)."), hr));
                else
                    _tcs.TrySetResult((IAudioClient)activated);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
