namespace DiscordVoiceTranslator.Core.Audio;

/// <summary>
/// Convertit du 48 kHz stéréo entrelacé en 16 kHz mono (le format attendu par Whisper et
/// Silero VAD). 48 000 / 16 000 = 3 exactement : un filtre passe-bas puis on garde un
/// échantillon sur trois, sans interpolation ni dérive.
/// </summary>
public sealed class Downsampler48kTo16kMono
{
    private const int Factor = 3;

    // Coupure à 7,2 kHz : sous la limite de Nyquist de 8 kHz, pour que les aigus ne se
    // replient pas en bruit audible dans la bande de la voix.
    private static readonly float[] Taps = DesignLowPass(95, 7200.0 / 48000.0);

    private float[] _work = new float[8192];
    private float[] _output = new float[4096];
    private int _next;

    public Downsampler48kTo16kMono() => Reset();

    public void Reset()
    {
        Array.Clear(_work);
        _next = Taps.Length - 1;
    }

    /// <summary>Le span renvoyé est réutilisé à l'appel suivant.</summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> stereo)
    {
        int history = Taps.Length - 1;
        int frames = stereo.Length / 2;
        if (_work.Length < history + frames)
            Array.Resize(ref _work, history + frames);
        if (_output.Length < frames / Factor + 1)
            _output = new float[frames / Factor + 1];

        // _work = [historique des N-1 derniers échantillons] + [nouveaux échantillons mono]
        for (int i = 0; i < frames; i++)
            _work[history + i] = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);

        int end = history + frames;
        int produced = 0;
        for (; _next < end; _next += Factor)
        {
            float acc = 0;
            for (int j = 0; j < Taps.Length; j++)
                acc += Taps[j] * _work[_next - j];
            _output[produced++] = acc;
        }

        Array.Copy(_work, frames, _work, 0, history);
        _next -= frames;
        return _output.AsSpan(0, produced);
    }

    // Sinus cardinal fenêtré (Blackman), gain normalisé à 1.
    private static float[] DesignLowPass(int length, double cutoff)
    {
        var taps = new double[length];
        double mid = (length - 1) / 2.0;
        for (int i = 0; i < length; i++)
        {
            double x = i - mid;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1))
                                 + 0.08 * Math.Cos(4 * Math.PI * i / (length - 1));
            taps[i] = sinc * window;
        }
        double sum = taps.Sum();
        return taps.Select(t => (float)(t / sum)).ToArray();
    }
}
