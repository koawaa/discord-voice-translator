using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Style des lignes de sous-titres, choisi dans Paramètres > Overlay. Le thème construit la
/// ligne entière (fond, noms, texte) ; l'overlay se contente de l'empiler et de l'estomper.
/// </summary>
internal sealed record SubtitleTheme(string Id, string EnglishName, string FrenchName, string EnglishDetails, string FrenchDetails)
{
    public const string DefaultId = "classic";

    public string DisplayName => T(EnglishName, FrenchName);
    public string Details => T(EnglishDetails, FrenchDetails);

    public static IReadOnlyList<SubtitleTheme> All { get; } =
    [
        new(DefaultId, "Classic", "Classique",
            "Dark rounded box, the original look.",
            "Boîte sombre arrondie, le style d’origine."),
        new("cinema", "Cinema", "Cinéma",
            "No box: centered white text with a black outline, like a film. Ignores the background opacity.",
            "Pas de boîte : texte blanc centré et cerné de noir, comme au cinéma. Ignore l’opacité du fond."),
        new("chat", "Chat", "Discussion",
            "A Discord-style message with a bar in the speaker’s color.",
            "Un message façon Discord, avec une barre à la couleur de la personne."),
        new("neon", "Neon", "Néon",
            "Glowing border in the speaker’s color.",
            "Contour lumineux à la couleur de la personne."),
    ];

    public static SubtitleTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    private static readonly Brush White = Frozen(Colors.White);
    private static readonly Brush Grey = Frozen(Color.FromRgb(0xB8, 0xB8, 0xB8));

    /// <summary>
    /// Une prise de parole : avatars et noms colorés (« A / B » si deux voix se mêlent),
    /// texte, et l'original en petit s'il est fourni.
    /// </summary>
    public Border BuildLine(IReadOnlyList<OverlaySpeaker> speakers, string text, string? original, double size, double backgroundOpacity)
    {
        byte alpha = (byte)(backgroundOpacity * 255);
        Brush accent = speakers.Count > 0 ? speakers[0].Color : White;
        return Id switch
        {
            // Texte cerné de noir, plus une ombre douce qui le décolle encore d'un décor chargé.
            "cinema" => new Border
            {
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = Halo(Colors.Black, 8, 0.5),
                Child = Content(speakers, text, original, size, White, Frozen(Color.FromRgb(0xE2, 0xE2, 0xE2)), cinema: true),
            },
            "chat" => new Border
            {
                Background = Frozen(Color.FromArgb(alpha, 0x31, 0x33, 0x38)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 14, 8),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 10, 0), Background = accent }, Dock.Left),
                        Content(speakers, text, original, size, Frozen(Color.FromRgb(0xDB, 0xDE, 0xE1)), Frozen(Color.FromRgb(0xB5, 0xBA, 0xC1)), cinema: false),
                    },
                },
            },
            // Le halo est porté par un contour vide placé derrière : posé sur la ligne elle-même,
            // il se verrait à travers le fond translucide et teinterait toute la boîte.
            // Marge à gauche : le halo déborde de la ligne et serait coupé par le bord de l'overlay.
            "neon" => new Border
            {
                Margin = new Thickness(4, 6, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Grid
                {
                    Children =
                    {
                        new Border
                        {
                            BorderBrush = accent,
                            BorderThickness = new Thickness(1.5),
                            CornerRadius = new CornerRadius(10),
                            Effect = Halo(accent is SolidColorBrush solid ? solid.Color : Colors.White, 14, 1),
                        },
                        new Border
                        {
                            Background = Frozen(Color.FromArgb(alpha, 0x0A, 0x0C, 0x14)),
                            BorderBrush = accent,
                            BorderThickness = new Thickness(1.5),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(12, 5, 12, 7),
                            Child = Content(speakers, text, original, size, White, Frozen(Color.FromRgb(0xA9, 0xB4, 0xC8)), cinema: false),
                        },
                    },
                },
            },
            _ => new Border
            {
                Background = Frozen(Color.FromArgb(alpha, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 6),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Content(speakers, text, original, size, White, Grey, cinema: false),
            },
        };
    }

    /// <param name="cinema">Texte centré et cerné de noir au lieu d'un texte simple aligné à gauche.</param>
    private static StackPanel Content(IReadOnlyList<OverlaySpeaker> speakers, string text, string? original, double size,
                                      Brush textBrush, Brush mutedBrush, bool cinema)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = cinema ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        foreach (var speaker in speakers.Where(s => s.Avatar is not null))
        {
            header.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, 5, 0),
                Fill = new ImageBrush(speaker.Avatar) { Stretch = Stretch.UniformToFill },
            });
        }
        for (int i = 0; i < speakers.Count; i++)
        {
            if (i > 0)
                header.Children.Add(Label(" / ", mutedBrush, size * 0.75, cinema, FontWeights.SemiBold));
            header.Children.Add(Label(speakers[i].Name, speakers[i].Color, size * 0.75, cinema, FontWeights.SemiBold));
        }

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(Label(text, textBrush, size, cinema));
        if (!string.IsNullOrWhiteSpace(original))
            content.Children.Add(Label(original, mutedBrush, size * 0.7, cinema, style: FontStyles.Italic));
        return content;
    }

    private static FrameworkElement Label(string text, Brush brush, double size, bool cinema, FontWeight? weight = null, FontStyle? style = null) =>
        cinema
            ? new OutlinedText
            {
                Text = text,
                Fill = brush,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                FontStyle = style ?? FontStyles.Normal,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                FontStyle = style ?? FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

    private static DropShadowEffect Halo(Color color, double blur, double opacity)
    {
        var effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = blur, Opacity = opacity };
        effect.Freeze();
        return effect;
    }

    private static UIElement Docked(UIElement element, Dock dock)
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
