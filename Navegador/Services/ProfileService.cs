using Microsoft.Web.WebView2.Core;

namespace Navegador.Services;

/// <summary>
/// Perfil normal: carpeta persistente en %LOCALAPPDATA%\Navegador\WebView2Data.
/// Incógnito: carpeta temporal propia (cookies, caché y almacenamiento separados)
/// que se borra al cerrar la última pestaña de incógnito.
/// </summary>
public sealed class ProfileService
{
    private const string IncognitoPrefix = "Navegador_incognito_";

    private static readonly string NormalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Navegador", "WebView2Data");

    private Task<CoreWebView2Environment>? _normalTask;
    private Task<CoreWebView2Environment>? _incognitoTask;
    private string? _incognitoDir;

    public Task<CoreWebView2Environment> GetEnvironmentAsync(bool incognito)
    {
        if (!incognito)
            return _normalTask ??= CreateAsync(NormalDir);

        if (_incognitoTask is null)
        {
            _incognitoDir = Path.Combine(Path.GetTempPath(), IncognitoPrefix + Guid.NewGuid().ToString("N"));
            _incognitoTask = CreateAsync(_incognitoDir);
        }
        return _incognitoTask;
    }

    private static async Task<CoreWebView2Environment> CreateAsync(string userDataDir)
    {
        Directory.CreateDirectory(userDataDir);
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, new CoreWebView2EnvironmentOptions());
    }

    public void DisposeIncognito()
    {
        var dir = _incognitoDir;
        _incognitoTask = null;
        _incognitoDir = null;
        if (dir is not null) _ = DeleteWithRetryAsync(dir);
    }

    public void CleanupStaleIncognitoFolders()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), IncognitoPrefix + "*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    // WebView2 tarda un momento en soltar los archivos después de cerrar
    private static async Task DeleteWithRetryAsync(string dir)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return;
            }
            catch { }
        }
    }
}
