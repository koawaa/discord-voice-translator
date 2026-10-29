using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DiscordVoiceTranslator.Core.Vad;

/// <summary>
/// Détecteur de voix Silero (modèle ONNX, CPU, moins d'1 ms par bloc). Renvoie la
/// probabilité qu'un bloc de 32 ms contienne de la parole. Le modèle est récurrent : il garde
/// un état et les 64 derniers échantillons du bloc précédent comme contexte, exactement
/// comme le wrapper Python officiel (OnnxWrapper de silero-vad).
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;
    public const int ChunkSamples = 512;
    private const int ContextSamples = 64;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[ContextSamples + ChunkSamples];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly DenseTensor<long> _sampleRate;

    public SileroVad(string modelPath)
    {
        // Un seul thread : le travail est minuscule, et on ne veut pas réveiller plusieurs
        // cœurs 31 fois par seconde pendant que le jeu tourne.
        using var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);

        // Selon la version exportée, « sr » est un scalaire ou un vecteur [1].
        int[] srDims = _session.InputMetadata["sr"].Dimensions.Select(d => Math.Max(d, 1)).ToArray();
        _sampleRate = new DenseTensor<long>(new long[] { SampleRate }, srDims);
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_state);
    }

    public float Process(ReadOnlySpan<float> chunk)
    {
        if (chunk.Length != ChunkSamples)
            throw new ArgumentException($"Silero attend des blocs de {ChunkSamples} échantillons.", nameof(chunk));

        chunk.CopyTo(_input.AsSpan(ContextSamples));
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, _input.Length])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", _sampleRate),
        };

        float probability;
        using (var results = _session.Run(inputs))
        {
            probability = results[0].AsTensor<float>().GetValue(0);
            int i = 0;
            foreach (float v in results[1].AsTensor<float>())
                _state[i++] = v;
        }

        // Les 64 derniers échantillons deviennent le contexte du bloc suivant.
        _input.AsSpan(ChunkSamples, ContextSamples).CopyTo(_input);
        return probability;
    }

    public void Dispose() => _session.Dispose();
}
