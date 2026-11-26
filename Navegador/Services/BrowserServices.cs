using Microsoft.UI.Xaml;

namespace Navegador.Services;

/// <summary>Lo que las páginas internas (descargas, historial, marcadores, configuración) necesitan del navegador.</summary>
public sealed class BrowserServices
{
    public required HistoryService History { get; init; }
    public required BookmarksService Bookmarks { get; init; }
    public required VaultService Vault { get; init; }
    public required HelloService Hello { get; init; }
    public required Window Owner { get; init; }
    public required Action<string> OpenUrl { get; init; }
    public required Action SettingsChanged { get; init; }
    public required Func<Task> ClearBrowsingDataAsync { get; init; }
}
