using System.Collections.ObjectModel;
using Navegador.Models;

namespace Navegador.Services;

/// <summary>Lista de descargas. Se guarda en disco, así que sobrevive al cierre de la app.</summary>
public static class DownloadsStore
{
    private const string FileName = "downloads.json";
    private const int MaxSaved = 200;

    public static ObservableCollection<DownloadItem> Items { get; } = Load();

    private static ObservableCollection<DownloadItem> Load()
    {
        var records = JsonStore.Load<List<DownloadRecord>>(FileName);
        var items = new ObservableCollection<DownloadItem>(records.Select(DownloadItem.FromRecord));
        items.CollectionChanged += (_, _) => Save();
        return items;
    }

    public static void Save()
    {
        var records = Items
            .Select(i => i.ToRecord())
            .Where(r => r.State != "InProgress")
            .Take(MaxSaved)
            .ToList();
        JsonStore.Save(FileName, records);
    }
}
