using System.Threading.Channels;
using DiscordVoiceTranslator.Core.Transcription;
using DiscordVoiceTranslator.Core.Vad;

namespace DiscordVoiceTranslator.Core.Pipeline;

/// <summary>
/// Audio 16 kHz → VAD → transcription, chacun sur son propre fil pour que le thread de
/// capture ne fasse que copier. Le GPU ne travaille que quand un segment de parole arrive.
/// </summary>
public sealed class TranscriptionPipeline : IAsyncDisposable
{
    private readonly SpeechSegmenter _segmenter;
    private readonly ITranscriber _transcriber;
    private readonly Channel<(float[] Samples, long Qpc)> _audio =
        Channel.CreateUnbounded<(float[], long)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<SpeechSegment> _segments =
        Channel.CreateUnbounded<SpeechSegment>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _progressSignal = new(0);
    private SpeechSegment? _latestProgress;
    private Task? _vadTask, _transcriptionTask, _progressTask;
    private int _pending;

    public event Action<long>? SpeechStarted;
    public event Action? SpeechDiscarded;
    public event Action<SpeechSegment>? SegmentQueued;
    public event Action<TranscriptionResult>? Transcribed;

    /// <summary>Aperçu de la phrase en cours (voir <see cref="LiveDrafts"/>).</summary>
    public event Action<TranscriptionResult>? DraftTranscribed;

    /// <summary>Une prise de parole terminée n'a rien donné : son aperçu éventuel doit disparaître.</summary>
    public event Action<SpeechSegment>? NothingRecognized;

    /// <summary>
    /// Transcrire aussi la phrase pendant qu'elle est prononcée, environ chaque seconde. Les
    /// phrases finies passent toujours avant : aucun aperçu n'est fait tant qu'il y en a une en attente.
    /// </summary>
    public volatile bool LiveDrafts;

    /// <summary>Un segment a fini d'être traité, qu'il ait donné du texte ou non.</summary>
    public event Action? SegmentProcessed;

    public event Action<Exception>? Error;

    /// <summary>Segments en attente ou en cours de transcription.</summary>
    public int PendingSegments => Volatile.Read(ref _pending);

    public TranscriptionPipeline(SpeechSegmenter segmenter, ITranscriber transcriber)
    {
        _segmenter = segmenter;
        _transcriber = transcriber;
        _segmenter.SpeechStarted += qpc => SpeechStarted?.Invoke(qpc);
        _segmenter.SpeechDiscarded += () => SpeechDiscarded?.Invoke();
        _segmenter.SegmentReady += segment =>
        {
            Interlocked.Increment(ref _pending);
            _segments.Writer.TryWrite(segment);
            SegmentQueued?.Invoke(segment);
        };
        _segmenter.SpeechProgress += segment =>
        {
            if (!LiveDrafts)
                return;
            // Seul le plus récent compte : un aperçu pas encore traité est remplacé.
            Interlocked.Exchange(ref _latestProgress, segment);
            _progressSignal.Release();
        };
    }

    public void Start()
    {
        _vadTask = Task.Run(VadLoop);
        _transcriptionTask = Task.Run(TranscriptionLoop);
        _progressTask = Task.Run(DraftLoop);
    }

    /// <summary>Appelé depuis le thread de capture : copie et rend la main aussitôt.</summary>
    public void PushAudio(ReadOnlySpan<float> samples, long firstSampleQpc) =>
        _audio.Writer.TryWrite((samples.ToArray(), firstSampleQpc));

    private async Task VadLoop()
    {
        try
        {
            await foreach (var (samples, qpc) in _audio.Reader.ReadAllAsync(_cts.Token))
                _segmenter.Feed(samples, qpc);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    private async Task TranscriptionLoop()
    {
        try
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(_cts.Token))
            {
                TranscriptionResult? result = null;
                try
                {
                    result = await _transcriber.TranscribeAsync(segment, _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error?.Invoke(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
                // Après la décrémentation : les abonnés voient déjà le bon nombre de segments en attente.
                if (result is not null)
                    Transcribed?.Invoke(result);
                else
                    NothingRecognized?.Invoke(segment);
                SegmentProcessed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DraftLoop()
    {
        try
        {
            while (true)
            {
                await _progressSignal.WaitAsync(_cts.Token);
                var segment = Interlocked.Exchange(ref _latestProgress, null);
                // Déjà pris par un signal précédent, ou une phrase finie attend : elle passe avant.
                if (segment is null || !LiveDrafts || PendingSegments > 0)
                    continue;
                try
                {
                    if (await _transcriber.TranscribeAsync(segment, _cts.Token) is { } result)
                        DraftTranscribed?.Invoke(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _audio.Writer.TryComplete();
        _segments.Writer.TryComplete();
        _cts.Cancel();
        if (_vadTask is not null) await _vadTask;
        if (_transcriptionTask is not null) await _transcriptionTask;
        if (_progressTask is not null) await _progressTask;
        _cts.Dispose();
        _progressSignal.Dispose();
    }
}
