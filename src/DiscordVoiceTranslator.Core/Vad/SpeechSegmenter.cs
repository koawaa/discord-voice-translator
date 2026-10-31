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

    /// <summary>
    /// Quelqu'un qui parle sans s'arrêter depuis plus longtemps : une simple respiration
    /// (<see cref="ShortSilenceMs"/>) suffit alors à finir la phrase. Un narrateur ou une
    /// personne qui enchaîne est ainsi sous-titré phrase par phrase, et non par blocs de 12 s.
    /// </summary>
    public int SoftMaxSegmentMs { get; init; } = 6_000;

    public int ShortSilenceMs { get; init; } = 250;

    /// <summary>Au-delà, on coupe pour afficher quelque chose sans attendre la fin d'un monologue.</summary>
    public int MaxSegmentMs { get; init; } = 12_000;

    /// <summary>Pendant que la personne parle, un aperçu de la phrase en cours à ce rythme (0 : jamais).</summary>
    public int ProgressIntervalMs { get; init; } = 1_000;

    /// <summary>
    /// Pas d'aperçu avant : sur une seconde de parole, Whisper se trompe souvent (« your
    /// intelligence » pour « your intel is a scam »), et une phrase courte arrive de toute
    /// façon juste après. Les aperçus ne servent qu'aux phrases longues.
    /// </summary>
    public int FirstProgressMs { get; init; } = 2_500;
}

