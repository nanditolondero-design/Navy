using System.Text;
using System.Text.RegularExpressions;

namespace Navegador.Services;

/// <summary>
/// Motor de filtros compatible con lo esencial de EasyList/EasyPrivacy:
/// reglas de red (||dominio^, rutas, comodines, $third-party, $script, $image, $domain=...)
/// y ocultamiento de elementos (##selector, dominio##selector).
/// </summary>
public sealed class FilterEngine
{
    // Bits de tipo de recurso
    public const int Script = 1, Image = 2, Stylesheet = 4, Media = 8, Font = 16, Xhr = 32, Other = 64, Ping = 128;

    private static readonly Regex TokenRegex = new("[a-z0-9]{3,}", RegexOptions.Compiled);
    private static readonly Regex OptionsRegex =
        new(@"^[~a-z0-9_\-]+(=[^,]*)?(,[~a-z0-9_\-]+(=[^,]*)?)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] CuratedSelectors =
    {
        "ins.adsbygoogle", ".adsbygoogle", "[id^=\"google_ads_iframe\"]", "[id^=\"div-gpt-ad\"]",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication.com\"]",
        ".ad-banner", ".ad-container", ".advertisement", "[aria-label=\"Advertisement\"]", "#ad-slot"
    };

    private static readonly string[] Unsupported =
    {
        ":-abp-", ":has-text(", ":contains(", ":xpath(", ":matches-css", ":upward(", ":remove(", ":style(", "+js(", ":min-text-length("
    };

    private readonly RuleSet _blocks = new();
    private readonly RuleSet _exceptions = new();
    private readonly List<string> _generic = new();
    private readonly Dictionary<string, List<string>> _byDomain = new(StringComparer.OrdinalIgnoreCase);

    public int NetworkRules => _blocks.Count;
    public int CosmeticRules => _generic.Count + _byDomain.Values.Sum(l => l.Count);

    // ───────── Carga ─────────

    public static FilterEngine Parse(IEnumerable<string> lines)
    {
        var engine = new FilterEngine();
        foreach (var line in lines)
        {
            try { engine.ParseLine(line); } catch { }
        }
        return engine;
    }

    private void ParseLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == '[') return;

        // Ocultamiento de elementos
        var hash = line.IndexOf("##", StringComparison.Ordinal);
        if (hash >= 0 || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#"))
        {
            if (hash < 0 || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#")) return;
            AddCosmetic(line[..hash], line[(hash + 2)..]);
            return;
        }

        // Reglas de red
        var isException = line.StartsWith("@@", StringComparison.Ordinal);
        if (isException) line = line[2..];

        var options = "";
        var dollar = line.LastIndexOf('$');
        if (dollar >= 0 && OptionsRegex.IsMatch(line[(dollar + 1)..]))
        {
            options = line[(dollar + 1)..];
            line = line[..dollar];
        }

        if (line.Length < 3) return;
        if (line[0] == '/' && line[^1] == '/') return; // reglas con regex: no soportadas

        var rule = new Rule();
        if (!ApplyOptions(rule, options)) return;

        var p = line.ToLowerInvariant();
        if (p.StartsWith("||", StringComparison.Ordinal)) { rule.HostAnchor = true; p = p[2..]; }
        else if (p.StartsWith('|')) { rule.StartAnchor = true; p = p[1..]; }
        if (p.EndsWith('|')) { rule.EndAnchor = true; p = p[..^1]; }
        if (p.Trim('*', '^').Length < 3) return;

        rule.Pattern = p;
        (isException ? _exceptions : _blocks).Add(rule);
    }

    private void AddCosmetic(string domains, string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0 || selector.Length > 90) return;
        foreach (var bad in Unsupported)
            if (selector.Contains(bad, StringComparison.Ordinal)) return;

        if (domains.Length == 0)
        {
            if (_generic.Count < 3000) _generic.Add(selector);
            return;
        }

        foreach (var d in domains.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (d.StartsWith('~')) continue;
            var key = d.ToLowerInvariant();
            if (!_byDomain.TryGetValue(key, out var list)) _byDomain[key] = list = new List<string>();
            list.Add(selector);
        }
    }

