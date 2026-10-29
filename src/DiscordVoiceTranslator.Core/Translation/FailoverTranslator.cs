using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Translation;

/// <summary>
/// Plusieurs clés du même fournisseur, prises dans l'ordre : quand le quota du mois d'une clé
/// est épuisé (ou qu'elle est refusée), la suivante prend le relais sans perdre la phrase.
/// </summary>
public sealed class FailoverTranslator : ITranslator
{
    private readonly string _name;
    private readonly IReadOnlyList<ITranslator> _translators;
    private int _current;

    public FailoverTranslator(string name, IReadOnlyList<ITranslator> translators)
    {
        if (translators.Count == 0)
            throw new ArgumentException("At least one translator is needed.", nameof(translators));
        _name = name;
        _translators = translators;
    }

    public string Name => _translators.Count == 1
        ? _name
        : T($"{_name} (key {Current + 1}/{_translators.Count})", $"{_name} (clé {Current + 1}/{_translators.Count})");

    private int Current => Volatile.Read(ref _current);

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        // Chaque clé est essayée au plus une fois par phrase. On reboucle sur les premières :
        // leur quota a pu repartir à zéro avec le nouveau mois.
        for (int attempt = 1; ; attempt++)
        {
            int index = Current;
            try
            {
                return await _translators[index].TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
            }
            catch (TranslationException ex) when (ex.KeyUnusable && _translators.Count > 1)
            {
                MoveNext(index);
                if (attempt == _translators.Count)
                    throw new TranslationException(T($"All {_translators.Count} {_name} keys are used up or rejected.",
                                                     $"Les {_translators.Count} clés {_name} sont toutes épuisées ou refusées."), ex, keyUnusable: true);
            }
        }
    }

    public async Task<TranslatorUsage?> GetUsageAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            int index = Current;
            TranslatorUsage? usage;
            try
            {
                usage = await _translators[index].GetUsageAsync(cancellationToken);
            }
            catch (TranslationException ex) when (ex.KeyUnusable && attempt < _translators.Count)
            {
                MoveNext(index);
                continue;
            }

            // Quota déjà atteint : inutile d'attendre un refus sur la prochaine phrase pour changer.
            if (usage is { } used && used.CharactersUsed >= used.CharacterLimit && attempt < _translators.Count)
            {
                MoveNext(index);
                continue;
            }
            return usage;
        }
    }

    // Si une autre requête a déjà changé de clé entre-temps, on garde son choix.
    private void MoveNext(int from) =>
        Interlocked.CompareExchange(ref _current, (from + 1) % _translators.Count, from);

    public void Dispose()
    {
        foreach (var translator in _translators)
            translator.Dispose();
    }
}
