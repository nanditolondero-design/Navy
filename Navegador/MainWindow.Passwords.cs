using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Navegador.Services;
using Navegador.Ui;

namespace Navegador;

/// <summary>Gestor de contraseñas: autocompletar con la llave de la barra y ofrecer guardar al iniciar sesión.</summary>
public sealed partial class MainWindow
{
    private readonly VaultService _vault = new();
    private readonly HelloService _hello = new();
    private string _suggested = "";
    private BrowserTab? _newPwTab;

    private void InitPasswords()
    {
        // Se bloquea solo a los 15 minutos sin usarlo y al cerrar la ventana
        _suspendTimer!.Tick += (_, _) => _vault.CheckIdle(TimeSpan.FromMinutes(15));
        AppWindow.Closing += (_, _) => _vault.Lock();
    }

    // Detecta los formularios de inicio de sesión de la página y avisa al navegador cuando se envían.
    // Nunca se pide ni se llena nada sin que la persona lo elija desde la llave de la barra.
    private const string CredBridgeTemplate = """
(() => {
  const N = '__NONCE__';
  const post = (window.chrome && window.chrome.webview) ? window.chrome.webview.postMessage.bind(window.chrome.webview) : null;
  if (!post) return;
  let last = 0;
  const visible = el => !!(el && el.offsetParent !== null);
  const pick = () => {
    const pws = [...document.querySelectorAll('input[type=password]')].filter(e => visible(e) && e.value);
    if (!pws.length) return null;
    const pw = pws[0];
    const scope = pw.form || document;
    const cands = [...scope.querySelectorAll('input')].filter(e =>
      e !== pw && visible(e) && ['text', 'email', 'tel', ''].includes((e.type || '').toLowerCase()) && e.value);
    const before = cands.filter(e => e.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING);
    const user = before.length ? before[before.length - 1] : (cands[0] || null);
    return { u: user ? user.value : '', p: pw.value };
  };
  const report = () => {
    const now = Date.now();
    if (now - last < 3000) return;
    const c = pick();
    if (!c || !c.p) return;
    last = now;
    try { post('navegador:' + N + ':cred:' + JSON.stringify(c)); } catch (e) {}
  };
  const looksLikeSubmit = el => {
    if (!el) return false;
    if ((el.type || '').toLowerCase() === 'submit') return true;
    const t = ((el.innerText || el.value || '') + '').toLowerCase();
    return /ingres|entrar|iniciar|acceder|log ?in|sign ?in|continu|siguiente|enviar|submit|registr|crear/.test(t);
  };
  addEventListener('submit', report, true);
  addEventListener('click', e => {
    const t = e.target && e.target.closest ? e.target.closest('button,input[type=submit],[role=button]') : null;
    if (looksLikeSubmit(t)) report();
  }, true);
  addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.target && (e.target.type || '').toLowerCase() === 'password') report();
  }, true);

  // Formularios de registro: avisa cuando se entra a un campo de contraseña nueva para sugerir una segura
  const isNewPw = el => {
    if (!el || (el.type || '').toLowerCase() !== 'password') return false;
    const ac = (el.getAttribute('autocomplete') || '').toLowerCase();
    if (ac.includes('new-password')) return true;
    if (ac.includes('current-password')) return false;
    const form = el.form || document;
    const pws = [...form.querySelectorAll('input[type=password]')].filter(visible);
    if (pws.length >= 3 && el === pws[0]) return false;
    if (pws.length >= 2) return true;
    const hint = (((form.getAttribute && form.getAttribute('action')) || '') + ' ' + location.pathname + ' ' +
      (el.name || '') + ' ' + (el.id || '') + ' ' + (el.placeholder || '')).toLowerCase();
    return /sign.?up|regist|create.?account|crear.?cuenta|new.?password|nueva.?contrase/.test(hint);
  };
  addEventListener('focusin', e => {
    const el = e.target;
    if (!isNewPw(el) || el.value) return;
    const r = el.getBoundingClientRect();
    try { post('navegador:' + N + ':newpw:' + JSON.stringify({ x: r.left, y: r.bottom, w: r.width })); } catch (x) {}
  }, true);
})();
""";

    // Llena el formulario de la página con un usuario y una contraseña guardados
    private const string FillScriptTemplate = """
(() => {
  const u = __USER__;
  const p = __PASS__;
  const visible = el => !!(el && el.offsetParent !== null);
  const setVal = (el, v) => {
    const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    d.set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const textTypes = ['text', 'email', 'tel', ''];
  const pw = [...document.querySelectorAll('input[type=password]')].find(visible);
  if (!pw) {
    // inicio de sesión en dos pasos: primero solo se pide el usuario
    const only = [...document.querySelectorAll('input')].find(e => visible(e) && textTypes.includes((e.type || '').toLowerCase()));
    if (only && u) { setVal(only, u); return 'user'; }
    return 'nofield';
  }
  const scope = pw.form || document;
  const cands = [...scope.querySelectorAll('input')].filter(e => e !== pw && visible(e) && textTypes.includes((e.type || '').toLowerCase()));
  const before = cands.filter(e => e.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING);
  const user = before.length ? before[before.length - 1] : (cands[0] || null);
  if (user && u) setVal(user, u);
  setVal(pw, p);
  return 'ok';
})()
""";

