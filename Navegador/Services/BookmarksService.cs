using Navegador.Models;

namespace Navegador.Services;

public sealed class BookmarksService
{
    private const string FileName = "bookmarks.json";
    private readonly List<Bookmark> _items = JsonStore.Load<List<Bookmark>>(FileName);

    public event Action? Changed;

    public IReadOnlyList<Bookmark> All => _items;

    public bool Contains(string? url) => url is not null && _items.Any(b => b.Url == url);

    public void Add(string title, string url)
    {
        if (Contains(url)) return;
        _items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        SaveAndNotify();
    }

    public void Remove(string url)
    {
        _items.RemoveAll(b => b.Url == url);
        SaveAndNotify();
    }

    public int Merge(IEnumerable<Bookmark> bookmarks)
    {
        var added = 0;
        foreach (var b in bookmarks)
        {
            if (Contains(b.Url)) continue;
            _items.Add(b);
            added++;
        }
        if (added > 0) SaveAndNotify();
        return added;
    }

    private void SaveAndNotify()
    {
        JsonStore.Save(FileName, _items);
        Changed?.Invoke();
    }
}
