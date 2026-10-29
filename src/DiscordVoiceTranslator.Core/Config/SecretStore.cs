using System.Security.Cryptography;
using System.Text;

namespace DiscordVoiceTranslator.Core.Config;

/// <summary>
/// Clés API chiffrées avec DPAPI, liées au compte Windows. Le fichier est illisible pour un
/// autre compte ou sur un autre PC ; il ne protège pas contre un programme malveillant qui
/// tournerait déjà sous ton propre compte (aucun stockage local ne le peut).
/// </summary>
public static class SecretStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordVoiceTranslator", "secrets");

    // Pas un secret : évite seulement qu'un autre logiciel déchiffre nos fichiers par accident.
    private static readonly byte[] Entropy = "DiscordVoiceTranslator.v1"u8.ToArray();

    public static void Save(string name, string value)
    {
        Directory.CreateDirectory(Folder);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), encrypted);
    }

    public static string? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // fichier copié d'un autre compte ou abîmé
        }
    }

    public static void Delete(string name) => File.Delete(PathFor(name));

    private static string PathFor(string name) => Path.Combine(Folder, name + ".bin");
}
