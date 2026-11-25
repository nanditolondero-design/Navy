using System.Net;
using System.Text;

namespace Navegador.Services;

/// <summary>Página de inicio de las pestañas nuevas: buscador y favoritos, al estilo de Safari.</summary>
public static class StartPage
{
    public static string Build(string engine, IReadOnlyList<(string Title, string Url)> tiles, bool incognito)
    {
        var searchUrl = SearchEngines.SearchUrl(engine, "");
        var q = searchUrl.IndexOf('?');
        var action = q > 0 ? searchUrl[..q] : searchUrl;

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Página de inicio</title>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<style>");
        sb.Append(":root{color-scheme:light dark;--bg:#f3f4f8;--fg:#1d1d1f;--card:#fff;--muted:#6e6e73;--line:#d9dbe3}");
        sb.Append("@media(prefers-color-scheme:dark){:root{--bg:#1c1c1e;--fg:#f2f2f7;--card:#2c2c2e;--muted:#a1a1a6;--line:#3a3a3c}}");
        sb.Append("*{box-sizing:border-box}body{margin:0;min-height:100vh;display:flex;flex-direction:column;align-items:center;");
        sb.Append("padding:12vh 24px 40px;background:var(--bg);color:var(--fg);font-family:'Segoe UI',system-ui,sans-serif}");
        sb.Append("h1{font-size:1.1rem;font-weight:600;color:var(--muted);margin:0 0 20px}");
        sb.Append("form{width:min(640px,100%);margin-bottom:44px}");
        sb.Append("input{width:100%;font-size:1.05rem;padding:14px 20px;border-radius:14px;border:1px solid var(--line);");
        sb.Append("background:var(--card);color:var(--fg);outline:none;text-align:center}");
        sb.Append("input:focus{border-color:#705cff;box-shadow:0 0 0 3px rgba(112,92,255,.25);text-align:left}");
        sb.Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(104px,1fr));gap:22px;width:min(760px,100%)}");
        sb.Append("a.tile{display:flex;flex-direction:column;align-items:center;gap:10px;text-decoration:none;color:var(--fg);font-size:.82rem}");
        sb.Append(".ico{width:64px;height:64px;border-radius:16px;display:flex;align-items:center;justify-content:center;");
        sb.Append("font-size:1.6rem;font-weight:600;color:#fff;box-shadow:0 2px 8px rgba(0,0,0,.18)}");
        sb.Append("a.tile span.t{max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}");
        sb.Append("a.tile:hover .ico{transform:translateY(-2px)}");
        sb.Append("h2{font-size:.8rem;text-transform:uppercase;letter-spacing:.08em;color:var(--muted);margin:0 0 18px;align-self:flex-start;");
        sb.Append("width:min(760px,100%);margin-inline:auto}");
        sb.Append(".inc{margin-top:8px;color:var(--muted);font-size:.9rem;text-align:center}");
        sb.Append("</style></head><body>");

        sb.Append("<h1>").Append(incognito ? "🕶 Navegación de incógnito" : "Navegador").Append("</h1>");
        sb.Append("<form action=\"").Append(WebUtility.HtmlEncode(action)).Append("\" method=\"get\">");
        sb.Append("<input name=\"q\" autofocus autocomplete=\"off\" placeholder=\"Buscar en ")
          .Append(WebUtility.HtmlEncode(engine)).Append("\"></form>");

        if (incognito)
        {
            sb.Append("<p class=\"inc\">Esta pestaña no guarda historial, cookies ni datos de sitios cuando se cierra.</p>");
        }

        if (tiles.Count > 0)
        {
            sb.Append("<h2>Favoritos</h2><div class=\"grid\">");
            foreach (var (title, url) in tiles)
            {
                var label = string.IsNullOrWhiteSpace(title) ? url : title;
                var initial = label.TrimStart().Length > 0 ? char.ToUpperInvariant(label.TrimStart()[0]).ToString() : "?";
                var hue = (uint)Hash(url) % 360;
                sb.Append("<a class=\"tile\" href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">");
                sb.Append("<div class=\"ico\" style=\"background:hsl(").Append(hue).Append(" 62% 52%)\">")
                  .Append(WebUtility.HtmlEncode(initial)).Append("</div>");
                sb.Append("<span class=\"t\">").Append(WebUtility.HtmlEncode(label)).Append("</span></a>");
            }
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static int Hash(string s)
    {
        var h = 17;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return h;
    }
}
