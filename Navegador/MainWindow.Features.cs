using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Navegador.Models;
using Navegador.Services;
using Navegador.Ui;
using Windows.Storage.Pickers;
using Windows.System;

namespace Navegador;

/// <summary>Funciones de uso diario: búsqueda en la página, zoom, impresión, sesión, sugerencias, marcadores y más.</summary>
public sealed partial class MainWindow
{
    private readonly SessionService _session = new();
    private readonly Stack<string> _closedTabs = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sessionTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _suspendTimer;
    private BrowserTab? _prevTab;
    private List<Suggestion> _suggestions = new();
    private bool _overSuggest;
    private bool _restoring;
    private bool _fullscreen;
    private DateTime _lastShortcut = DateTime.MinValue;

    private void InitFeatures()
    {
        _sessionTimer = DispatcherQueue.CreateTimer();
        _sessionTimer.Interval = TimeSpan.FromSeconds(2);
        _sessionTimer.IsRepeating = false;
        _sessionTimer.Tick += (_, _) => SaveSessionNow();

        _suspendTimer = DispatcherQueue.CreateTimer();
        _suspendTimer.Interval = TimeSpan.FromMinutes(1);
        _suspendTimer.IsRepeating = true;
        _suspendTimer.Tick += (_, _) => SuspendIdleTabs();
        _suspendTimer.Start();

        InitPasswords();
        _bookmarks.Changed += RefreshBookmarksBar;
        AppWindow.Closing += (_, _) => SaveSessionNow();
        RefreshBookmarksBar();
    }

    // ───────────── Arranque: restaurar pestañas o página de inicio ─────────────

    private static string? BareArg()
    {
        foreach (var a in Environment.GetCommandLineArgs().Skip(1))
        {
            if (a.StartsWith("--")) continue;
            if (a.StartsWith("http://") || a.StartsWith("https://")) return a;
            try
            {
                if (File.Exists(a)) return new Uri(Path.GetFullPath(a)).AbsoluteUri;
            }
            catch
            {
            }
        }
        return null;
    }

