using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Texte cerné d'un contour net, comme les sous-titres de cinéma : lisible sur n'importe quel
/// décor sans boîte derrière. Un halo (DropShadowEffect) reste flou et trop pâle sur fond clair.
/// Construit une fois pour une ligne : changer une propriété après coup ne redessine rien.
/// </summary>
internal sealed class OutlinedText : FrameworkElement
{
    public string Text { get; init; } = "";
    public Brush Fill { get; init; } = Brushes.White;
    public Brush Outline { get; init; } = Brushes.Black;
    public double FontSize { get; init; } = 20;
    public FontStyle FontStyle { get; init; } = FontStyles.Normal;
    public FontWeight FontWeight { get; init; } = FontWeights.Normal;
    public TextAlignment TextAlignment { get; init; } = TextAlignment.Left;

    private Geometry? _geometry;

    // Le trait est centré sur le bord des lettres : seule sa moitié dépasse à l'extérieur.
    private double OutlineWidth => Math.Max(1.5, FontSize * 0.16);

    protected override Size MeasureOverride(Size available)
    {
        var text = Format(available.Width);
        // Espaces finaux compris (le « / » entre deux noms), et arrondi vers le haut : relayouter
        // à la largeur exacte pourrait renvoyer le dernier mot à la ligne.
        return new Size(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + OutlineWidth, text.Height + OutlineWidth);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _geometry = Format(final.Width).BuildGeometry(new Point(OutlineWidth / 2, OutlineWidth / 2));
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry is null)
            return;
        // Contour d'abord, remplissage par-dessus : le trait ne mange pas l'intérieur des lettres.
        dc.DrawGeometry(null, new Pen(Outline, OutlineWidth) { LineJoin = PenLineJoin.Round }, _geometry);
        dc.DrawGeometry(Fill, null, _geometry);
    }

    private FormattedText Format(double width)
    {
        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(TextElement.GetFontFamily(this), FontStyle, FontWeight, FontStretches.Normal),
            FontSize, Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = TextAlignment };
        if (!double.IsInfinity(width))
            text.MaxTextWidth = Math.Max(1, width - OutlineWidth);
        return text;
    }
}
