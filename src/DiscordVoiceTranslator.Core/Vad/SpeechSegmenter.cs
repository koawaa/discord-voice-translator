using DiscordVoiceTranslator.Core.Timing;

namespace DiscordVoiceTranslator.Core.Vad;

public sealed record VadOptions
{
    /// <summary>Probabilité à partir de laquelle on considère que quelqu'un parle.</summary>
    public float Threshold { get; init; } = 0.5f;

    /// <summary>Probabilité sous laquelle on compte du silence (hystérésis, comme Silero).</summary>
    public float NegThreshold { get; init; } = 0.35f;

    /// <summary>Silence qui marque la fin d'une phrase. Compte directement dans la latence.</summary>
    public int MinSilenceMs { get; init; } = 500;

    /// <summary>Audio gardé avant le déclenchement, pour ne pas couper le début des phrases.</summary>
    public int PreRollMs { get; init; } = 300;

    /// <summary>Silence gardé après la dernière parole (évite de couper la dernière syllabe).</summary>
    public int TailPadMs { get; init; } = 100;

    /// <summary>En dessous, c'est un bruit (toux, clic) : le segment est jeté.</summary>
    public int MinSpeechMs { get; init; } = 250;

    /// <summary>Au-delà, on coupe pour afficher quelque chose sans attendre la fin d'un monologue.</summary>
    public int MaxSegmentMs { get; init; } = 12_000;
}

/// <summary>Une prise de parole, en 16 kHz mono, horodatée sur <see cref="QpcClock"/>.</summary>
public sealed record SpeechSegment(
    float[] Samples,
    long StartQpc,
    long SpeechStartQpc,
    long SpeechEndQpc,
    long ClosedQpc,
    bool CutAtMaxLength)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SileroVad.SampleRate);
}

/// <summary>
/// Découpe le flux 16 kHz en prises de parole avec Silero. N'est pas thread-safe :
/// <see cref="Feed"/> doit toujours être appelé depuis le même thread.
/// </summary>
public sealed class SpeechSegmenter
{
    private const int Chunk = SileroVad.ChunkSamples;
    // 10 000 000 / 16 000 = 625 unités de 100 ns par échantillon, pile.
    private const long TicksPerSample = QpcClock.TicksPerSecond / SileroVad.SampleRate;

    private readonly SileroVad _vad;
    private readonly VadOptions _options;
    private readonly int _preRollChunks, _minSilenceSamples, _tailPadSamples, _maxSegmentSamples;
    private readonly long _minSpeech100ns;

    private readonly float[] _chunk = new float[Chunk];
    private int _chunkFill;
    private long _chunkStartQpc;

    private readonly Queue<(float[] Samples, long Qpc)> _preRoll = new();
    private readonly List<float> _segment = new();
    private bool _speaking;
    private long _segmentStartQpc, _firstSpeechQpc, _lastSpeechQpc;
    private int _lastSpeechIndex, _silenceSamples;

    /// <summary>Quelqu'un commence à parler (pour l'indicateur « en cours... »).</summary>
    public event Action<long>? SpeechStarted;

    /// <summary>La prise de parole était trop courte pour être transcrite.</summary>
    public event Action? SpeechDiscarded;

    public event Action<SpeechSegment>? SegmentReady;

    public SpeechSegmenter(SileroVad vad, VadOptions options)
    {
        _vad = vad;
        _options = options;
        _preRollChunks = MsToSamples(options.PreRollMs) / Chunk;
        _minSilenceSamples = MsToSamples(options.MinSilenceMs);
        _tailPadSamples = MsToSamples(options.TailPadMs);
        _maxSegmentSamples = MsToSamples(options.MaxSegmentMs);
        _minSpeech100ns = options.MinSpeechMs * 10_000L;
    }

    public void Feed(ReadOnlySpan<float> samples, long firstSampleQpc)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            if (_chunkFill == 0)
                _chunkStartQpc = firstSampleQpc + i * TicksPerSample;
            _chunk[_chunkFill++] = samples[i];
            if (_chunkFill == Chunk)
            {
                ProcessChunk(_chunk, _chunkStartQpc);
                _chunkFill = 0;
            }
        }
    }

    private void ProcessChunk(float[] chunk, long startQpc)
    {
        float probability = _vad.Process(chunk);
        long endQpc = startQpc + Chunk * TicksPerSample;

        if (!_speaking)
        {
            if (probability >= _options.Threshold)
            {
                _segment.Clear();
                _segmentStartQpc = _preRoll.Count > 0 ? _preRoll.Peek().Qpc : startQpc;
                foreach (var (samples, _) in _preRoll)
                    _segment.AddRange(samples);
                _preRoll.Clear();
                _segment.AddRange(chunk);
                _speaking = true;
                _firstSpeechQpc = startQpc;
                MarkSpeech(endQpc);
                SpeechStarted?.Invoke(startQpc);
            }
            else
            {
                _preRoll.Enqueue(((float[])chunk.Clone(), startQpc));
                while (_preRoll.Count > _preRollChunks)
                    _preRoll.Dequeue();
            }
            return;
        }

        _segment.AddRange(chunk);
        if (probability >= _options.Threshold)
            MarkSpeech(endQpc);
        else if (probability < _options.NegThreshold || _silenceSamples > 0)
            _silenceSamples += Chunk;

        if (_silenceSamples >= _minSilenceSamples)
            Close(cut: false, endQpc);
        else if (_segment.Count >= _maxSegmentSamples)
            Close(cut: true, endQpc);
    }

    private void MarkSpeech(long endQpc)
    {
        _silenceSamples = 0;
        _lastSpeechIndex = _segment.Count;
        _lastSpeechQpc = endQpc;
    }

    private void Close(bool cut, long endQpc)
    {
        int keep = cut ? _segment.Count : Math.Min(_segment.Count, _lastSpeechIndex + _tailPadSamples);
        if (_lastSpeechQpc - _firstSpeechQpc >= _minSpeech100ns)
        {
            var samples = _segment.GetRange(0, keep).ToArray();
            SegmentReady?.Invoke(new SpeechSegment(samples, _segmentStartQpc, _firstSpeechQpc,
                cut ? endQpc : _lastSpeechQpc, QpcClock.Now100ns, cut));
        }
        else
        {
            SpeechDiscarded?.Invoke();
        }

        _segment.Clear();
        _silenceSamples = 0;
        // Coupé pour longueur : la personne parle encore, le segment suivant démarre tout de suite.
        _speaking = cut;
        if (cut)
        {
            _segmentStartQpc = _firstSpeechQpc = _lastSpeechQpc = endQpc;
            _lastSpeechIndex = 0;
        }
    }

    private static int MsToSamples(int ms) => ms * SileroVad.SampleRate / 1000;
}