    private async Task StartTabsAsync(Dictionary<string, string> cli, string? startUrl, string? bare)
    {
        // Con argumentos de línea de comandos (modo capturas) no se restaura nada
        var data = cli.Count == 0 && _settings.RestoreSession ? _session.Load() : null;

        if (data is { Urls.Count: > 0 })
        {
            _restoring = true;
            try
            {
                var urls = data.Urls.Take(15).ToList();
                foreach (var url in urls) await AddTabAsync(false, url);

                var webTabs = _tabs.Where(t => t.Kind == TabKind.Web && !t.Incognito).ToList();
                var index = Math.Clamp(data.Selected, 0, Math.Max(0, webTabs.Count - 1));
                if (webTabs.Count > 0) Tabs.SelectedItem = webTabs[index].Item;
                if (bare is not null) await AddTabAsync(false, bare);
            }
            finally
            {
                _ = Task.Delay(4000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    _restoring = false;
                    SaveSessionNow();
                }));
            }
        }
        else
        {
            await AddTabAsync(cli.ContainsKey("incognito"), startUrl ?? bare);
        }
    }

    private void NavigateInitial(BrowserTab tab, string? url)
    {
        var core = tab.View!.CoreWebView2;
        if (url is null && _settings.StartPage)
            core.NavigateToString(BuildStartPage(tab.Incognito));
        else
            core.Navigate(url ?? HomeUrl);
    }

    private string BuildStartPage(bool incognito)
    {
        var tiles = new List<(string Title, string Url)>();
        foreach (var b in _bookmarks.All.Take(8)) tiles.Add((b.Title, b.Url));

        if (!incognito && tiles.Count < 8)
        {
            var top = _history.Search(null, 3000)
                .Where(h => HostOf(h.Url) is not null)
                .GroupBy(h => HostOf(h.Url)!)
                .OrderByDescending(g => g.Count())
                .Take(8 - tiles.Count);
            foreach (var g in top)
                tiles.Add((g.Key.StartsWith("www.") ? g.Key[4..] : g.Key, "https://" + g.Key + "/"));
        }
        return StartPage.Build(_settings.SearchEngine, tiles, incognito);
    }

    // ───────────── Sesión y pestañas cerradas ─────────────

    private void SaveSessionSoon()
    {
        _sessionTimer?.Stop();
        _sessionTimer?.Start();
    }

    private void SaveSessionNow()
    {
        if (_restoring) return;
        try
        {
            var urls = new List<string>();
            var selected = 0;
            foreach (var t in _tabs)
            {
                if (t.Incognito || t.Kind != TabKind.Web) continue;
                var source = t.View?.CoreWebView2?.Source;
                if (HostOf(source) is null) continue;
                if (ReferenceEquals(t.Item, Tabs.SelectedItem)) selected = urls.Count;
                urls.Add(source!);
            }
            _session.Save(urls, selected);
        }
        catch
        {
        }
    }

    private void RememberClosed(BrowserTab tab)
    {
        if (tab.Incognito || tab.Kind != TabKind.Web) return;
        try
        {
            var source = tab.View?.CoreWebView2?.Source;
            if (HostOf(source) is null) return;
            _closedTabs.Push(source!);
            while (_closedTabs.Count > 25)
            {
                var keep = _closedTabs.ToArray().Take(25).Reverse().ToArray();
                _closedTabs.Clear();
                foreach (var u in keep) _closedTabs.Push(u);
            }
        }
        catch
        {
        }
    }

    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        _ = AddTabAsync(false, _closedTabs.Pop());
    }

    // ───────────── Hooks desde ConfigureTab / selección ─────────────

    private const string KeyBridgeTemplate = """
(() => {
  const N = '__NONCE__';
  const send = a => { try { window.chrome.webview.postMessage('navegador:' + N + ':' + a); } catch (e) {} };
  addEventListener('keydown', e => {
    const k = (e.key || '').toLowerCase();
    const c = e.ctrlKey || e.metaKey;
    let a = null;
    if (c && !e.altKey) {
      if (e.shiftKey) {
        if (k === 'n') a = 'incognito';
        else if (k === 't') a = 'reopen';
        else if (k === 'b') a = 'bookmarksbar';
        else if (k === 'tab') a = 'prevtab';
      } else if (k === 't') a = 'newtab';
      else if (k === 'w') a = 'closetab';
      else if (k === 'l') a = 'address';
      else if (k === 'd') a = 'bookmark';
      else if (k === 'h') a = 'history';
      else if (k === 'j') a = 'downloads';
      else if (k === 'e') a = 'files';
      else if (k === 'f') a = 'find';
      else if (k === 'p') a = 'print';
      else if (k === 'tab') a = 'nexttab';
      else if (k === '=' || k === '+') a = 'zoomin';
      else if (k === '-') a = 'zoomout';
      else if (k === '0') a = 'zoomreset';
      else if (k.length === 1 && k >= '1' && k <= '9') a = 'tab' + k;
    } else if (!c && !e.altKey && !e.shiftKey) {
      if (e.key === 'F12') a = 'devtools';
      else if (e.key === 'F11') a = 'fullscreen';
    }
    if (a) { e.preventDefault(); e.stopPropagation(); send(a); }
  }, true);
  addEventListener('wheel', e => {
    if (e.ctrlKey) { e.preventDefault(); send(e.deltaY < 0 ? 'zoomin' : 'zoomout'); }
  }, { passive: false, capture: true });
})();
""";

    private void ConfigureFeatures(BrowserTab tab)
    {
        var core = tab.View!.CoreWebView2;

        // Atajos de teclado que funcionan también con el foco dentro de la página
        tab.Nonce = Guid.NewGuid().ToString("N");
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(KeyBridgeTemplate.Replace("__NONCE__", tab.Nonce));
        RegisterCredBridge(tab, core);
        core.WebMessageReceived += (_, e) => OnWebMessage(tab, e);

        core.SourceChanged += (_, _) =>
        {
            ApplySiteZoom(tab);
            SaveSessionSoon();
        };
        core.DOMContentLoaded += (sender, args) => { _ = ApplyZoomAsync(tab); };
        core.NavigationCompleted += (sender, args) =>
        {
            SaveSessionSoon();
            if (tab.Pending is not null && args.IsSuccess) _ = OfferSaveAsync(tab);
        };

        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            tab.Playing = core.IsDocumentPlayingAudio;
            RefreshHeader(tab);
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement, hideChrome: true);
        core.PermissionRequested += async (_, e) => await OnPermissionRequestedAsync(tab, e);
    }

    private void OnSelectionFeatures(BrowserTab cur)
    {
        if (_prevTab is not null && !ReferenceEquals(_prevTab, cur)) _prevTab.LastActive = DateTime.Now;
        cur.LastActive = null;
        _prevTab = cur;

        if (cur.Suspended)
        {
            try { cur.View?.CoreWebView2?.Resume(); } catch { }
            cur.Suspended = false;
        }
        CloseFind();
        HideSuggestions();
        NewPwPopup.IsOpen = false;
        SaveSessionSoon();
    }

    private void OnWebMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message = null;
        try { message = e.TryGetWebMessageAsString(); } catch { }

        var prefix = "navegador:" + tab.Nonce + ":";
        if (message is null || tab.Nonce.Length == 0 || !message.StartsWith(prefix, StringComparison.Ordinal)) return;
        var action = message[prefix.Length..];
        if (action.StartsWith("cred:", StringComparison.Ordinal))
        {
            OnCredMessage(tab, action[5..], e.Source);
            return;
        }
        if (action.StartsWith("newpw:", StringComparison.Ordinal))
        {
            OnNewPasswordField(tab, action[6..], e.Source);
            return;
        }
        RunShortcut(tab, action);
    }

    private void RunShortcut(BrowserTab tab, string action)
    {
        if (!ReferenceEquals(CurrentTab, tab)) return; // solo la pestaña que se está viendo

        if (!action.StartsWith("zoom", StringComparison.Ordinal))
        {
            if ((DateTime.Now - _lastShortcut).TotalMilliseconds < 120) return;
            _lastShortcut = DateTime.Now;
        }

        switch (action)
        {
            case "newtab": _ = AddTabAsync(false, null); break;
            case "incognito": _ = AddTabAsync(true, null); break;
            case "reopen": ReopenClosedTab(); break;
            case "closetab": CloseTab(tab); break;
            case "address": AddressBox.Focus(FocusState.Programmatic); break;
            case "bookmark": Star_Click(this, new RoutedEventArgs()); break;
            case "history": OpenInternalTab(TabKind.History); break;
            case "downloads": OpenInternalTab(TabKind.Downloads); break;
            case "files": SetDrawer(!_drawerOpen); break;
            case "find": ShowFind(); break;
            case "print": PrintPage(); break;
            case "bookmarksbar": ToggleBookmarksBar(); break;
            case "zoomin": ZoomBy(0.1); break;
            case "zoomout": ZoomBy(-0.1); break;
            case "zoomreset": ZoomReset(); break;
            case "devtools": OpenDevTools(); break;
            case "fullscreen": SetFullscreen(!_fullscreen); break;
            case "nexttab": CycleTab(1); break;
            case "prevtab": CycleTab(-1); break;
            default:
                if (action.StartsWith("tab", StringComparison.Ordinal) && int.TryParse(action[3..], out var n))
                    SelectTabNumber(n);
                break;
        }
    }

    private void CycleTab(int delta)
    {
        var items = Tabs.TabItems.OfType<TabViewItem>().ToList();
        if (items.Count < 2 || Tabs.SelectedItem is not TabViewItem current) return;
        var i = (items.IndexOf(current) + delta + items.Count) % items.Count;
        Tabs.SelectedItem = items[i];
    }

    private void SelectTabNumber(int n)
    {
        var items = Tabs.TabItems.OfType<TabViewItem>().ToList();
        if (items.Count == 0) return;
        Tabs.SelectedItem = n >= 9 ? items[^1] : items[Math.Min(n, items.Count) - 1];
    }

    // ───────────── Título de pestaña (con el ícono de sonido) ─────────────

    private void RefreshHeader(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        var title = tab.Title;
        if (title.Length > 40) title = title[..40] + "…";
        var audio = core is not null && tab.Playing ? (core.IsMuted ? "🔇 " : "🔊 ") : "";
        tab.Item.Header = (tab.Incognito ? "🕶 " : "") + audio + title;
    }

    // ───────────── Menú de cada pestaña ─────────────

    private void AttachTabMenu(BrowserTab tab)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => BuildTabMenu(menu, tab);
        tab.Item.ContextFlyout = menu;
    }

    private void BuildTabMenu(MenuFlyout menu, BrowserTab tab)
    {
        menu.Items.Clear();
        var core = tab.View?.CoreWebView2;

        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (core is not null)
        {
            Add("Recargar", () => core.Reload());
            Add("Duplicar pestaña", () => _ = AddTabAsync(tab.Incognito, HostOf(core.Source) is null ? null : core.Source));
            Add(core.IsMuted ? "Activar el sonido de la pestaña" : "Silenciar pestaña", () =>
            {
                core.IsMuted = !core.IsMuted;
                RefreshHeader(tab);
            });
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        Add("Cerrar pestañas a la derecha", () => CloseTabsAfter(tab));
        Add("Cerrar las demás pestañas", () => CloseOthers(tab));
        Add("Cerrar pestaña", () => CloseTab(tab));
    }

    private void CloseTabsAfter(BrowserTab tab)
    {
        var items = Tabs.TabItems.OfType<TabViewItem>().ToList();
        var index = items.IndexOf(tab.Item);
        foreach (var item in items.Skip(index + 1))
        {
            var t = _tabs.FirstOrDefault(x => ReferenceEquals(x.Item, item));
            if (t is not null) CloseTab(t);
        }
    }

    private void CloseOthers(BrowserTab tab)
    {
        foreach (var t in _tabs.Where(x => !ReferenceEquals(x, tab)).ToList()) CloseTab(t);
    }

    // ───────────── Ahorro de memoria ─────────────

    private void SuspendIdleTabs()
    {
        if (!_settings.MemorySaver) return;
        foreach (var t in _tabs.ToList())
        {
            if (t.Kind != TabKind.Web || t.Suspended || t.Playing || t.LastActive is not { } last) continue;
            if (DateTime.Now - last < TimeSpan.FromMinutes(5)) continue;
            var core = t.View?.CoreWebView2;
            if (core is not null) _ = SuspendAsync(t, core);
        }
    }

    private static async Task SuspendAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            if (await core.TrySuspendAsync()) tab.Suspended = true;
        }
        catch
        {
        }
    }

    // ───────────── Buscar en la página ─────────────

    private void ShowFind()
    {
        if (CurrentTab?.View?.CoreWebView2 is null) return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) _ = RunFindAsync(forward: true, fresh: true);
    }

    private void CloseFind()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = "";
        try { _ = CurrentTab?.View?.CoreWebView2?.ExecuteScriptAsync("getSelection().removeAllRanges()"); } catch { }
    }

    private async Task RunFindAsync(bool forward, bool fresh)
    {
        var core = CurrentTab?.View?.CoreWebView2;
        if (core is null) return;

        var query = FindBox.Text;
        if (query.Length == 0)
        {
            FindCount.Text = "";
            try { await core.ExecuteScriptAsync("getSelection().removeAllRanges()"); } catch { }
            return;
        }

        var literal = JsonSerializer.Serialize(query);
        var js = "(()=>{const q=" + literal +
                 ";const t=(document.body?document.body.innerText:'').toLowerCase();const lq=q.toLowerCase();" +
                 "let n=0,i=0;while((i=t.indexOf(lq,i))>=0){n++;i+=Math.max(1,lq.length);}" +
                 (fresh ? "getSelection().removeAllRanges();" : "") +
                 "window.find(q,false," + (forward ? "false" : "true") + ",true,false,false,false);return n;})()";
        try
        {
            var result = await core.ExecuteScriptAsync(js);
            FindCount.Text = int.TryParse(result, out var n)
                ? (n == 0 ? "Sin resultados" : n == 1 ? "1 coincidencia" : $"{n} coincidencias")
                : "";
        }
        catch
        {
        }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => _ = RunFindAsync(forward: true, fresh: true);

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            _ = RunFindAsync(forward: !shift, fresh: false);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(forward: false, fresh: false);
    private void FindNext_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(forward: true, fresh: false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    // ───────────── Zoom ─────────────

    // WinUI 3 no expone el zoom nativo de WebView2 en el control, así que se aplica con la propiedad CSS «zoom»
    // de la página (se vuelve a aplicar cada vez que carga un documento nuevo).
    private void ZoomBy(double delta)
    {
        var tab = CurrentTab;
        if (tab?.View?.CoreWebView2 is null) return;

        tab.Zoom = Math.Clamp(Math.Round((tab.Zoom + delta) * 100) / 100, 0.25, 5.0);
        SaveSiteZoom(tab.PageHost, tab.Zoom);
        _ = ApplyZoomAsync(tab);
    }

    private void ZoomReset()
    {
        var tab = CurrentTab;
        if (tab?.View?.CoreWebView2 is null) return;

        tab.Zoom = 1.0;
        SaveSiteZoom(tab.PageHost, 1.0);
        _ = ApplyZoomAsync(tab);
    }

    private void SaveSiteZoom(string? host, double zoom)
    {
        if (host is null) return;
        if (Math.Abs(zoom - 1.0) < 0.001) _settings.SiteZoom.Remove(host);
        else _settings.SiteZoom[host] = zoom;
        _settings.Save();
    }

    private void ApplySiteZoom(BrowserTab tab)
    {
        tab.Zoom = tab.PageHost is { } host && _settings.SiteZoom.TryGetValue(host, out var saved) ? saved : 1.0;
        _ = ApplyZoomAsync(tab);
    }

    private static async Task ApplyZoomAsync(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;

        var value = Math.Abs(tab.Zoom - 1.0) < 0.001
            ? "''"
            : "'" + tab.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'";
        try
        {
            await core.ExecuteScriptAsync("if (document.documentElement) document.documentElement.style.zoom = " + value + ";");
        }
        catch
        {
        }
    }

    private void MainMenu_Opening(object? sender, object e)
    {
        var zoom = (int)Math.Round((CurrentTab?.Zoom ?? 1.0) * 100);
        ZoomResetItem.Text = $"Zoom: {zoom}% (restablecer)";
        BookmarksBarToggle.IsChecked = _settings.ShowBookmarksBar;
    }

    // ───────────── Imprimir, PDF, herramientas y pantalla completa ─────────────

    private void PrintPage()
    {
        try { CurrentTab?.View?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

    private async Task SavePdfAsync()
    {
        var core = CurrentTab?.View?.CoreWebView2;
        if (core is null) return;

        var name = string.Concat((core.DocumentTitle ?? "pagina").Split(Path.GetInvalidFileNameChars())).Trim();
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = name.Length == 0 ? "pagina" : name
        };
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await core.PrintToPdfAsync(file.Path, core.Environment.CreatePrintSettings());
        }
        catch
        {
        }
    }

    private void OpenDevTools()
    {
        try { CurrentTab?.View?.CoreWebView2?.OpenDevToolsWindow(); } catch { }
    }

    private void SetFullscreen(bool on, bool hideChrome = false)
    {
        _fullscreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);

        var visibility = on && hideChrome ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = visibility;
        Toolbar.Visibility = visibility;
    }

    // ───────────── Sugerencias de la barra de direcciones ─────────────

    private static bool LooksLikeUrl(string q) =>
        q.Contains("://") || (!q.Contains(' ') && q.Contains('.'));

    private List<Suggestion> BuildSuggestions(string text)
    {
        var q = text.Trim();
        var list = new List<Suggestion>();
        if (q.Length == 0) return list;

        var isUrl = LooksLikeUrl(q);
        list.Add(new Suggestion
        {
            Glyph = isUrl ? "\uE774" : "\uE721",
            Title = isUrl ? "Ir a " + q : $"Buscar «{q}» en {_settings.SearchEngine}",
            Url = ToUrl(q)
        });

        foreach (var b in _bookmarks.All
                     .Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 b.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(3))
            list.Add(new Suggestion { Glyph = "\uE734", Title = b.Title, Subtitle = b.Url, Url = b.Url });

        foreach (var h in _history.Search(q, 12))
            list.Add(new Suggestion { Glyph = "\uE81C", Title = h.Title, Subtitle = h.Url, Url = h.Url });

        return list.GroupBy(s => s.Url).Select(g => g.First()).Take(8).ToList();
    }

    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_addressFocused) return;
        var text = AddressBox.Text;
        if (text == _addressUrl || text.Trim().Length == 0)
        {
            HideSuggestions();
            return;
        }
        ShowSuggestions(text);
    }

    private void ShowSuggestions(string text)
    {
        _suggestions = BuildSuggestions(text);
        SuggestList.ItemsSource = _suggestions;
        if (_suggestions.Count == 0)
        {
            HideSuggestions();
            return;
        }

        SuggestList.SelectedIndex = 0;
        var point = AddressPill.TransformToVisual(SuggestPopup)
            .TransformPoint(new Windows.Foundation.Point(0, AddressPill.ActualHeight + 4));
        SuggestPopup.XamlRoot = Root.XamlRoot;
        SuggestPopup.HorizontalOffset = point.X;
        SuggestPopup.VerticalOffset = point.Y;
        SuggestBox.Width = AddressPill.ActualWidth;
        SuggestPopup.IsOpen = true;
    }

    private void HideSuggestions()
    {
        SuggestPopup.IsOpen = false;
        _overSuggest = false;
    }

    private void MoveSuggestion(int delta)
    {
        var n = _suggestions.Count;
        if (n == 0) return;
        var i = SuggestList.SelectedIndex + delta;
        if (i < 0) i = n - 1;
        if (i >= n) i = 0;
        SuggestList.SelectedIndex = i;
    }

    private void HandleAddressKey(KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                if (SuggestPopup.IsOpen) { MoveSuggestion(1); e.Handled = true; }
                break;
            case VirtualKey.Up:
                if (SuggestPopup.IsOpen) { MoveSuggestion(-1); e.Handled = true; }
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                HideSuggestions();
                AddressBox.Text = _addressUrl;
                CurrentTab?.View?.Focus(FocusState.Programmatic);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                var target = SuggestPopup.IsOpen && SuggestList.SelectedItem is Suggestion s
                    ? s.Url
                    : ToUrl(AddressBox.Text);
                HideSuggestions();
                GoTo(target);
                break;
        }
    }

    private void GoTo(string url)
    {
        var core = CurrentTab?.View?.CoreWebView2;
        if (core is null)
        {
            // Estando en una página interna, la dirección se abre en una pestaña nueva
            _ = AddTabAsync(false, url);
            return;
        }
        core.Navigate(url);
        CurrentTab?.View?.Focus(FocusState.Programmatic);
    }

    private void Suggest_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Suggestion s) return;
        HideSuggestions();
        GoTo(s.Url);
    }

    private void Suggest_PointerEntered(object sender, PointerRoutedEventArgs e) => _overSuggest = true;
    private void Suggest_PointerExited(object sender, PointerRoutedEventArgs e) => _overSuggest = false;

    // ───────────── Barra de marcadores ─────────────

    private void RefreshBookmarksBar()
    {
        BookmarksStrip.Children.Clear();
        var show = _settings.ShowBookmarksBar && _bookmarks.All.Count > 0;
        BookmarksBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var style = (Style)Root.Resources["ToolBtn"];
        foreach (var b in _bookmarks.All.Take(40))
        {
            var url = b.Url;
            var title = string.IsNullOrWhiteSpace(b.Title) ? url : b.Title;
            if (title.Length > 22) title = title[..22] + "…";

            var chip = new Button
            {
                Style = style,
                Padding = new Thickness(8, 3, 8, 3),
                Content = new TextBlock { Text = title, FontSize = 12 }
            };
            ToolTipService.SetToolTip(chip, url);
            chip.Click += (_, _) => OpenFromBar(url, newTab: false);
            chip.RightTapped += (sender, args) =>
            {
                var menu = new MenuFlyout();
                var open = new MenuFlyoutItem { Text = "Abrir en una pestaña nueva" };
                open.Click += (_, _) => OpenFromBar(url, newTab: true);
                var remove = new MenuFlyoutItem { Text = "Quitar de marcadores" };
                remove.Click += (_, _) => _bookmarks.Remove(url);
                menu.Items.Add(open);
                menu.Items.Add(remove);
                menu.ShowAt(chip, new FlyoutShowOptions { Position = args.GetPosition(chip) });
                args.Handled = true;
            };
            BookmarksStrip.Children.Add(chip);
        }
    }

    private void OpenFromBar(string url, bool newTab)
    {
        var core = CurrentTab?.View?.CoreWebView2;
        if (newTab || core is null) _ = AddTabAsync(false, url);
        else core.Navigate(url);
    }

    private void ToggleBookmarksBar()
    {
        _settings.ShowBookmarksBar = !_settings.ShowBookmarksBar;
        _settings.Save();
        RefreshBookmarksBar();
    }

    // ───────────── Permisos de sitios ─────────────

    private async Task OnPermissionRequestedAsync(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var kind = e.PermissionKind.ToString();
            var what = PermissionInfo.AskText(kind);
            if (what is null)
            {
                e.State = CoreWebView2PermissionState.Default;
                return;
            }

            var host = HostOf(e.Uri) ?? tab.PageHost ?? "este sitio";
            var key = host + "|" + kind;
            if (_settings.SitePermissions.TryGetValue(key, out var saved))
            {
                e.State = saved == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                return;
            }

            var (allow, remember) = await AskPermissionAsync(host, what, canRemember: !tab.Incognito);
            e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            if (remember)
            {
                _settings.SitePermissions[key] = allow ? "allow" : "deny";
                _settings.Save();
            }
        }
        catch
        {
            e.State = CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<(bool Allow, bool Remember)> AskPermissionAsync(string host, string what, bool canRemember)
    {
        var check = new CheckBox
        {
            Content = "Recordar mi decisión para este sitio",
            IsChecked = canRemember,
            Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"{host} quiere {what}.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(check);

        var dialog = new ContentDialog
        {
            Title = "Permiso del sitio",
            Content = panel,
            PrimaryButtonText = "Permitir",
            SecondaryButtonText = "Bloquear",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Root.XamlRoot
        };
        var result = await Dialogs.Show(dialog);
        return (result == ContentDialogResult.Primary, check.IsChecked == true && result != ContentDialogResult.None);
    }

    // ───────────── Tirador en forma de gota ─────────────

    private void DropHandle_Tapped(object sender, TappedRoutedEventArgs e) => SetDrawer(!_drawerOpen);
    private void DropHandle_PointerEntered(object sender, PointerRoutedEventArgs e) => DropHandle.Opacity = 1.0;
    private void DropHandle_PointerExited(object sender, PointerRoutedEventArgs e) => DropHandle.Opacity = 0.85;

    // ───────────── Eventos de menú y atajos ─────────────

    private void Reopen_Click(object sender, RoutedEventArgs e) => ReopenClosedTab();
    private void Find_Click(object sender, RoutedEventArgs e) => ShowFind();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(-0.1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomReset();
    private void Print_Click(object sender, RoutedEventArgs e) => PrintPage();
    private void SavePdf_Click(object sender, RoutedEventArgs e) => _ = SavePdfAsync();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void DevTools_Click(object sender, RoutedEventArgs e) => OpenDevTools();
    private void BookmarksBarToggle_Click(object sender, RoutedEventArgs e) => ToggleBookmarksBar();

    private void FindAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; ShowFind(); }
    private void PrintAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; PrintPage(); }
    private void ReopenAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; ReopenClosedTab(); }
    private void BookmarksBarAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; ToggleBookmarksBar(); }
    private void NextTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; CycleTab(1); }
    private void PrevTabAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; CycleTab(-1); }
    private void FullscreenAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; SetFullscreen(!_fullscreen); }
    private void DevToolsAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; OpenDevTools(); }
}
