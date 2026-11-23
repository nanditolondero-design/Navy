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

    private static readonly (string File, string Url)[] FilterLists =
    {
        ("easylist.txt", "https://easylist.to/easylist/easylist.txt"),
        ("easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt"),
        ("easylist-es.txt", "https://easylist-downloads.adblockplus.org/easylistspanish.txt")
    };

    private volatile HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private volatile FilterEngine? _engine;

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

    /// <summary>Descarga (con caché de 7 días) EasyList, EasyPrivacy y EasyList Spanish y arma el motor de filtros.</summary>
    public async Task UpdateFilterListsAsync()
    {
        try
        {
            var lines = new List<string>();
            foreach (var (file, url) in FilterLists)
            {
                var text = await GetCachedAsync(file, url);
                if (text is not null) lines.AddRange(text.Split('\n'));
            }
            if (lines.Count == 0) return;
            _engine = await Task.Run(() => FilterEngine.Parse(lines));
        }
        catch
        {
        }
    }

    private static async Task<string?> GetCachedAsync(string fileName, string url)
    {
        var path = Path.Combine(CacheDir, fileName);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7))
                return await File.ReadAllTextAsync(path);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var text = await http.GetStringAsync(url);
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(path, text);
            return text;
        }
        catch
        {
            // sin conexión: se usa la copia vieja si existe
            return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        }
    }

    /// <summary>Bloqueo completo: dominios conocidos + reglas de las listas (rutas, terceros, tipos de recurso).</summary>
    public bool ShouldBlock(Uri uri, string? pageHost, int typeBit)
    {
        if (ShouldBlock(uri)) return true;
        return _engine?.ShouldBlock(uri, pageHost, typeBit) ?? false;
    }

    /// <summary>CSS para ocultar los elementos publicitarios de la página.</summary>
    public string CosmeticCss(string host) => _engine?.BuildCosmeticCss(host) ?? FilterEngine.CuratedCss();

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
