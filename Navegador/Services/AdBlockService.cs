namespace Navegador.Services;

/// <summary>
/// Bloqueo por dominio: si el host de una solicitud (o un dominio padre) está en la lista, se bloquea.
/// Parte de una lista incluida en la app y, en segundo plano, suma una lista pública (cache de 7 días).
/// </summary>
public sealed class AdBlockService
{
    private const string RemoteUrl =
        "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=nohtml&showintro=0&mimetype=plaintext";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador");
    private static readonly string CacheFile = Path.Combine(CacheDir, "blocklist-cache.txt");

    private volatile HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;
    public int BlockedCount { get; set; }

    public void LoadDefaults() => Merge(DefaultBlocklist.Domains.Split('\n'));

    public async Task UpdateFromRemoteAsync()
    {
        try
        {
            string[] lines;
            if (File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < TimeSpan.FromDays(7))
            {
                lines = await File.ReadAllLinesAsync(CacheFile);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var text = await http.GetStringAsync(RemoteUrl);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(CacheFile, text);
                lines = text.Split('\n');
            }
            Merge(lines);
        }
        catch
        {
            // sin conexión: se sigue con la lista incluida
        }
    }

    private void Merge(IEnumerable<string> lines)
    {
        var set = new HashSet<string>(_domains, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var host = parts[^1]; // formato hosts ("0.0.0.0 dominio") o dominio solo
            if (host.Contains('.') && !host.Contains('/') && !host.Contains(':'))
                set.Add(host);
        }
        _domains = set;
    }

    public bool ShouldBlock(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var domains = _domains;
        var host = uri.Host;
        while (true)
        {
            if (domains.Contains(host)) return true;
            var dot = host.IndexOf('.');
            if (dot < 0) return false;
            host = host[(dot + 1)..];
        }
    }
}
