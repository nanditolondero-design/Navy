using System.Text.Json;
using Microsoft.Data.Sqlite;
using Navegador.Models;

namespace Navegador.Services;

public sealed record ChromeProfile(string Dir, string Name, string FullPath);

/// <summary>Importa marcadores e historial desde un perfil de Google Chrome instalado en este equipo.</summary>
public static class ChromeImporter
{
    private static string UserDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data");

    public static List<ChromeProfile> FindProfiles()
    {
        var result = new List<ChromeProfile>();
        if (!Directory.Exists(UserDataDir)) return result;

        var names = ReadProfileNames();
        foreach (var dir in Directory.EnumerateDirectories(UserDataDir))
        {
            var d = Path.GetFileName(dir);
            if (d != "Default" && !d.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            if (!File.Exists(Path.Combine(dir, "Bookmarks")) && !File.Exists(Path.Combine(dir, "History"))) continue;

            var display = names.TryGetValue(d, out var n) && !string.IsNullOrWhiteSpace(n) ? $"{n} ({d})" : d;
            result.Add(new ChromeProfile(d, display, dir));
        }
        return result;
    }

    private static Dictionary<string, string> ReadProfileNames()
    {
        var map = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(UserDataDir, "Local State");
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(ReadSharedText(path));
            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("info_cache", out var cache))
            {
                foreach (var p in cache.EnumerateObject())
                {
                    if (p.Value.TryGetProperty("name", out var name))
                        map[p.Name] = name.GetString() ?? "";
                }
            }
        }
        catch
        {
        }
        return map;
    }

    public static List<Bookmark> ReadBookmarks(ChromeProfile profile)
    {
        var list = new List<Bookmark>();
        var path = Path.Combine(profile.FullPath, "Bookmarks");
        if (!File.Exists(path)) return list;

        using var doc = JsonDocument.Parse(ReadSharedText(path));
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return list;

        foreach (var rootName in new[] { "bookmark_bar", "other", "synced" })
        {
            if (roots.TryGetProperty(rootName, out var root)) Walk(root, list);
        }
        return list;
    }

    private static void Walk(JsonElement node, List<Bookmark> output)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "url")
        {
            var url = node.TryGetProperty("url", out var u) ? u.GetString() : null;
            var name = node.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
                output.Add(new Bookmark { Title = name ?? url, Url = url });
        }
        else if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray()) Walk(child, output);
        }
    }

    public static List<HistoryEntry> ReadHistory(ChromeProfile profile)
    {
        var list = new List<HistoryEntry>();
        var source = Path.Combine(profile.FullPath, "History");
        if (!File.Exists(source)) return list;

        // Chrome mantiene el archivo abierto: se trabaja sobre una copia
        var temp = Path.Combine(Path.GetTempPath(), "navegador_hist_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = File.Create(temp))
            {
                src.CopyTo(dst);
            }

            using var con = new SqliteConnection($"Data Source={temp};Mode=ReadOnly;Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT url, title, last_visit_time FROM urls WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT 5000";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var url = rd.GetString(0);
                if (!url.StartsWith("http://") && !url.StartsWith("https://")) continue;
                var title = rd.IsDBNull(1) ? "" : rd.GetString(1);

                var time = DateTime.Now;
                try
                {
                    // Chrome guarda microsegundos desde 1601 (FILETIME = 100 ns)
                    time = DateTime.FromFileTimeUtc(rd.GetInt64(2) * 10).ToLocalTime();
                }
                catch
                {
                }
                list.Add(new HistoryEntry { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, Time = time });
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        return list;
    }

    private static string ReadSharedText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
