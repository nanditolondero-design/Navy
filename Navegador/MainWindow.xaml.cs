using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Navegador.Services;
using Windows.System;
using Windows.UI;

namespace Navegador;

public sealed class BrowserTab
{
    public required TabViewItem Item { get; init; }
    public required WebView2 View { get; init; }
    public bool Incognito { get; init; }
}

public sealed partial class MainWindow : Window
{
    private const string HomeUrl = "https://www.google.com";

    private readonly ProfileService _profiles = new();
    private readonly AdBlockService _adblock = new();
    private readonly List<BrowserTab> _tabs = new();
    private bool _started;

    private BrowserTab? Current =>
        Tabs.SelectedItem is TabViewItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Navegador";

        _profiles.CleanupStaleIncognitoFolders();
        _adblock.LoadDefaults();
        _ = _adblock.UpdateFromRemoteAsync();

        Closed += (_, _) => _profiles.DisposeIncognito();
        Root.Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await AddTabAsync(incognito: false, url: null);
        };
    }

    // ───────────── Pestañas ─────────────

    private async Task AddTabAsync(bool incognito, string? url)
    {
        var view = new WebView2();
        var item = new TabViewItem();
        var tab = new BrowserTab { Item = item, View = view, Incognito = incognito };
        SetTitle(tab, incognito ? "Incógnito" : "Nueva pestaña");

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
        view.CoreWebView2.Navigate(url ?? HomeUrl);
    }

    private void ConfigureTab(BrowserTab tab)
    {
        var core = tab.View.CoreWebView2;

        core.DocumentTitleChanged += (_, _) =>
            SetTitle(tab, string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle);

        core.SourceChanged += (_, _) =>
        {
            if (Current == tab) AddressBox.Text = core.Source;
            UpdateNavButtons();
        };

        core.HistoryChanged += (_, _) => UpdateNavButtons();
        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync(tab);

        // Los enlaces target=_blank se abren en una pestaña nueva del mismo modo
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _ = AddTabAsync(tab.Incognito, e.Uri);
        };

        // Adblock
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => OnWebResourceRequested(tab, e);
    }

    private void OnWebResourceRequested(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_adblock.Enabled) return;
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return; // nunca bloquear la página en sí
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;

        if (_adblock.ShouldBlock(uri))
        {
            e.Response = tab.View.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            _adblock.BlockedCount++;
            BlockedText.Text = $"🛡 {_adblock.BlockedCount}";
        }
    }

    private void SetTitle(BrowserTab tab, string title)
    {
        if (title.Length > 40) title = title[..40] + "…";
        tab.Item.Header = tab.Incognito ? "🕶 " + title : title;
    }

    private static async Task UpdateFaviconAsync(BrowserTab tab)
    {
        try
        {
            using var stream = await tab.View.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
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
        Tabs.TabItems.Remove(tab.Item);
        _tabs.Remove(tab);
        ContentHost.Children.Remove(tab.View);
        tab.View.Close();

        // Al cerrar la última pestaña de incógnito se descarta su perfil
        if (tab.Incognito && !_tabs.Any(t => t.Incognito))
            _profiles.DisposeIncognito();

        if (_tabs.Count == 0) Close();
    }

    // ───────────── Eventos de la UI ─────────────

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = AddTabAsync(false, null);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var tab = _tabs.FirstOrDefault(t => t.Item == args.Tab);
        if (tab is not null) CloseTab(tab);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var t in _tabs)
            t.View.Visibility = t.Item == Tabs.SelectedItem ? Visibility.Visible : Visibility.Collapsed;

        var cur = Current;
        if (cur is null) return;

        AddressBox.Text = cur.View.CoreWebView2?.Source ?? "";
        ApplyIncognitoLook(cur.Incognito);
        UpdateNavButtons();
    }

    private void ApplyIncognitoLook(bool incognito)
    {
        Toolbar.Background = incognito ? new SolidColorBrush(Color.FromArgb(255, 45, 45, 48)) : null;
        Toolbar.RequestedTheme = incognito ? ElementTheme.Dark : ElementTheme.Default;
        IncognitoBadge.Visibility = incognito ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNavButtons()
    {
        var core = Current?.View.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack ?? false;
        ForwardBtn.IsEnabled = core?.CanGoForward ?? false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Current?.View.CoreWebView2?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => Current?.View.CoreWebView2?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => Current?.View.CoreWebView2?.Reload();

    private void Address_GotFocus(object sender, RoutedEventArgs e) => AddressBox.SelectAll();

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Current?.View.CoreWebView2?.Navigate(ToUrl(AddressBox.Text));
        Current?.View.Focus(FocusState.Programmatic);
    }

    private static string ToUrl(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "about:blank";
        if (input.Contains("://") || input.StartsWith("about:")) return input;
        if (!input.Contains(' ') && (input.Contains('.') || input.StartsWith("localhost")))
            return "https://" + input;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    // Menú
    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = AddTabAsync(false, null);
    private void NewIncognito_Click(object sender, RoutedEventArgs e) => _ = AddTabAsync(true, null);
    private void AdblockToggle_Click(object sender, RoutedEventArgs e) => _adblock.Enabled = AdblockToggle.IsChecked;

    // Atajos (funcionan cuando el foco está en la barra, no dentro de la página web)
    private void NewTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; _ = AddTabAsync(false, null); }
    private void NewIncognitoAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; _ = AddTabAsync(true, null); }
    private void CloseTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; if (Current is { } t) CloseTab(t); }
    private void FocusAddressAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; AddressBox.Focus(FocusState.Programmatic); }
    private void ReloadAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; Current?.View.CoreWebView2?.Reload(); }
}