    private static bool ApplyOptions(Rule rule, string options)
    {
        if (options.Length == 0) return true;

        foreach (var opt in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var o = opt.ToLowerInvariant();
            var negated = o.StartsWith('~');
            if (negated) o = o[1..];
            var eq = o.IndexOf('=');
            var name = eq >= 0 ? o[..eq] : o;

            switch (name)
            {
                case "third-party": case "3p": rule.ThirdParty = negated ? 0 : 1; break;
                case "first-party": case "1p": rule.ThirdParty = negated ? 1 : 0; break;
                case "important": case "match-case": break;
                case "domain":
                    if (eq < 0) return false;
                    var inc = new List<string>();
                    var exc = new List<string>();
                    foreach (var d in o[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (d.StartsWith('~')) exc.Add(d[1..]); else inc.Add(d);
                    }
                    if (inc.Count > 0) rule.Domains = inc.ToArray();
                    if (exc.Count > 0) rule.NotDomains = exc.ToArray();
                    break;
                default:
                    var bit = TypeBit(name);
                    if (bit == 0) return false; // opción no soportada: se ignora la regla entera
                    if (negated) rule.Exclude |= bit; else rule.Include |= bit;
                    break;
            }
        }
        return true;
    }

    private static int TypeBit(string name) => name switch
    {
        "script" => Script,
        "image" => Image,
        "stylesheet" or "css" => Stylesheet,
        "media" => Media,
        "font" => Font,
        "xmlhttprequest" or "xhr" => Xhr,
        "ping" => Ping,
        "other" or "object" => Other,
        _ => 0
    };

    // ───────── Consulta de red ─────────

    public bool ShouldBlock(Uri uri, string? pageHost, int type)
    {
        var url = uri.AbsoluteUri;
        var lower = url.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var third = pageHost is not null &&
                    !string.Equals(RegDomain(host), RegDomain(pageHost), StringComparison.OrdinalIgnoreCase);

        if (_exceptions.Count > 0 && _exceptions.IsMatch(url, lower, host, type, third, pageHost)) return false;
        return _blocks.IsMatch(url, lower, host, type, third, pageHost);
    }

    public static string RegDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var tld = parts[^1];
        var sld = parts[^2];
        var multi = tld.Length == 2 && (sld is "co" or "com" or "org" or "net" or "gov" or "edu" or "ac");
        return string.Join('.', parts[^(multi ? 3 : 2)..]);
    }

    // ───────── Ocultamiento de elementos ─────────

    public string BuildCosmeticCss(string host)
    {
        var selectors = new List<string>(CuratedSelectors);
        selectors.AddRange(_generic);

        var h = host.ToLowerInvariant();
        while (true)
        {
            if (_byDomain.TryGetValue(h, out var list)) selectors.AddRange(list);
            var dot = h.IndexOf('.');
            if (dot < 0) break;
            h = h[(dot + 1)..];
        }
        return ToCss(selectors);
    }

    public static string CuratedCss() => ToCss(CuratedSelectors);

