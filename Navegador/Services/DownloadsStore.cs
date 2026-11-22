using System.Collections.ObjectModel;
using Navegador.Models;

namespace Navegador.Services;

public static class DownloadsStore
{
    public static ObservableCollection<DownloadItem> Items { get; } = new();
}
