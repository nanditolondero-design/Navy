using System.Text;

namespace Navegador.Services;

/// <summary>Lee y escribe el CSV de contraseñas (el que exportan Chrome, Edge, Firefox y Bitwarden).</summary>
public static class CsvPasswords
{
    public static (List<VaultEntry> Entries, int Skipped) Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0) throw new FormatException("El archivo está vacío.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                var i = header.IndexOf(n);
                if (i >= 0) return i;
            }
            return -1;
        }

        var iName = Find("name", "title");
        var iUrl = Find("url", "login_uri", "website", "web site", "origin");
        var iUser = Find("username", "login_username", "user", "user name");
        var iPass = Find("password", "login_password", "pass");
        var iNote = Find("note", "notes");

        if (iPass < 0 || (iUrl < 0 && iUser < 0))
            throw new FormatException("No parece un archivo de contraseñas: faltan las columnas de sitio, usuario o contraseña.");

        string Get(List<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";

        var entries = new List<VaultEntry>();
        var skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            var password = iPass < row.Count ? row[iPass] : "";
            var url = Get(row, iUrl);
            var user = Get(row, iUser);
            if (password.Length == 0 || (url.Length == 0 && user.Length == 0))
            {
                skipped++;
                continue;
            }

            var name = Get(row, iName);
            if (name.Length == 0)
            {
                var host = VaultService.HostFromUrl(url);
                name = host is null ? (user.Length > 0 ? user : "Sin nombre") : (host.StartsWith("www.") ? host[4..] : host);
            }

            entries.Add(new VaultEntry
            {
                Name = name,
                Url = url,
                Username = user,
                Password = password,
                Note = Get(row, iNote)
            });
        }
        return (entries, skipped);
    }

    public static string Write(IEnumerable<VaultEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("name,url,username,password,note\r\n");
        foreach (var e in entries)
        {
            sb.Append(Quote(e.Name)).Append(',')
              .Append(Quote(e.Url)).Append(',')
              .Append(Quote(e.Username)).Append(',')
              .Append(Quote(e.Password)).Append(',')
              .Append(Quote(e.Note)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Sobrescribe el archivo con ceros antes de borrarlo (en discos SSD no hay garantía total).</summary>
    public static void SecureDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(length, 1 << 16)];
                long written = 0;
                while (written < length)
                {
                    var n = (int)Math.Min(zeros.Length, length - written);
                    fs.Write(zeros, 0, n);
                    written += n;
                }
                fs.Flush(true);
            }
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 && value.Trim() == value) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
