using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Navegador;

public enum TabKind { Web, Downloads, History, Bookmarks, Settings }

public sealed class BrowserTab
{
    public required TabViewItem Item { get; init; }
    public required FrameworkElement Content { get; init; }
    public WebView2? View { get; init; }
    public bool Incognito { get; init; }
    public TabKind Kind { get; init; } = TabKind.Web;

    // Estado de las páginas web
    public bool Loading { get; set; }
    public int Blocked { get; set; }
    public string? PageHost { get; set; }
    public string? UpgradedHttps { get; set; }
    public string? HttpFallbackUrl { get; set; }
    public bool SkipUpgrade { get; set; }
}
