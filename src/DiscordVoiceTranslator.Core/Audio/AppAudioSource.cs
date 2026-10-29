using System.Diagnostics;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Audio;

/// <summary>
/// Source audio « une seule appli » (Discord, un jeu...) qui se gère toute seule : attend
/// que l'appli soit lancée, capte son arborescence de processus, et se reconnecte si elle
/// est fermée ou redémarre (mise à jour, crash, nouvelle partie).
/// </summary>
public sealed class AppAudioSource : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _name;
    private readonly string[] _exeNames;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly Downsampler48kTo16kMono _downsampler = new();
    private Thread? _supervisor;
    private string? _lastStatus;

    /// <summary>Flux pour la reconnaissance vocale : 16 kHz mono.</summary>
    public event AudioDataHandler? Data16kMono;

    public event Action<string>? StatusChanged;

    /// <param name="name">Nom affiché (« Discord », « War Dogs »...).</param>
    /// <param name="exeNames">Exécutables acceptés, par ordre de préférence.</param>
    public AppAudioSource(string name, IReadOnlyList<string> exeNames)
    {
        _name = name;
        _exeNames = exeNames.ToArray();
    }

    public static AppAudioSource Discord() => new("Discord", ProcessLocator.DiscordExeNames);

    public void Start()
    {
        _stopRequested.Reset();
        _supervisor = new Thread(Supervise) { IsBackground = true, Name = $"{_name} audio supervisor" };
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
            var app = ProcessLocator.FindRoot(_exeNames);
            if (app is null)
            {
                Report(T($"Audio {_name}: not running, waiting...", $"Audio {_name} : pas lancé, en attente..."));
                _stopRequested.Wait(RetryDelay);
                continue;
            }

            Process process;
            try { process = Process.GetProcessById(app.ProcessId); }
            catch (ArgumentException) { continue; } // fermé entre-temps

            using (process)
            using (var capture = new ProcessLoopbackCapture((uint)app.ProcessId))
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
                    Report(T($"Audio {_name}: capture failed to start: {ex.Message}", $"Audio {_name} : échec du démarrage de la capture : {ex.Message}"));
                    _stopRequested.Wait(RetryDelay);
                    continue;
                }

                Report(T($"Audio {_name}: capturing {app.ExeName} (PID {app.ProcessId}) and its child processes",
                         $"Audio {_name} : capture de {app.ExeName} (PID {app.ProcessId}) et de ses sous-processus"));

                while (!_stopRequested.IsSet && fault is null && !process.HasExited)
                    _stopRequested.Wait(500);

                if (fault is not null)
                    Report(T($"Audio {_name}: capture interrupted: {fault.Message}", $"Audio {_name} : capture interrompue : {fault.Message}"));
                else if (process.HasExited)
                    Report(T($"Audio {_name}: closed", $"Audio {_name} : fermé"));
            }
        }
    }

    private void OnRawData(ReadOnlySpan<float> stereo, long qpc)
    {
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
