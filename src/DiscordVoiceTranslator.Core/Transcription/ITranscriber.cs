using DiscordVoiceTranslator.Core.Vad;

namespace DiscordVoiceTranslator.Core.Transcription;

public sealed record TranscriptionResult(
    string Text,
    string Language,
    float LanguageProbability,
    SpeechSegment Segment,
    TimeSpan ProcessingTime,
    TimeSpan LanguageDetectionTime);

/// <summary>
/// Transcription d'une prise de parole. Whisper local aujourd'hui ; un fournisseur cloud
/// (API compatible OpenAI, Groq, Deepgram...) viendra derrière la même interface.
/// </summary>
public interface ITranscriber : IDisposable
{
    string Description { get; }

    /// <summary>
    /// Premier passage à vide, au chargement plutôt que sur la première vraie phrase
    /// (Vulkan compile ses shaders au premier calcul : plus de 10 s).
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken);

    /// <summary>Renvoie null si rien d'exploitable n'a été reconnu.</summary>
    Task<TranscriptionResult?> TranscribeAsync(SpeechSegment segment, CancellationToken cancellationToken);
}
