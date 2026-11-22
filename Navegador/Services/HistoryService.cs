using Navegador.Models;

namespace Navegador.Services;

public sealed class HistoryService
{
    private const string FileName = "history.json";
    private const int MaxItems = 10000;

    private readonly object _lock = new();
    private readonly List<HistoryEntry> _items;
    private readonly Timer _saveTimer;

    public HistoryService()
    {
        _items = JsonStore.Load<List<HistoryEntry>>(FileName);
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Add(string title, string url)
    {
        lock (_lock)
        {
            if (_items.Count > 0)
            {
                var last = _items[^1];
                if (last.Url == url && (DateTime.Now - last.Time).TotalSeconds < 30)
                {
                    last.Title = title;
                    MarkDirty();
                    return;
                }
            }
            _items.Add(new HistoryEntry { Title = title, Url = url, Time = DateTime.Now });
            if (_items.Count > MaxItems) _items.RemoveRange(0, _items.Count - MaxItems);
            MarkDirty();
        }
    }

    public List<HistoryEntry> Search(string? text, int max = 500)
    {
        lock (_lock)
        {
            IEnumerable<HistoryEntry> q = Enumerable.Reverse(_items);
            if (!string.IsNullOrWhiteSpace(text))
                q = q.Where(e => e.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || e.Url.Contains(text, StringComparison.OrdinalIgnoreCase));
            return q.Take(max).ToList();
        }
    }

    public int Merge(IEnumerable<HistoryEntry> entries)
    {
        var added = 0;
        lock (_lock)
        {
            var seen = new HashSet<string>(_items.Select(Key));
            foreach (var e in entries)
            {
                if (seen.Add(Key(e)))
                {
                    _items.Add(e);
                    added++;
                }
            }
            _items.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (_items.Count > MaxItems) _items.RemoveRange(0, _items.Count - MaxItems);
            MarkDirty();
        }
        return added;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            MarkDirty();
        }
    }

    public void Flush()
    {
        lock (_lock) JsonStore.Save(FileName, _items);
    }

    private static string Key(HistoryEntry e) => e.Url + "|" + (e.Time.Ticks / TimeSpan.TicksPerSecond);

    private void MarkDirty() => _saveTimer.Change(2000, Timeout.Infinite);
}
