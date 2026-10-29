// Étape 2 : VAD + Whisper sur l'audio de Discord, résultat en console avec la langue
// détectée, la latence et la VRAM.
// Usage :
//   TranscribeTest [--seconds N]       écoute Discord en direct (Ctrl+C pour arrêter)
//   TranscribeTest --file test.wav     rejoue un WAV 16 kHz mono au rythme réel
// Options : --cpu   --langs en,fr,es,de   --model chemin\vers\ggml-*.bin   --runtime cuda|vulkan|cpu

using System.Diagnostics;
using DiscordVoiceTranslator.Core.Audio;
using DiscordVoiceTranslator.Core.Diagnostics;
using DiscordVoiceTranslator.Core.Pipeline;
using DiscordVoiceTranslator.Core.Timing;
using DiscordVoiceTranslator.Core.Transcription;
using DiscordVoiceTranslator.Core.Vad;
using NAudio.Wave;
using Whisper.net.LibraryLoader;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.WriteLine($"[erreur fatale] {e.ExceptionObject}");
    PauseIfInteractive();
};

string? filePath = ArgValue("--file");
bool useCpu = args.Contains("--cpu");
string[] languages = (ArgValue("--langs") ?? "en,fr,es,de").Split(',', StringSplitOptions.RemoveEmptyEntries);
string modelsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordVoiceTranslator", "models");
string modelPath = ArgValue("--model") ?? Path.Combine(modelsDir, "ggml-large-v3-turbo-q8_0.bin");
string vadPath = Path.Combine(modelsDir, "silero_vad.onnx");

if (ArgValue("--runtime") is string runtime)
    RuntimeOptions.RuntimeLibraryOrder = [Enum.Parse<RuntimeLibrary>(runtime, ignoreCase: true)];

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine($"VRAM avant chargement : {FormatVram()}");
var loadClock = Stopwatch.StartNew();
using var vad = new SileroVad(vadPath);
using var transcriber = new WhisperTranscriber(new WhisperOptions
{
    ModelPath = modelPath,
    UseGpu = !useCpu,
    AllowedLanguages = languages,
});
Console.WriteLine($"{transcriber.Description} chargé en {loadClock.Elapsed.TotalSeconds:0.0} s");
loadClock.Restart();
await transcriber.WarmUpAsync(CancellationToken.None);
Console.WriteLine($"Préchauffage : {loadClock.Elapsed.TotalSeconds:0.0} s");
Console.WriteLine($"VRAM après chargement : {FormatVram()}");
Console.WriteLine($"Langues autorisées : {string.Join(", ", languages)}");
Console.WriteLine();

var segmenter = new SpeechSegmenter(vad, new VadOptions());
await using var pipeline = new TranscriptionPipeline(segmenter, transcriber);

var latencies = new List<double>();
var processing = new List<double>();
pipeline.SpeechStarted += _ => Console.WriteLine("  ... parole détectée");
pipeline.SpeechDiscarded += () => Console.WriteLine("  ... trop court, ignoré");
pipeline.SegmentQueued += s => Console.WriteLine($"  ... segment de {s.Duration.TotalSeconds:0.0} s envoyé à Whisper{(s.CutAtMaxLength ? " (coupé, la personne parle encore)" : "")}");
pipeline.Error += ex => Console.WriteLine($"  [erreur] {ex.GetType().Name} : {ex.Message}");
pipeline.Transcribed += r =>
{
    // Latence ressentie : de la fin réelle de la phrase jusqu'au texte disponible
    // (inclut le silence d'attente qui confirme la fin de phrase).
    double latency = QpcClock.ToMilliseconds(QpcClock.Now100ns - r.Segment.SpeechEndQpc);
    lock (latencies)
    {
        latencies.Add(latency);
        processing.Add(r.ProcessingTime.TotalMilliseconds);
    }
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {r.Language} ({r.LanguageProbability:P0}) | fin de phrase → texte {latency:0} ms (Whisper {r.ProcessingTime.TotalMilliseconds:0} ms dont langue {r.LanguageDetectionTime.TotalMilliseconds:0}) | VRAM {FormatVram()}");
    Console.WriteLine($"    « {r.Text} »");
};

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
pipeline.Start();

if (filePath is null)
{
    using var source = new DiscordAudioSource();
    source.StatusChanged += msg => Console.WriteLine($"[statut] {msg}");
    source.Data16kMono += pipeline.PushAudio;
    source.Start();
    Console.WriteLine("Écoute de Discord en cours, Ctrl+C pour arrêter.");
    int seconds = int.TryParse(ArgValue("--seconds"), out var s) ? s : Timeout.Infinite;
    try { await Task.Delay(seconds == Timeout.Infinite ? Timeout.Infinite : seconds * 1000, stop.Token); }
    catch (OperationCanceledException) { }
    source.Stop();
}
else
{
    await PlayFileAsync(filePath, pipeline, stop.Token);
}

Console.WriteLine();
lock (latencies)
{
    if (latencies.Count > 0)
        Console.WriteLine($"{latencies.Count} phrases | latence moyenne {latencies.Average():0} ms (max {latencies.Max():0}) | Whisper moyen {processing.Average():0} ms");
}
PauseIfInteractive();

// Lancé d'un double-clic, la fenêtre se fermerait avant qu'on ait pu lire le bilan.
void PauseIfInteractive()
{
    if (Console.IsInputRedirected) return;
    Console.WriteLine();
    Console.WriteLine("Appuie sur Entrée pour fermer.");
    Console.ReadLine();
}

static async Task PlayFileAsync(string path, TranscriptionPipeline pipeline, CancellationToken token)
{
    using var reader = new WaveFileReader(path);
    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
        throw new InvalidOperationException("Le fichier doit être en 16 kHz mono.");
    var samples = reader.ToSampleProvider();
    Console.WriteLine($"Lecture de {Path.GetFileName(path)} ({reader.TotalTime.TotalSeconds:0.0} s) au rythme réel...");

    // Blocs de 32 ms, cadencés sur l'horloge réelle comme le ferait la capture.
    var buffer = new float[512];
    var clock = Stopwatch.StartNew();
    long fed = 0;
    int read;
    while (!token.IsCancellationRequested && (read = samples.Read(buffer, 0, buffer.Length)) > 0)
    {
        pipeline.PushAudio(buffer.AsSpan(0, read), QpcClock.Now100ns);
        fed += read;
        var due = TimeSpan.FromSeconds(fed / 16000.0) - clock.Elapsed;
        if (due > TimeSpan.Zero) await Task.Delay(due);
    }

    // 1 s de silence pour clore la dernière phrase, puis on attend les transcriptions.
    Array.Clear(buffer);
    for (int i = 0; i < 32; i++)
    {
        pipeline.PushAudio(buffer, QpcClock.Now100ns);
        await Task.Delay(32);
    }
    while (pipeline.PendingSegments > 0 && !token.IsCancellationRequested)
        await Task.Delay(50);
}

string FormatVram() => GpuMemory.DedicatedBytesForCurrentProcess() is long bytes ? $"{bytes / 1024.0 / 1024:0} Mo" : "indisponible";

string? ArgValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
