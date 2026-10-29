using System.Diagnostics;

namespace DiscordVoiceTranslator.Core.Diagnostics;

public static class GpuMemory
{
    /// <summary>
    /// Mémoire GPU dédiée utilisée par ce processus, en octets : le même compteur que la
    /// colonne « Mémoire GPU dédiée » du Gestionnaire des tâches. Null si indisponible.
    /// Coûte quelques millisecondes : à appeler de temps en temps, pas en boucle.
    /// </summary>
    public static long? DedicatedBytesForCurrentProcess()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            string prefix = $"pid_{Environment.ProcessId}_";
            long total = 0;
            foreach (string instance in category.GetInstanceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            {
                using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                total += counter.RawValue;
            }
            return total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
