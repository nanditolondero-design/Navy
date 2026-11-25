namespace Navegador.Models;

/// <summary>Una sugerencia de la barra de direcciones.</summary>
public sealed class Suggestion
{
    public string Glyph { get; set; } = "\uE721";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Url { get; set; } = "";
}