    private void RegisterCredBridge(BrowserTab tab, CoreWebView2 core)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(CredBridgeTemplate.Replace("__NONCE__", tab.Nonce));
    }

    // ───────────── Ofrecer guardar ─────────────

    private void OnCredMessage(BrowserTab tab, string json, string? source)
    {
        if (tab.Incognito || tab.Kind != TabKind.Web || !_vault.Exists) return;

        // Solo vale lo que viene de la página que se está viendo (no de un iframe de otro sitio)
        var host = HostOf(source);
        if (host is null || !string.Equals(host, tab.PageHost, StringComparison.OrdinalIgnoreCase)) return;
        if (_settings.NeverSaveHosts.Contains(host)) return;

        string user, pass;
        try
        {
            using var doc = JsonDocument.Parse(json);
            user = doc.RootElement.GetProperty("u").GetString() ?? "";
            pass = doc.RootElement.GetProperty("p").GetString() ?? "";
        }
        catch
        {
            return;
        }
        if (pass.Length == 0 || pass.Length > 256 || user.Length > 256) return;

        tab.Pending = new PendingCred(host, user, pass, DateTime.Now);

        // Si la página no navega (inicio de sesión sin recarga), se revisa a los 4 segundos
        _ = Task.Delay(4000).ContinueWith(t => DispatcherQueue.TryEnqueue(() => { _ = OfferSaveAsync(tab); }));
    }

    private async Task OfferSaveAsync(BrowserTab tab)
    {
        var pending = tab.Pending;
        if (pending is null) return;
        tab.Pending = null;
        if ((DateTime.Now - pending.At).TotalSeconds > 30) return;

        var core = tab.View?.CoreWebView2;
        if (core is null) return;

        // Si la página sigue mostrando un campo de contraseña, el inicio de sesión probablemente falló
        try
        {
            var stillThere = await core.ExecuteScriptAsync("!!document.querySelector('input[type=password]')");
            if (stillThere == "true") return;
        }
        catch
        {
            return;
        }

        await SaveCredentialAsync(pending);
    }

    private async Task<bool> EnsureUnlockedAsync(string title, string text)
    {
        if (_vault.IsUnlocked) return true;

        if (_hello.IsEnabled)
        {
            var key = await _hello.GetKeyAsync();
            if (key is not null)
            {
                var opened = await _vault.UnlockWithKeyAsync(key);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                if (opened) return true;
            }
        }

        Dialogs.Root = Root.XamlRoot;
        Dialogs.Theme = Root.ActualTheme;

        var master = await Dialogs.Password(title, text, "Desbloquear");
        if (master is null) return false;
        if (await _vault.UnlockAsync(master)) return true;

        await Dialogs.Message("Contraseña incorrecta", "No se pudo desbloquear el gestor de contraseñas.");
        return false;
    }

    private async Task SaveCredentialAsync(PendingCred p)
    {
        Dialogs.Root = Root.XamlRoot;
        Dialogs.Theme = Root.ActualTheme;

        if (!await EnsureUnlockedAsync($"Guardar la contraseña de {p.Host}", "Escribí tu contraseña maestra para guardar esta contraseña.")) return;

        var existing = _vault.MatchHost(p.Host)
            .FirstOrDefault(e => string.Equals(e.Username, p.User, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (existing.Password == p.Pass) return;
            var update = await Dialogs.Confirm("Actualizar contraseña",
                $"¿Querés actualizar la contraseña de {(p.User.Length > 0 ? p.User : "esta cuenta")} en {p.Host}?", "Actualizar");
            if (!update) return;
            existing.Password = p.Pass;
            existing.Modified = DateTime.Now;
            _vault.Update(existing);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "¿Guardar la contraseña?",
            Content = new TextBlock
            {
                Text = p.User.Length > 0
                    ? $"Se guardaría la contraseña de {p.User} para {p.Host}, cifrada en esta PC."
                    : $"Se guardaría esta contraseña para {p.Host}, cifrada en esta PC.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Guardar",
            SecondaryButtonText = "Nunca en este sitio",
            CloseButtonText = "Ahora no",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme
        };
        var result = await Dialogs.Show(dialog);

        if (result == ContentDialogResult.Primary)
        {
            _vault.Add(new VaultEntry
            {
                Name = p.Host.StartsWith("www.") ? p.Host[4..] : p.Host,
                Url = "https://" + p.Host,
                Username = p.User,
                Password = p.Pass
            });
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _settings.NeverSaveHosts.Add(p.Host);
            _settings.Save();
        }
    }

    // ───────────── Llave de la barra: autocompletar ─────────────

    private async void KeyBtn_Click(object sender, RoutedEventArgs e) => await ShowCredentialMenuAsync();

    private async Task ShowCredentialMenuAsync()
    {
        var tab = CurrentTab;
        if (tab?.View?.CoreWebView2 is null) return;

        if (!_vault.Exists)
        {
            OpenInternalTab(TabKind.Passwords);
            return;
        }
        if (!await EnsureUnlockedAsync("Desbloquear las contraseñas", "Escribí tu contraseña maestra.")) return;

        var host = tab.PageHost;
        var matches = host is null ? new List<VaultEntry>() : _vault.MatchHost(host).ToList();

        var menu = new MenuFlyout();
        if (matches.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No hay contraseñas guardadas para este sitio", IsEnabled = false });
        }
        foreach (var match in matches)
        {
            var entry = match;
            var item = new MenuFlyoutItem
            {
                Text = string.IsNullOrEmpty(entry.Username) ? entry.Name : $"{entry.Username} — {entry.Name}"
            };
            item.Click += async (_, _) => await FillAsync(tab, entry);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var manager = new MenuFlyoutItem { Text = "Abrir el gestor de contraseñas" };
        manager.Click += (_, _) => OpenInternalTab(TabKind.Passwords);
        menu.Items.Add(manager);

        menu.ShowAt(KeyBtn, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private async Task FillAsync(BrowserTab tab, VaultEntry entry)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;

        // Por seguridad no se llenan formularios de páginas sin HTTPS (salvo las del propio equipo)
        var secure = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
        if (!secure)
        {
            Dialogs.Root = Root.XamlRoot;
            await Dialogs.Message("Sitio sin HTTPS", "Por seguridad no se completan contraseñas en páginas que no usan HTTPS.");
            return;
        }

        var js = FillScriptTemplate
            .Replace("__USER__", JsonSerializer.Serialize(entry.Username))
            .Replace("__PASS__", JsonSerializer.Serialize(entry.Password));
        try
        {
            var result = await core.ExecuteScriptAsync(js);
            if (result.Contains("nofield"))
            {
                Dialogs.Root = Root.XamlRoot;
                await Dialogs.Message("No encontré el formulario", "En esta página no hay un campo de usuario o contraseña visible.");
            }
        }
        catch
        {
        }
    }

    // ───────────── Sugerir una contraseña en los formularios de registro ─────────────

    private const string FillNewPasswordTemplate = """
(() => {
  const p = __PASS__;
  const visible = el => !!(el && el.offsetParent !== null);
  const setVal = (el, v) => {
    const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    d.set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const a = document.activeElement;
  const form = (a && a.form) || document;
  let pws = [...form.querySelectorAll('input[type=password]')].filter(visible);
  if (!pws.length) pws = [...document.querySelectorAll('input[type=password]')].filter(visible);
  if (pws.length >= 3) pws = pws.slice(1); // la primera es la contraseña actual
  pws.forEach(e => setVal(e, p));
  return pws.length;
})()
""";

    private void OnNewPasswordField(BrowserTab tab, string json, string? source)
    {
        if (tab.Incognito || tab.Kind != TabKind.Web || !_vault.Exists || !ReferenceEquals(CurrentTab, tab)) return;

        var host = HostOf(source);
        if (host is null || !string.Equals(host, tab.PageHost, StringComparison.OrdinalIgnoreCase)) return;
        if (_settings.NeverSaveHosts.Contains(host)) return;

        double x, y;
        try
        {
            using var doc = JsonDocument.Parse(json);
            x = doc.RootElement.GetProperty("x").GetDouble();
            y = doc.RootElement.GetProperty("y").GetDouble();
        }
        catch
        {
            return;
        }

        _newPwTab = tab;
        _suggested = VaultService.GeneratePassword(20);
        NewPwText.Text = _suggested;

        // La ventana va justo debajo del campo (las coordenadas de la página coinciden con las de la ventana)
        var origin = tab.View!.TransformToVisual(NewPwPopup).TransformPoint(new Windows.Foundation.Point(0, 0));
        NewPwPopup.XamlRoot = Root.XamlRoot;
        NewPwPopup.HorizontalOffset = Math.Clamp(origin.X + x, 8, Math.Max(8, Root.ActualWidth - 316));
        NewPwPopup.VerticalOffset = origin.Y + y + 6;
        NewPwPopup.IsOpen = true;
    }

    private async void NewPwUse_Click(object sender, RoutedEventArgs e)
    {
        NewPwPopup.IsOpen = false;
        var tab = _newPwTab;
        var core = tab?.View?.CoreWebView2;
        if (core is null) return;

        var secure = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
        if (!secure)
        {
            Dialogs.Root = Root.XamlRoot;
            await Dialogs.Message("Sitio sin HTTPS", "Por seguridad no se completan contraseñas en páginas que no usan HTTPS.");
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(FillNewPasswordTemplate.Replace("__PASS__", JsonSerializer.Serialize(_suggested)));
        }
        catch
        {
        }
    }

    private void NewPwAnother_Click(object sender, RoutedEventArgs e)
    {
        _suggested = VaultService.GeneratePassword(20);
        NewPwText.Text = _suggested;
    }

    private void NewPwNo_Click(object sender, RoutedEventArgs e) => NewPwPopup.IsOpen = false;

    private void Passwords_Click(object sender, RoutedEventArgs e) => OpenInternalTab(TabKind.Passwords);
}
