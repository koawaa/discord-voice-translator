using System.Security.Cryptography;

namespace DiscordVoiceTranslator.Core.Models;

/// <summary>Un modèle téléchargeable, avec son empreinte pour vérifier qu'il est intact.</summary>
public sealed record ModelFile(string FileName, string Url, long Size, string Sha256)
{
    /// <summary>Déjà téléchargé dans ce dossier (vérifié lors du téléchargement, la taille suffit).</summary>
    public bool IsDownloaded(string directory)
    {
        var info = new FileInfo(Path.Combine(directory, FileName));
        return info.Exists && info.Length == Size;
    }
}

/// <summary>
/// Télécharge un modèle au premier lancement. Le fichier est écrit à côté (.part) puis
/// renommé seulement si son empreinte SHA-256 est la bonne : un téléchargement coupé ou
/// corrompu n'est jamais pris pour un modèle valide.
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <param name="progress">Fraction téléchargée, de 0 à 1.</param>
    /// <returns>Chemin du modèle prêt à l'emploi.</returns>
    public static async Task<string> EnsureAsync(ModelFile model, string directory, Action<double>? progress, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, model.FileName);
        // Déjà vérifié lors de son téléchargement : la taille suffit, pas besoin de relire des centaines de Mo à chaque lancement.
        if (model.IsDownloaded(directory))
            return path;

        Directory.CreateDirectory(directory);
        string partial = path + ".part";
        using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(partial);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[1 << 20];
            long received = 0;
            int lastPercent = -1, read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                int percent = (int)(received * 100 / model.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Invoke(Math.Min(1, (double)received / model.Size));
                }
            }

            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                target.Close();
                File.Delete(partial);
                throw new InvalidDataException("Downloaded model is corrupted (SHA-256 mismatch).");
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
