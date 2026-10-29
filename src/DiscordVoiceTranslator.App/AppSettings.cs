using System.IO;
using System.Text.Json;
using DiscordVoiceTranslator.Core.Config;
using DiscordVoiceTranslator.Core.Models;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Réglages non secrets, en JSON lisible dans %APPDATA%\DiscordVoiceTranslator\settings.json.
/// Les secrets (clé DeepL, application Discord, jeton) sont à part, chiffrés (SecretStore).
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordVoiceTranslator", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>« en » ou « fr ». Anglais par défaut.</summary>
    public string UiLanguage { get; set; } = "en";

    public OverlaySettings Overlay { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();

    /// <summary>Faux au tout premier lancement : on en profite pour ouvrir les paramètres.</summary>
    public static bool Exists => File.Exists(FilePath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Fichier abîmé : on repart des valeurs par défaut plutôt que de bloquer le démarrage.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Écrit à côté puis remplace : une coupure en pleine écriture ne laisse pas un fichier tronqué.
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

internal sealed class OverlaySettings
{
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 20;

    /// <summary>Opacité du fond derrière chaque ligne, de 0 (invisible) à 1 (noir plein).</summary>
    public double BackgroundOpacity { get; set; } = 0.55;

    public int MaxLines { get; set; } = 4;
    public double DisplaySeconds { get; set; } = 10;
    public bool ShowOriginal { get; set; } = true;

    // Null : centré en bas de l'écran principal.
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 320;
}

internal sealed class TranslationSettings
{
    public const string DeepLSecretName = "deepl";

    public bool Enabled { get; set; } = true;
    public string TargetLanguage { get; set; } = "EN-US";

    /// <summary>
    /// Clés DeepL dans l'ordre d'utilisation, une par ligne dans le même secret chiffré
    /// (un ancien secret à une seule clé se relit donc tel quel).
    /// </summary>
    public static List<string> LoadDeepLKeys() =>
        SecretStore.Load(DeepLSecretName)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

    public static void SaveDeepLKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count > 0)
            SecretStore.Save(DeepLSecretName, string.Join('\n', keys));
        else if (SecretStore.Load(DeepLSecretName) is not null)
            SecretStore.Delete(DeepLSecretName);
    }
}

internal sealed class TranscriptionSettings
{
    /// <summary>Modèle Whisper choisi (<see cref="WhisperModel.Id"/>) : rapide ou précis.</summary>
    public string Model { get; set; } = WhisperModel.DefaultId;

    /// <summary>Langues que Whisper a le droit de détecter (codes ISO à deux lettres).</summary>
    public List<string> AllowedLanguages { get; set; } = ["en", "fr", "es", "de"];

    /// <summary>Silence qui marque la fin d'une phrase : compte directement dans la latence.</summary>
    public int EndOfSentenceSilenceMs { get; set; } = 500;

    public bool UseGpu { get; set; } = true;
}

internal sealed class DiscordSettings
{
    /// <summary>Décalage toléré entre les événements « parle » de Discord et l'audio.</summary>
    public int SpeakerMarginMs { get; set; } = 300;
}
