using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Navegador.Models;
using Navegador.Services;
using Navegador.Views;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Navegador;

public sealed partial class MainWindow : Window
{
    private readonly ProfileService _profiles = new();
    private readonly AdBlockService _adblock = new();
    private readonly HistoryService _history = new();
    private readonly BookmarksService _bookmarks = new();
    private readonly AppSettings _settings = AppSettings.Instance;
    private readonly BrowserServices _services;
    private readonly List<BrowserTab> _tabs = new();

    private CoreWebView2Profile? _normalProfile;
    private bool _started;
    private bool _updatingShield;
    private bool _addressFocused;
    private bool _drawerOpen;
    private string _addressUrl = "";

    private BrowserTab? CurrentTab =>
        Tabs.SelectedItem is TabViewItem item ? _tabs.FirstOrDefault(t => ReferenceEquals(t.Item, item)) : null;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Navegador";
        ExtendsContentIntoTitleBar = true; // la barra de pestañas hace de barra de título
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };

        _services = new BrowserServices
        {
            History = _history,
            Bookmarks = _bookmarks,
            Owner = this,
            OpenUrl = url => { _ = AddTabAsync(false, url); },
            SettingsChanged = ApplySettings,
            ClearBrowsingDataAsync = ClearBrowsingDataAsync
        };

        _profiles.CleanupStaleIncognitoFolders();
        _adblock.Enabled = _settings.AdblockEnabled;
        _adblock.LoadDefaults();
        _ = _adblock.UpdateFromRemoteAsync();
        _ = _adblock.UpdateFilterListsAsync();
        _bookmarks.Changed += UpdateStar;
        InitFeatures();

        Explorer.OpenInBrowser = path => { _ = AddTabAsync(false, new Uri(path).AbsoluteUri); };
        Explorer.CloseRequested += () => SetDrawer(false);

        Closed += (_, _) =>
        {
            _history.Flush();
            _profiles.DisposeIncognito();
        };

        Root.Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;

            SetTitleBar(CustomDragRegion);
            try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico")); } catch { }
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            }

            // Argumentos de línea de comandos (los usa el workflow de capturas)
            var cli = ParseArgs();
            if (cli.TryGetValue("size", out var size))
            {
                var dims = size.Split('x');
                if (dims.Length == 2 && int.TryParse(dims[0], out var w) && int.TryParse(dims[1], out var h))
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, w, h));
            }

            string? startUrl = cli.TryGetValue("url", out var cliUrl) && cliUrl.Length > 0 ? cliUrl : null;
            await StartTabsAsync(cli, startUrl, BareArg());

            if (cli.TryGetValue("open", out var open))
            {
                TabKind? kind = open switch
                {
                    "settings" => TabKind.Settings,
                    "history" => TabKind.History,
                    "bookmarks" => TabKind.Bookmarks,
                    "downloads" => TabKind.Downloads,
                    _ => null
                };
                if (kind is { } k) OpenInternalTab(k);
            }

            if (cli.TryGetValue("drawer", out var drawerPath))
                SetDrawer(true, drawerPath.Length > 0 ? drawerPath : null);

            if (cli.ContainsKey("demo-shields"))
            {
                await Task.Delay(6000);
                ShieldBtn.Flyout?.ShowAt(ShieldBtn);
            }
        };
    }

    private static Dictionary<string, string> ParseArgs()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (!arg.StartsWith("--")) continue;
            var body = arg[2..];
            var i = body.IndexOf('=');
            if (i < 0) map[body] = "";
            else map[body[..i]] = body[(i + 1)..];
        }
        return map;
    }

    // ───────────── Pestañas ─────────────

    private string HomeUrl => SearchEngines.Home(_settings.SearchEngine);

    private async Task AddTabAsync(bool incognito, string? url)
    {
        var view = new WebView2();
        var item = new TabViewItem();
        var tab = new BrowserTab { Item = item, Content = view, View = view, Incognito = incognito };
        SetTitle(tab, incognito ? "Incógnito" : "Nueva pestaña");
        AttachTabMenu(tab);

        _tabs.Add(tab);
        ContentHost.Children.Add(view);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item; // muestra esta pestaña y oculta las demás

        try
        {
            var env = await _profiles.GetEnvironmentAsync(incognito);
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            SetTitle(tab, "Error: " + ex.Message);
            return;
        }

        ConfigureTab(tab);
        ConfigureFeatures(tab);
        NavigateInitial(tab, url);
    }

    private void OpenInternalTab(TabKind kind)
    {
        var existing = _tabs.FirstOrDefault(t => t.Kind == kind);
        if (existing is not null)
        {
            Tabs.SelectedItem = existing.Item;
            return;
        }

        FrameworkElement page = kind switch
        {
            TabKind.Downloads => new DownloadsPage(),
            TabKind.History => new HistoryPage(_services),
            TabKind.Bookmarks => new BookmarksPage(_services),
            _ => new SettingsPage(_services)
        };

        var (title, glyph) = kind switch
        {
            TabKind.Downloads => ("Descargas", "\uE896"),
            TabKind.History => ("Historial", "\uE81C"),
            TabKind.Bookmarks => ("Marcadores", "\uE734"),
            _ => ("Configuración", "\uE713")
        };

        var item = new TabViewItem
        {
            Header = title,
            IconSource = new FontIconSource { Glyph = glyph }
        };
        var tab = new BrowserTab { Item = item, Content = page, Kind = kind };
        AttachTabMenu(tab);

        _tabs.Add(tab);
        ContentHost.Children.Add(page);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    private void ConfigureTab(BrowserTab tab)
    {
        var core = tab.View!.CoreWebView2;

        if (!tab.Incognito)
        {
            _normalProfile = core.Profile;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = true;
        }
        ApplyTrackingPrevention(core);

        core.DocumentTitleChanged += (_, _) =>
            SetTitle(tab, string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle);

        core.SourceChanged += (_, _) =>
        {
            tab.PageHost = HostOf(core.Source);
            if (ReferenceEquals(CurrentTab, tab)) SetAddress(core.Source);
            UpdateNavButtons();
            UpdateStar();
        };

        core.HistoryChanged += (_, _) => UpdateNavButtons();
        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync(tab);

        // Los enlaces target=_blank se abren en una pestaña nueva del mismo modo
        core.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            _ = AddTabAsync(tab.Incognito, e.Uri);
        };

        core.NavigationStarting += (s, e) => OnNavigationStarting(tab, e);
        core.NavigationCompleted += (s, e) => OnNavigationCompleted(tab, e);
        core.DOMContentLoaded += async (s, e) => await InjectCosmeticFilterAsync(tab);
        core.DownloadStarting += async (s, e) => await OnDownloadStartingAsync(e);

        // Escudos: bloqueo de anuncios/rastreadores y señales de privacidad
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) => OnWebResourceRequested(tab, e);
    }

    private void ApplyTrackingPrevention(CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredTrackingPreventionLevel =
                (CoreWebView2TrackingPreventionLevel)Math.Clamp(_settings.TrackingPrevention, 0, 3);
        }
        catch
        {
        }
    }

    private void ApplySettings()
    {
        _adblock.Enabled = _settings.AdblockEnabled;
        RefreshBookmarksBar();
        foreach (var t in _tabs)
        {
            var core = t.View?.CoreWebView2;
            if (core is not null) ApplyTrackingPrevention(core);
        }
    }

    private async Task ClearBrowsingDataAsync()
    {
        var profile = _normalProfile;
        if (profile is null) return;
        await profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.DiskCache);
    }

    // ───────────── Navegación y escudos ─────────────

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs e)
    {
        tab.Blocked = 0;
        UpdateShield();

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        var target = uri;

        if (_settings.StripTrackingParams)
            target = TrackingParams.Strip(target);

        if (_settings.HttpsUpgrade && !tab.SkipUpgrade &&
            target.Scheme == Uri.UriSchemeHttp && IsUpgradable(target))
        {
            tab.HttpFallbackUrl = target.AbsoluteUri;
            target = new UriBuilder(target) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
            tab.UpgradedHttps = target.AbsoluteUri;
        }
        tab.SkipUpgrade = false;
        tab.PageHost = target.Host;

        if (target.AbsoluteUri != uri.AbsoluteUri)
        {
            e.Cancel = true;
            var url = target.AbsoluteUri;
            DispatcherQueue.TryEnqueue(() => tab.View?.CoreWebView2?.Navigate(url));
            return;
        }

        tab.Loading = true;
        UpdateLoading();
    }

    private void OnNavigationCompleted(BrowserTab tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        var core = tab.View!.CoreWebView2;

        // La navegación que cancelamos para redirigirla no cuenta
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            tab.Loading = false;
            UpdateLoading();
            return;
        }

        // Si falló el intento por HTTPS, se vuelve a HTTP una sola vez
        if (!e.IsSuccess && tab.HttpFallbackUrl is { } fallback && SameHost(core.Source, tab.UpgradedHttps))
        {
            tab.HttpFallbackUrl = null;
            tab.UpgradedHttps = null;
            tab.SkipUpgrade = true;
            core.Navigate(fallback);
            return;
        }
        tab.HttpFallbackUrl = null;
        tab.UpgradedHttps = null;
        tab.Loading = false;
        UpdateLoading();

        if (e.IsSuccess && !tab.Incognito && HostOf(core.Source) is not null)
        {
            var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle;
            _history.Add(title, core.Source);
        }
        UpdateStar();
    }

    private void OnWebResourceRequested(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var host = tab.PageHost;
        if (host is not null && _settings.ShieldsOffHosts.Contains(host)) return; // escudos apagados en este sitio

        if (_settings.SendPrivacySignals)
        {
            e.Request.Headers.SetHeader("Sec-GPC", "1");
            e.Request.Headers.SetHeader("DNT", "1");
        }

        if (!_adblock.Enabled) return;
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return; // nunca bloquear la página en sí
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;

        if (_adblock.ShouldBlock(uri, host, TypeBit(e.ResourceContext)))
        {
            e.Response = tab.View!.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            tab.Blocked++;
            if (ReferenceEquals(CurrentTab, tab)) UpdateShield();
        }
    }

    private static int TypeBit(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Script => FilterEngine.Script,
        CoreWebView2WebResourceContext.Image => FilterEngine.Image,
        CoreWebView2WebResourceContext.Stylesheet => FilterEngine.Stylesheet,
        CoreWebView2WebResourceContext.Media => FilterEngine.Media,
        CoreWebView2WebResourceContext.Font => FilterEngine.Font,
        CoreWebView2WebResourceContext.XmlHttpRequest => FilterEngine.Xhr,
        CoreWebView2WebResourceContext.Fetch => FilterEngine.Xhr,
        CoreWebView2WebResourceContext.Ping => FilterEngine.Ping,
        _ => FilterEngine.Other
    };

    // Oculta los espacios publicitarios que quedan en la página (banners, contenedores vacíos)
    private async Task InjectCosmeticFilterAsync(BrowserTab tab)
    {
        if (!_adblock.Enabled) return;
        var host = tab.PageHost;
        if (host is null || _settings.ShieldsOffHosts.Contains(host)) return;

        var css = _adblock.CosmeticCss(host);
        if (css.Length == 0) return;

        var js = "(()=>{const s=document.createElement('style');s.textContent=" +
                 System.Text.Json.JsonSerializer.Serialize(css) +
                 ";(document.head||document.documentElement).appendChild(s);})();";
        try
        {
            await tab.View!.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch
        {
        }
    }

    private static bool IsUpgradable(Uri u) =>
        u.IsDefaultPort &&
        u.Host.Contains('.') &&
        !System.Net.IPAddress.TryParse(u.Host, out _) &&
        !u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !u.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.Host
            : null;

    private static bool SameHost(string? a, string? b)
    {
        var ha = HostOf(a);
        var hb = HostOf(b);
        return ha is not null && hb is not null && string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── Descargas ─────────────

    private async Task OnDownloadStartingAsync(CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true; // sin el cuadro de descarga predeterminado de WebView2

            var suggested = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggested)) suggested = "descarga";

            string? target;
            if (_settings.AskDownloadLocation)
            {
                target = await PickSavePathAsync(suggested);
            }
            else
            {
                Directory.CreateDirectory(_settings.DownloadFolder);
                target = UniquePath(Path.Combine(_settings.DownloadFolder, suggested));
            }

            if (target is null)
            {
                e.Cancel = true;
                return;
            }

            e.ResultFilePath = target;
            DownloadsStore.Items.Insert(0, new DownloadItem(e.DownloadOperation, target));
            OpenInternalTab(TabKind.Downloads);
        }
        catch
        {
            e.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName)
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var ext = Path.GetExtension(suggestedName);
        if (string.IsNullOrEmpty(ext))
            picker.FileTypeChoices.Add("Archivo", new List<string> { "." });
        else
            picker.FileTypeChoices.Add($"Archivo {ext.TrimStart('.').ToUpperInvariant()}", new List<string> { ext });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        // El selector crea un archivo vacío; se borra para que WebView2 escriba el suyo
        try
        {
            if (File.Exists(file.Path) && new FileInfo(file.Path).Length == 0) File.Delete(file.Path);
        }
        catch
        {
        }
        return file.Path;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ───────────── Interfaz ─────────────

    private void SetTitle(BrowserTab tab, string title)
    {
        tab.Title = title;
        RefreshHeader(tab);
    }

    private static async Task UpdateFaviconAsync(BrowserTab tab)
    {
        try
        {
            using var stream = await tab.View!.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            tab.Item.IconSource = new ImageIconSource { ImageSource = bmp };
        }
        catch
        {
            // sin favicon: se deja el ícono anterior
        }
    }

    private void CloseTab(BrowserTab tab)
    {
        RememberClosed(tab);
        Tabs.TabItems.Remove(tab.Item);
        _tabs.Remove(tab);
        ContentHost.Children.Remove(tab.Content);
        tab.View?.Close();

        // Al cerrar la última pestaña de incógnito se descarta su perfil
        if (tab.Incognito && !_tabs.Any(t => t.Incognito))
            _profiles.DisposeIncognito();

        SaveSessionSoon();
        if (_tabs.Count == 0) Close();
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = AddTabAsync(false, null);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Item, args.Tab));
        if (tab is not null) CloseTab(tab);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var t in _tabs)
            t.Content.Visibility = ReferenceEquals(t.Item, Tabs.SelectedItem) ? Visibility.Visible : Visibility.Collapsed;

        var cur = CurrentTab;
        if (cur is null) return;

        SetAddress(cur.View?.CoreWebView2?.Source);
        UpdateLoading();
        ApplyIncognitoLook(cur.Incognito);
        UpdateNavButtons();
        UpdateShield();
        UpdateStar();

        if (cur.Content is IRefreshable refreshable) refreshable.Refresh();
        OnSelectionFeatures(cur);
    }

    private void ApplyIncognitoLook(bool incognito)
    {
        Toolbar.Background = incognito ? new SolidColorBrush(Color.FromArgb(255, 45, 45, 48)) : null;
        Toolbar.RequestedTheme = incognito ? ElementTheme.Dark : ElementTheme.Default;
        IncognitoBadge.Visibility = incognito ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNavButtons()
    {
        var core = CurrentTab?.View?.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack ?? false;
        ForwardBtn.IsEnabled = core?.CanGoForward ?? false;
        ReloadBtn.IsEnabled = core is not null;
    }

    private void UpdateShield() => ShieldBtn.Content = $"🛡 {CurrentTab?.Blocked ?? 0}";

    private void UpdateStar()
    {
        var core = CurrentTab?.View?.CoreWebView2;
        var url = core?.Source;
        var isWeb = HostOf(url) is not null;
        StarBtn.IsEnabled = isWeb;
        StarIcon.Glyph = isWeb && _bookmarks.Contains(url) ? "\uE735" : "\uE734";
    }

    private void Back_Click(object sender, RoutedEventArgs e) => CurrentTab?.View?.CoreWebView2?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => CurrentTab?.View?.CoreWebView2?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => CurrentTab?.View?.CoreWebView2?.Reload();

    // Como Safari: sin foco se muestra solo el dominio; al enfocar, la dirección completa
    private void SetAddress(string? url)
    {
        if (url == "about:blank") url = null;
        _addressUrl = url ?? "";
        if (!_addressFocused) ShowAddressDisplay();

        var scheme = Uri.TryCreate(_addressUrl, UriKind.Absolute, out var u) ? u.Scheme : "";
        var isWeb = scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps;
        LockIcon.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        LockIcon.Glyph = scheme == Uri.UriSchemeHttps ? "\uE72E" : "\uE785";
    }

    private void ShowAddressDisplay()
    {
        var host = HostOf(_addressUrl);
        AddressBox.Text = host is null ? _addressUrl : (host.StartsWith("www.") ? host[4..] : host);
    }

    private void Address_GotFocus(object sender, RoutedEventArgs e)
    {
        _addressFocused = true;
        AddressBox.Text = _addressUrl;
        AddressBox.TextAlignment = TextAlignment.Left;
        AddressBox.SelectAll();
    }

    private void Address_LostFocus(object sender, RoutedEventArgs e)
    {
        _addressFocused = false;
        AddressBox.TextAlignment = TextAlignment.Center;
        ShowAddressDisplay();
        if (!_overSuggest) HideSuggestions();
    }

    private void UpdateLoading() =>
        LoadBar.Visibility = CurrentTab?.Loading == true ? Visibility.Visible : Visibility.Collapsed;

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e) => HandleAddressKey(e);

    private string ToUrl(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "about:blank";
        if (input.Contains("://") || input.StartsWith("about:")) return input;
        if (!input.Contains(' ') && (input.Contains('.') || input.StartsWith("localhost")))
            return "https://" + input;
        return SearchEngines.SearchUrl(_settings.SearchEngine, input);
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        var core = CurrentTab?.View?.CoreWebView2;
        if (core is null || HostOf(core.Source) is null) return;

        if (_bookmarks.Contains(core.Source)) _bookmarks.Remove(core.Source);
        else _bookmarks.Add(core.DocumentTitle, core.Source);
    }

    // Escudos del sitio actual
    private void ShieldFlyout_Opening(object? sender, object e)
    {
        var tab = CurrentTab;
        var host = tab?.PageHost;

        _updatingShield = true;
        SiteShieldToggle.IsEnabled = host is not null;
        ShieldHostText.Text = host ?? "Esta página no es un sitio web";
        SiteShieldToggle.IsOn = host is null || !_settings.ShieldsOffHosts.Contains(host);
        ShieldCountText.Text = $"{tab?.Blocked ?? 0} solicitudes bloqueadas en esta página";
        _updatingShield = false;
    }

    private void SiteShield_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingShield) return;
        var tab = CurrentTab;
        var host = tab?.PageHost;
        if (tab is null || host is null) return;

        if (SiteShieldToggle.IsOn) _settings.ShieldsOffHosts.Remove(host);
        else if (!_settings.ShieldsOffHosts.Contains(host)) _settings.ShieldsOffHosts.Add(host);
        _settings.Save();

        tab.View?.CoreWebView2?.Reload();
    }

    // Cajón lateral con el explorador de archivos
    private void SetDrawer(bool open, string? path = null)
    {
        _drawerOpen = open;
        Explorer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DrawerColumn.Width = new GridLength(open ? 540 : 0);
        DropHandleIcon.Glyph = open ? "\uE76B" : "\uE76C";
        if (open) Explorer.Open(path);
    }

    private void Files_Click(object sender, RoutedEventArgs e) => SetDrawer(!_drawerOpen);

    // Menú y botones
    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = AddTabAsync(false, null);
    private void NewIncognito_Click(object sender, RoutedEventArgs e) => _ = AddTabAsync(true, null);
    private void DownloadsBtn_Click(object sender, RoutedEventArgs e) => OpenInternalTab(TabKind.Downloads);
    private void History_Click(object sender, RoutedEventArgs e) => OpenInternalTab(TabKind.History);
    private void Bookmarks_Click(object sender, RoutedEventArgs e) => OpenInternalTab(TabKind.Bookmarks);
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenInternalTab(TabKind.Settings);

    // Atajos (funcionan cuando el foco está en la barra, no dentro de la página web)
    private void NewTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; _ = AddTabAsync(false, null); }
    private void NewIncognitoAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; _ = AddTabAsync(true, null); }
    private void CloseTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; if (CurrentTab is { } t) CloseTab(t); }
    private void FocusAddressAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; AddressBox.Focus(FocusState.Programmatic); }
    private void BookmarkAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; Star_Click(s, new RoutedEventArgs()); }
    private void HistoryAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; OpenInternalTab(TabKind.History); }
    private void DownloadsAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; OpenInternalTab(TabKind.Downloads); }
    private void FilesAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; SetDrawer(!_drawerOpen); }
    private void ReloadAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; CurrentTab?.View?.CoreWebView2?.Reload(); }
}