/// <summary>Une prise de parole, en 16 kHz mono, horodatée sur <see cref="QpcClock"/>.</summary>
/// <param name="UtteranceId">
/// Numéro de la prise de parole : ses aperçus et sa version finale ont le même. Une phrase
/// coupée pour longueur continue sous un nouveau numéro.
/// </param>
public sealed record SpeechSegment(
    float[] Samples,
    long StartQpc,
    long SpeechStartQpc,
    long SpeechEndQpc,
    long ClosedQpc,
    bool CutAtMaxLength,
    long UtteranceId = 0)
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
    private readonly int _softMaxSegmentSamples, _shortSilenceSamples, _progressIntervalSamples, _firstProgressSamples;
    private readonly long _minSpeech100ns;
    private long _utteranceId;
    private int _sinceProgress;

    private readonly float[] _chunk = new float[Chunk];
    private int _chunkFill;
    private long _chunkStartQpc;

    // Où chercher le point de coupe d'un monologue trop long : les 2 dernières secondes.
    private const int CutSearchChunks = 2 * SileroVad.SampleRate / Chunk;

    private readonly Queue<(float[] Samples, long Qpc)> _preRoll = new();
    private readonly List<float> _segment = new();
    // Probabilité de parole de chaque bloc de _segment, pour couper au moment le plus calme.
    private readonly List<float> _probabilities = new();
    private bool _speaking;
    private long _segmentStartQpc, _firstSpeechQpc, _lastSpeechQpc;
    private int _lastSpeechIndex, _silenceSamples;

    /// <summary>Quelqu'un commence à parler (pour l'indicateur « en cours... »).</summary>
    public event Action<long>? SpeechStarted;

    /// <summary>La prise de parole était trop courte pour être transcrite.</summary>
    public event Action? SpeechDiscarded;

    public event Action<SpeechSegment>? SegmentReady;

    /// <summary>La phrase en cours jusqu'ici, pendant que la personne parle encore.</summary>
    public event Action<SpeechSegment>? SpeechProgress;

    public SpeechSegmenter(SileroVad vad, VadOptions options)
    {
        _vad = vad;
        _options = options;
        _preRollChunks = MsToSamples(options.PreRollMs) / Chunk;
        _minSilenceSamples = MsToSamples(options.MinSilenceMs);
        _tailPadSamples = MsToSamples(options.TailPadMs);
        _maxSegmentSamples = MsToSamples(options.MaxSegmentMs);
        _softMaxSegmentSamples = MsToSamples(options.SoftMaxSegmentMs);
        _shortSilenceSamples = Math.Min(MsToSamples(options.ShortSilenceMs), _minSilenceSamples);
        _progressIntervalSamples = MsToSamples(options.ProgressIntervalMs);
        _firstProgressSamples = MsToSamples(options.FirstProgressMs);
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
                _probabilities.Clear();
                _segmentStartQpc = _preRoll.Count > 0 ? _preRoll.Peek().Qpc : startQpc;
                foreach (var (samples, _) in _preRoll)
                {
                    _segment.AddRange(samples);
                    _probabilities.Add(0f);
                }
                _preRoll.Clear();
                _segment.AddRange(chunk);
                _probabilities.Add(probability);
                _speaking = true;
                _utteranceId++;
                _sinceProgress = 0;
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

        _probabilities.Add(probability);

        int silenceNeeded = _segment.Count >= _softMaxSegmentSamples ? _shortSilenceSamples : _minSilenceSamples;
        if (_silenceSamples >= silenceNeeded)
            Close(endQpc);
        else if (_segment.Count >= _maxSegmentSamples)
            CutAtQuietestPoint();
        else
            ReportProgress(endQpc);
    }

    /// <summary>
    /// Monologue trop long : on coupe au moment le plus calme des dernières secondes (le blanc
    /// entre deux mots), pas pile à la limite, en plein mot. La suite repart aussitôt comme une
    /// nouvelle prise de parole déjà commencée.
    /// </summary>
    private void CutAtQuietestPoint()
    {
        int from = Math.Max(1, _probabilities.Count - CutSearchChunks);
        int quietest = from;
        for (int j = from + 1; j < _probabilities.Count; j++)
        {
            if (_probabilities[j] < _probabilities[quietest])
                quietest = j;
        }

        int keep = (quietest + 1) * Chunk;
        long cutQpc = _segmentStartQpc + keep * TicksPerSample;
        SegmentReady?.Invoke(new SpeechSegment(_segment.GetRange(0, keep).ToArray(), _segmentStartQpc, _firstSpeechQpc,
            cutQpc, QpcClock.Now100ns, CutAtMaxLength: true, _utteranceId));

        _segment.RemoveRange(0, keep);
        _probabilities.RemoveRange(0, quietest + 1);
        _lastSpeechIndex = Math.Max(0, _lastSpeechIndex - keep);
        _segmentStartQpc = _firstSpeechQpc = cutQpc;
        _utteranceId++;
        _sinceProgress = 0;
    }

    private void ReportProgress(long endQpc)
    {
        if (_progressIntervalSamples == 0 || SpeechProgress is null)
            return;
        _sinceProgress += Chunk;
        if (_sinceProgress < _progressIntervalSamples || _segment.Count < _firstProgressSamples)
            return;
        _sinceProgress = 0;
        SpeechProgress(new SpeechSegment(_segment.ToArray(), _segmentStartQpc, _firstSpeechQpc, endQpc, QpcClock.Now100ns, false, _utteranceId));
    }

    private void MarkSpeech(long endQpc)
    {
        _silenceSamples = 0;
        _lastSpeechIndex = _segment.Count;
        _lastSpeechQpc = endQpc;
    }

    /// <summary>Fin de la prise de parole (assez de silence) : envoyée, ou jetée si trop courte.</summary>
    private void Close(long endQpc)
    {
        int keep = Math.Min(_segment.Count, _lastSpeechIndex + _tailPadSamples);
        if (_lastSpeechQpc - _firstSpeechQpc >= _minSpeech100ns)
        {
            var samples = _segment.GetRange(0, keep).ToArray();
            SegmentReady?.Invoke(new SpeechSegment(samples, _segmentStartQpc, _firstSpeechQpc,
                _lastSpeechQpc, QpcClock.Now100ns, CutAtMaxLength: false, _utteranceId));
        }
        else
        {
            SpeechDiscarded?.Invoke();
        }

        _segment.Clear();
        _probabilities.Clear();
        _silenceSamples = 0;
        _speaking = false;
    }

    private static int MsToSamples(int ms) => ms * SileroVad.SampleRate / 1000;
}
