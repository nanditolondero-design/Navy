using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Navegador.Models;
using Navegador.Services;
using Windows.Storage.Pickers;

namespace Navegador.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly BrowserServices _services;
    private readonly AppSettings _cfg = AppSettings.Instance;
    private bool _loading = true;

    public SettingsPage(BrowserServices services)
    {
        _services = services;
        InitializeComponent();

        SearchBox.ItemsSource = SearchEngines.Names;
        SearchBox.SelectedItem = _cfg.SearchEngine;

        TrackingBox.ItemsSource = new[] { "Desactivada", "Básica", "Equilibrada", "Estricta" };
        TrackingBox.SelectedIndex = Math.Clamp(_cfg.TrackingPrevention, 0, 3);

        RestoreSwitch.IsOn = _cfg.RestoreSession;
        StartPageSwitch.IsOn = _cfg.StartPage;
        BarSwitch.IsOn = _cfg.ShowBookmarksBar;
        SaverSwitch.IsOn = _cfg.MemorySaver;
        LoadPermissions();

        AdblockSwitch.IsOn = _cfg.AdblockEnabled;
        HttpsSwitch.IsOn = _cfg.HttpsUpgrade;
        StripSwitch.IsOn = _cfg.StripTrackingParams;
        SignalsSwitch.IsOn = _cfg.SendPrivacySignals;
        AskSwitch.IsOn = _cfg.AskDownloadLocation;
        FolderText.Text = _cfg.DownloadFolder;

        var profiles = ChromeImporter.FindProfiles();
        ProfileBox.ItemsSource = profiles;
        if (profiles.Count > 0)
        {
            ProfileBox.SelectedIndex = 0;
        }
        else
        {
            ImportBtn.IsEnabled = false;
            ImportResult.Text = "No encontré Google Chrome con datos en este equipo.";
        }

        _loading = false;
    }

    private void Apply()
    {
        if (_loading) return;
        _cfg.SearchEngine = SearchBox.SelectedItem as string ?? "Google";
        _cfg.RestoreSession = RestoreSwitch.IsOn;
        _cfg.StartPage = StartPageSwitch.IsOn;
        _cfg.ShowBookmarksBar = BarSwitch.IsOn;
        _cfg.MemorySaver = SaverSwitch.IsOn;
        _cfg.AdblockEnabled = AdblockSwitch.IsOn;
        _cfg.HttpsUpgrade = HttpsSwitch.IsOn;
        _cfg.StripTrackingParams = StripSwitch.IsOn;
        _cfg.SendPrivacySignals = SignalsSwitch.IsOn;
        _cfg.TrackingPrevention = Math.Max(0, TrackingBox.SelectedIndex);
        _cfg.AskDownloadLocation = AskSwitch.IsOn;
        _cfg.Save();
        _services.SettingsChanged();
    }

    private void LoadPermissions()
    {
        var items = _cfg.SitePermissions
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var parts = kv.Key.Split('|');
                var host = parts[0];
                var kind = parts.Length > 1 ? parts[1] : "";
                return new PermissionEntry
                {
                    Key = kv.Key,
                    Text = $"{host} · {PermissionInfo.Label(kind)}: {(kv.Value == "allow" ? "permitido" : "bloqueado")}"
                };
            })
            .ToList();
        PermList.ItemsSource = items;
        PermEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PermRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PermissionEntry entry) return;
        _cfg.SitePermissions.Remove(entry.Key);
        _cfg.Save();
        LoadPermissions();
    }

    private void DefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        var ok = DefaultBrowser.Register();
        DefaultResult.Text = ok
            ? "Listo: Navegador ya figura en la lista de Windows. En la pantalla que se abrió, elegilo como navegador predeterminado."
            : "No se pudo registrar la app en Windows.";
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    private void Setting_Toggled(object sender, RoutedEventArgs e) => Apply();
    private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e) => Apply();

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_services.Owner));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _cfg.DownloadFolder = folder.Path;
        FolderText.Text = folder.Path;
        _cfg.Save();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ChromeProfile profile) return;

        ImportBtn.IsEnabled = false;
        ImportResult.Text = "Importando…";
        try
        {
            var parts = new List<string>();
            if (BookmarksCheck.IsChecked == true)
            {
                var bookmarks = await Task.Run(() => ChromeImporter.ReadBookmarks(profile));
                parts.Add($"Marcadores nuevos: {_services.Bookmarks.Merge(bookmarks)}");
            }
            if (HistoryCheck.IsChecked == true)
            {
                var history = await Task.Run(() => ChromeImporter.ReadHistory(profile));
                parts.Add($"Entradas de historial nuevas: {_services.History.Merge(history)}");
            }
            ImportResult.Text = parts.Count == 0 ? "Elegí qué querés importar." : string.Join(" · ", parts);
        }
        catch (IOException)
        {
            ImportResult.Text = "Chrome tiene los datos bloqueados. Cerralo por completo (también en la bandeja del sistema) y probá de nuevo.";
        }
        catch (Exception ex)
        {
            ImportResult.Text = "No se pudo importar: " + ex.Message;
        }
        finally
        {
            ImportBtn.IsEnabled = true;
        }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _services.ClearBrowsingDataAsync();
            ClearResult.Text = "Listo: se borraron cookies, caché y datos de sitios.";
        }
        catch (Exception ex)
        {
            ClearResult.Text = "No se pudo borrar: " + ex.Message;
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _services.History.Clear();
        ClearResult.Text = "Historial borrado.";
    }
}