    // :is() ignora los selectores inválidos en vez de descartar toda la regla
    private static string ToCss(IReadOnlyList<string> selectors)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < selectors.Count; i += 400)
        {
            sb.Append(":is(")
              .Append(string.Join(",", selectors.Skip(i).Take(400)))
              .Append("){display:none!important}");
        }
        return sb.ToString();
    }

    // ───────── Estructuras internas ─────────

    private sealed class Rule
    {
        public string Pattern = "";
        public bool HostAnchor, StartAnchor, EndAnchor;
        public int ThirdParty = -1; // -1 cualquiera, 1 solo de terceros, 0 solo del propio sitio
        public int Include, Exclude;
        public string[]? Domains, NotDomains;
        private Regex? _regex;

        public bool Applies(int type, bool thirdParty, string? pageHost)
        {
            if (ThirdParty == 1 && !thirdParty) return false;
            if (ThirdParty == 0 && thirdParty) return false;
            if (Include != 0 && (Include & type) == 0) return false;
            if ((Exclude & type) != 0) return false;

            if (Domains is not null || NotDomains is not null)
            {
                if (pageHost is null) return Domains is null;
                if (NotDomains is not null && NotDomains.Any(d => HostMatches(pageHost, d))) return false;
                if (Domains is not null && !Domains.Any(d => HostMatches(pageHost, d))) return false;
            }
            return true;
        }

        public bool Matches(string url)
        {
            _regex ??= Build();
            try { return _regex.IsMatch(url); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private Regex Build()
        {
            var sb = new StringBuilder();
            if (HostAnchor) sb.Append(@"^[a-z][a-z0-9+.\-]*://(?:[^/?#]*\.)?");
            else if (StartAnchor) sb.Append('^');

            foreach (var ch in Pattern)
            {
                switch (ch)
                {
                    case '*': sb.Append(".*"); break;
                    case '^': sb.Append(@"(?:[^a-z0-9_.%\-]|$)"); break;
                    default: sb.Append(Regex.Escape(ch.ToString())); break;
                }
            }
            if (EndAnchor) sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
        }

        private static bool HostMatches(string host, string domain) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RuleSet
    {
        private readonly Dictionary<string, List<Rule>> _byHost = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Rule>> _byToken = new(StringComparer.Ordinal);

        public int Count { get; private set; }

        public void Add(Rule rule)
        {
            var host = rule.HostAnchor ? LeadingHost(rule.Pattern) : null;
            if (host is not null)
            {
                Bucket(_byHost, host).Add(rule);
                Count++;
                return;
            }

            var token = BestToken(rule);
            if (token is not null)
            {
                Bucket(_byToken, token).Add(rule);
                Count++;
            }
        }

        public bool IsMatch(string url, string lowerUrl, string host, int type, bool third, string? pageHost)
        {
            var h = host;
            while (true)
            {
                if (_byHost.TryGetValue(h, out var list) && AnyMatch(list, url, type, third, pageHost)) return true;
                var dot = h.IndexOf('.');
                if (dot < 0) break;
                h = h[(dot + 1)..];
            }

            var seen = 0;
            foreach (var m in TokenRegex.Matches(lowerUrl).Cast<Match>())
            {
                if (++seen > 80) break;
                if (_byToken.TryGetValue(m.Value, out var list) && AnyMatch(list, url, type, third, pageHost)) return true;
            }
            return false;
        }

        private static bool AnyMatch(List<Rule> rules, string url, int type, bool third, string? pageHost)
        {
            foreach (var r in rules)
                if (r.Applies(type, third, pageHost) && r.Matches(url)) return true;
            return false;
        }

        private static List<Rule> Bucket(Dictionary<string, List<Rule>> map, string key)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Rule>();
            return list;
        }

        // ||ejemplo.com^... -> "ejemplo.com"
        private static string? LeadingHost(string pattern)
        {
            var end = pattern.IndexOfAny(new[] { '^', '/', '*', '?', ':', '|', '$' });
            var host = end < 0 ? pattern : pattern[..end];
            if (host.Length < 4 || !host.Contains('.')) return null;
            foreach (var c in host)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-')) return null;
            return host;
        }

        // Palabra completa (delimitada en ambos lados) más larga del patrón
        private static string? BestToken(Rule rule)
        {
            var p = rule.Pattern;
            string? best = null;
            foreach (var m in Regex.Matches(p, "[a-z0-9]+").Cast<Match>())
            {
                if (m.Length < 3) continue;
                var start = m.Index;
                var end = m.Index + m.Length;

                var startOk = start > 0 && p[start - 1] != '*';
                var endOk = end < p.Length ? p[end] != '*' : rule.EndAnchor;
                if (!startOk || !endOk) continue;

                if (best is null || m.Length > best.Length) best = m.Value;
            }
            return best;
        }
    }
}
