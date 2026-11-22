namespace Navegador.Services;

public static class SearchEngines
{
    public static readonly string[] Names = { "Google", "DuckDuckGo", "Brave Search", "Bing" };

    public static string Home(string name) => name switch
    {
        "DuckDuckGo" => "https://duckduckgo.com",
        "Brave Search" => "https://search.brave.com",
        "Bing" => "https://www.bing.com",
        _ => "https://www.google.com"
    };

    public static string SearchUrl(string name, string query)
    {
        var q = Uri.EscapeDataString(query);
        return name switch
        {
            "DuckDuckGo" => "https://duckduckgo.com/?q=" + q,
            "Brave Search" => "https://search.brave.com/search?q=" + q,
            "Bing" => "https://www.bing.com/search?q=" + q,
            _ => "https://www.google.com/search?q=" + q
        };
    }
}
