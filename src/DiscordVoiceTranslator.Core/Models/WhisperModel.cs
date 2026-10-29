using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Models;

/// <summary>
/// Un modèle Whisper proposé dans les paramètres : rapide (par défaut) ou précis. Les fichiers
/// sont ceux de whisper.cpp sur Hugging Face, vérifiés par leur SHA-256 au téléchargement.
/// </summary>
public sealed record WhisperModel(string Id, ModelFile File, string EnglishName, string FrenchName, string EnglishDetails, string FrenchDetails)
{
    public const string DefaultId = "turbo";

    public string DisplayName => T(EnglishName, FrenchName);
    public string Details => T(EnglishDetails, FrenchDetails);

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new(DefaultId,
            new("ggml-large-v3-turbo-q8_0.bin", Url("ggml-large-v3-turbo-q8_0.bin"), 874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"),
            "Fast (recommended)", "Rapide (recommandé)",
            "Whisper large-v3-turbo · 830 MB · about 1.5 GB of video memory. Light on the PC, subtitles appear quickly.",
            "Whisper large-v3-turbo · 830 Mo · environ 1,5 Go de mémoire vidéo. Léger pour le PC, les sous-titres arrivent vite."),
        // large-v3 complet (32 couches de décodage contre 4 pour turbo), quantifié en 5 bits :
        // presque la qualité du fichier de 3 Go, avec deux fois moins de mémoire vidéo.
        new("large",
            new("ggml-large-v3-q5_0.bin", Url("ggml-large-v3-q5_0.bin"), 1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
            "Accurate", "Précis",
            "Whisper large-v3 · 1 GB · about 2 GB of video memory. Fewer mistakes (accents, noisy audio, short sentences), but each sentence takes longer and the graphics card works harder.",
            "Whisper large-v3 · 1 Go · environ 2 Go de mémoire vidéo. Moins d’erreurs (accents, son bruité, phrases courtes), mais chaque phrase prend plus de temps et la carte graphique travaille davantage."),
    ];

    public static WhisperModel Default => All.First(m => m.Id == DefaultId);

    public static WhisperModel Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? Default;

    private static string Url(string fileName) => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + fileName;
}
