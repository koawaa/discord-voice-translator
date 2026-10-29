using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Models;

/// <summary>
/// Un modèle Whisper proposé dans les paramètres, du plus léger au plus précis. Les fichiers
/// sont ceux de whisper.cpp sur Hugging Face, vérifiés par leur SHA-256 au téléchargement.
/// </summary>
public sealed record WhisperModel(string Id, ModelFile File, string EnglishName, string FrenchName, string EnglishDetails, string FrenchDetails)
{
    public const string DefaultId = "turbo";

    public string DisplayName => T(EnglishName, FrenchName);
    public string Details => T(EnglishDetails, FrenchDetails);

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("small",
            new("ggml-small-q8_0.bin", Url("ggml-small-q8_0.bin"), 264_464_607, "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f"),
            "Light", "Léger",
            "small · 250 MB · for a modest PC or no graphics card. Fast, but more mistakes.",
            "small · 250 Mo · pour un PC modeste ou sans carte graphique. Rapide, mais plus d’erreurs."),
        new(DefaultId,
            new("ggml-large-v3-turbo-q8_0.bin", Url("ggml-large-v3-turbo-q8_0.bin"), 874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"),
            "Balanced (recommended)", "Équilibré (recommandé)",
            "large-v3-turbo · 830 MB · about 1.5 GB of video memory. Accurate and fast.",
            "large-v3-turbo · 830 Mo · environ 1,5 Go de mémoire vidéo. Précis et rapide."),
        new("large-q5",
            new("ggml-large-v3-q5_0.bin", Url("ggml-large-v3-q5_0.bin"), 1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
            "Accurate", "Précis",
            "large-v3 · 1 GB · about 2 GB of video memory. Better on accents and noisy audio, but each sentence takes several times longer.",
            "large-v3 · 1 Go · environ 2 Go de mémoire vidéo. Meilleur sur les accents et le son bruité, mais chaque phrase prend plusieurs fois plus de temps."),
        new("large",
            new("ggml-large-v3.bin", Url("ggml-large-v3.bin"), 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
            "Maximum", "Maximum",
            "large-v3, full precision · 2.9 GB · about 4 GB of video memory. The most accurate, for a powerful graphics card.",
            "large-v3 en pleine précision · 2,9 Go · environ 4 Go de mémoire vidéo. Le plus précis, pour une carte graphique puissante."),
    ];

    public static WhisperModel Default => All.First(m => m.Id == DefaultId);

    public static WhisperModel Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? Default;

    private static string Url(string fileName) => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + fileName;
}
