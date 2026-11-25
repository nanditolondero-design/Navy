namespace Navegador.Services;

public sealed class AppSettings
{
    private const string FileName = "settings.json";

    public string SearchEngine { get; set; } = "Google";

    // Escudos (estilo Brave)
    public bool AdblockEnabled { get; set; } = true;
    public bool HttpsUpgrade { get; set; } = true;
    public bool StripTrackingParams { get; set; } = true;
    public bool SendPrivacySignals { get; set; } = true;   // Sec-GPC y DNT
    public int TrackingPrevention { get; set; } = 2;       // 0 ninguna, 1 básica, 2 equilibrada, 3 estricta
    public List<string> ShieldsOffHosts { get; set; } = new();

    // Navegación
    public bool RestoreSession { get; set; } = true;
    public bool StartPage { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    public bool MemorySaver { get; set; } = true;
    public Dictionary<string, double> SiteZoom { get; set; } = new();

    // Permisos recordados por sitio: "host|Microphone" -> "allow" | "deny"
    public Dictionary<string, string> SitePermissions { get; set; } = new();

    // Explorador de archivos (cajón lateral)
    public string ExplorerPath { get; set; } = "";
    public bool ExplorerShowHidden { get; set; }
    public string ExplorerSort { get; set; } = "Name";
    public bool ExplorerAscending { get; set; } = true;

    // Descargas
    public bool AskDownloadLocation { get; set; } = true;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;

    public static string DefaultDownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static AppSettings Instance { get; } = JsonStore.Load<AppSettings>(FileName);

    public void Save() => JsonStore.Save(FileName, this);
}
