using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Navegador;

public enum TabKind { Web, Downloads, History, Bookmarks, Settings, Passwords }

/// <summary>Usuario y contraseña que se acaban de escribir en un formulario, a la espera de ofrecer guardarlos.</summary>
public sealed record PendingCred(string Host, string User, string Pass, DateTime At);

public sealed class BrowserTab
{
    public required TabViewItem Item { get; init; }
    public required FrameworkElement Content { get; init; }
    public WebView2? View { get; init; }
    public bool Incognito { get; init; }
    public TabKind Kind { get; init; } = TabKind.Web;

    // Estado de las páginas web
    public bool Loading { get; set; }
    public string Title { get; set; } = "";
    public bool Playing { get; set; }
    public bool Suspended { get; set; }
    public double Zoom { get; set; } = 1.0;
    public DateTime? LastActive { get; set; }
    public string Nonce { get; set; } = "";
    public PendingCred? Pending { get; set; }
    public int Blocked { get; set; }
    public string? PageHost { get; set; }
    public string? UpgradedHttps { get; set; }
    public string? HttpFallbackUrl { get; set; }
    public bool SkipUpgrade { get; set; }
}
