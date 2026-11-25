using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Navegador.Ui;

/// <summary>
/// Íconos de botones y opciones con el color de acento del navegador (el violeta del ícono de la app).
/// Los íconos de carpetas y archivos del explorador NO pasan por acá: conservan su color por tipo.
/// </summary>
internal static class AccentIcons
{
    public static Color Current { get; } = Palette.Rgb(0x70, 0x5C, 0xFF);

    public static FontIcon Create(string glyph, double size = 16) =>
        new() { Glyph = glyph, FontSize = size, Foreground = new SolidColorBrush(Current) };
}
