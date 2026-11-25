using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Navegador.Ui;

/// <summary>Colores y pinceles que usa el explorador (versión mínima de la Palette de AlternAIs).</summary>
internal static class Palette
{
    public static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    public static SolidColorBrush Brush(Color c) => new(c);

    /// <summary>El mismo color con otra opacidad (0–255).</summary>
    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color TextOnColor(Color c)
    {
        var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum > 0.6 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }
}
