using System.Diagnostics;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Audio;

/// <summary>
/// Source audio « Discord seul » qui se gère toute seule : attend que Discord soit lancé,
/// capte son arborescence de processus, et se reconnecte si Discord est fermé ou redémarre
/// (mise à jour, crash).
/// </summary>
public sealed class DiscordAudioSource : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly Downsampler48kTo16kMono _downsampler = new();
    private Thread? _supervisor;
    private string? _lastStatus;

    /// <summary>Flux brut : 48 kHz stéréo entrelacé.</summary>
    public event AudioDataHandler? RawData;

    /// <summary>Flux pour la reconnaissance vocale : 16 kHz mono.</summary>
    public event AudioDataHandler? Data16kMono;

    public event Action<string>? StatusChanged;

    public void Start()
    {
        _stopRequested.Reset();
        _supervisor = new Thread(Supervise) { IsBackground = true, Name = "Discord audio supervisor" };
        _supervisor.Start();
    }

    public void Stop()
    {
        _stopRequested.Set();
        _supervisor?.Join();
        _supervisor = null;
    }

    public void Dispose()
    {
        Stop();
        _stopRequested.Dispose();
    }

    private void Supervise()
    {
        while (!_stopRequested.IsSet)
        {
            var discord = DiscordProcessLocator.FindRoot();
            if (discord is null)
            {
                Report(T("Audio: Discord is not running, waiting...", "Audio : Discord n'est pas lancé, en attente..."));
                _stopRequested.Wait(RetryDelay);
                continue;
            }

            Process process;
            try { process = Process.GetProcessById(discord.ProcessId); }
            catch (ArgumentException) { continue; } // fermé entre-temps

            using (process)
            using (var capture = new ProcessLoopbackCapture((uint)discord.ProcessId))
            {
                Exception? fault = null;
                capture.DataAvailable += OnRawData;
                capture.Faulted += ex => fault = ex;

                try
                {
                    _downsampler.Reset();
                    capture.Start();
                }
                catch (Exception ex)
                {
                    Report(T($"Audio: capture failed to start: {ex.Message}", $"Audio : échec du démarrage de la capture : {ex.Message}"));
                    _stopRequested.Wait(RetryDelay);
                    continue;
                }

                Report(T($"Audio: capturing {discord.ExeName} (PID {discord.ProcessId}) and its child processes",
                         $"Audio : capture de {discord.ExeName} (PID {discord.ProcessId}) et de ses sous-processus"));

                while (!_stopRequested.IsSet && fault is null && !process.HasExited)
                    _stopRequested.Wait(500);

                if (fault is not null)
                    Report(T($"Audio: capture interrupted: {fault.Message}", $"Audio : capture interrompue : {fault.Message}"));
                else if (process.HasExited)
                    Report(T("Audio: Discord was closed", "Audio : Discord s'est fermé"));
            }
        }
    }

    private void OnRawData(ReadOnlySpan<float> stereo, long qpc)
    {
        RawData?.Invoke(stereo, qpc);
        var mono = _downsampler.Process(stereo);
        if (mono.Length > 0)
            Data16kMono?.Invoke(mono, qpc);
    }

    // Évite de répéter « en attente » toutes les 2 s.
    private void Report(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }
}
