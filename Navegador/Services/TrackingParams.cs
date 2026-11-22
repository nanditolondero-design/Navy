namespace Navegador.Services;

/// <summary>Quita parámetros de seguimiento conocidos (utm_*, fbclid, gclid...) de las URL.</summary>
public static class TrackingParams
{
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "mc_eid", "mc_cid",
        "igshid", "yclid", "twclid", "ttclid", "li_fat_id", "_hsenc", "_hsmi",
        "vero_id", "oly_enc_id", "oly_anon_id", "mkt_tok", "s_cid"
    };

    public static Uri Strip(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return uri;

        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(p =>
        {
            var key = Uri.UnescapeDataString(p.Split('=')[0]);
            return !(key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || Exact.Contains(key));
        }).ToArray();

        if (kept.Length == parts.Length) return uri;
        return new UriBuilder(uri) { Query = string.Join("&", kept) }.Uri;
    }
}
