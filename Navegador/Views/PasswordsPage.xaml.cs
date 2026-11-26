using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Navegador.Services;
using Navegador.Ui;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Navegador.Views;

public sealed class VaultRow
{
    public required VaultEntry Entry { get; init; }
    public string Title => Entry.Name;
    public string Subtitle => string.IsNullOrEmpty(Entry.Username) ? Entry.Url : $"{Entry.Username} · {Entry.Url}";
}

/// <summary>Pantalla del gestor de contraseñas: crear o desbloquear la bóveda y administrar lo guardado.</summary>
public sealed partial class PasswordsPage : UserControl, IRefreshable
{
    private readonly BrowserServices _s;
    private VaultService Vault => _s.Vault;
    private bool _helloSupported;

    public PasswordsPage(BrowserServices services)
    {
        _s = services;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            Dialogs.Root = XamlRoot;
            Dialogs.Theme = ActualTheme;
        };
        Vault.Changed += OnVaultChanged;
        Unloaded += (_, _) => Vault.Changed -= OnVaultChanged;
        Refresh();
        _ = LoadHelloSupportAsync();
    }

    private async Task LoadHelloSupportAsync()
    {
        _helloSupported = await HelloService.IsSupportedAsync();
        Refresh();
    }

    private void OnVaultChanged() => DispatcherQueue.TryEnqueue(Refresh);

    public void Refresh()
    {
        var unlocked = Vault.IsUnlocked;
        LockedPanel.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
        OpenPanel.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;

        HelloBtn.Visibility = !unlocked && _helloSupported && _s.Hello.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        HelloToggleBtn.Visibility = unlocked && _helloSupported ? Visibility.Visible : Visibility.Collapsed;
        HelloToggleBtn.Content = _s.Hello.IsEnabled ? "Desactivar Windows Hello" : "Activar Windows Hello";

        if (!unlocked)
        {
            var exists = Vault.Exists;
            MasterBox2.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
            UnlockBtn.Content = exists ? "Desbloquear" : "Crear el gestor de contraseñas";
            ForgotBtn.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
            LockedHint.Text = exists
                ? "Escribí tu contraseña maestra para ver y usar tus contraseñas."
                : "Creá una contraseña maestra. Tus contraseñas se guardan cifradas (AES-256) solo en esta PC. " +
                  "Si la olvidás, no hay forma de recuperarlas.";
            return;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var all = Vault.Entries;
        var q = SearchBox.Text.Trim();

        var rows = all
            .Where(e => q.Length == 0 ||
                        e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        e.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(1000)
            .Select(e => new VaultRow { Entry = e })
            .ToList();

        ItemsList.ItemsSource = rows;
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var reused = all.GroupBy(e => e.Password).Where(g => g.Count() > 1).Sum(g => g.Count());
        var weak = all.Count(e => e.Password.Length < 10);
        SummaryText.Text = all.Count == 0
            ? ""
            : $"{all.Count} guardadas · {reused} con contraseña repetida · {weak} débiles (menos de 10 caracteres)";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vault.IsUnlocked) ApplyFilter();
    }

    // ───────── Crear / desbloquear ─────────

    private void Master_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Unlock_Click(sender, e);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        LockedError.Text = "";
        var master = MasterBox.Password;
        if (master.Length == 0) return;

        UnlockBtn.IsEnabled = false;
        var previous = UnlockBtn.Content;
        UnlockBtn.Content = "Procesando…";
        try
        {
            if (!Vault.Exists)
            {
                if (master.Length < 8) { LockedError.Text = "Usá al menos 8 caracteres."; return; }
                if (master != MasterBox2.Password) { LockedError.Text = "Las dos contraseñas no coinciden."; return; }
                await Vault.CreateAsync(master);
            }
            else if (!await Vault.UnlockAsync(master))
            {
                LockedError.Text = "Contraseña incorrecta.";
                return;
            }
            MasterBox.Password = "";
            MasterBox2.Password = "";
        }
        catch (Exception ex)
        {
            LockedError.Text = "No se pudo abrir el gestor: " + ex.Message;
        }
        finally
        {
            UnlockBtn.IsEnabled = true;
            UnlockBtn.Content = previous;
        }
    }

    private async void Forgot_Click(object sender, RoutedEventArgs e)
    {
        var ok = await Dialogs.Confirm(
            "Olvidé mi contraseña maestra",
            "No se puede recuperar. Si seguís, se borran TODAS las contraseñas guardadas y podés empezar de cero. ¿Querés borrarlas?",
            "Borrar todo");
        if (!ok) return;
        _s.Hello.Disable();
        Vault.Reset();
    }

    private async void Hello_Click(object sender, RoutedEventArgs e)
    {
        LockedError.Text = "";
        var key = await _s.Hello.GetKeyAsync();
        var ok = key is not null && await Vault.UnlockWithKeyAsync(key);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        if (!ok) LockedError.Text = "No se pudo desbloquear con Windows Hello. Usá la contraseña maestra.";
    }

    private async void HelloToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Hello.IsEnabled)
        {
            _s.Hello.Disable();
            Refresh();
            return;
        }

        var key = Vault.ExportKey();
        if (key is null) return;
        try
        {
            if (!await _s.Hello.EnableAsync(key))
                await Dialogs.Message("No se activó", "Windows Hello no se pudo activar. Probá de nuevo o seguí usando la contraseña maestra.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        Refresh();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => Vault.Lock();

    // ───────── Agregar, editar, quitar ─────────

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var entry = await EditAsync(null);
        if (entry is not null) Vault.Add(entry);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRow row) return;
        var entry = await EditAsync(row.Entry);
        if (entry is not null) Vault.Update(entry);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRow row) return;
        var ok = await Dialogs.Confirm("Quitar contraseña", $"¿Quitar «{row.Entry.Name}» del gestor?", "Quitar");
        if (ok) Vault.Remove(row.Entry.Id);
    }

    private async Task<VaultEntry?> EditAsync(VaultEntry? existing)
    {
        var name = new TextBox { Header = "Nombre", Text = existing?.Name ?? "" };
        var url = new TextBox { Header = "Sitio web", PlaceholderText = "https://ejemplo.com", Text = existing?.Url ?? "" };
        var user = new TextBox { Header = "Usuario o correo", Text = existing?.Username ?? "" };
        var pass = new PasswordBox { Header = "Contraseña", Password = existing?.Password ?? "" };
        var generate = new Button { Content = "Generar una contraseña segura" };
        generate.Click += (_, _) => pass.Password = VaultService.GeneratePassword(20);
        var note = new TextBox
        {
            Header = "Nota", Text = existing?.Note ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 60
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(name);
        panel.Children.Add(url);
        panel.Children.Add(user);
        panel.Children.Add(pass);
        panel.Children.Add(generate);
        panel.Children.Add(note);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Agregar contraseña" : "Editar contraseña",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        if (await Dialogs.Show(dialog) != ContentDialogResult.Primary) return null;

        if (pass.Password.Length == 0)
        {
            await Dialogs.Message("Falta la contraseña", "Escribí una contraseña o generá una.");
            return null;
        }

        var entry = existing ?? new VaultEntry();
        entry.Url = url.Text.Trim();
        entry.Username = user.Text.Trim();
        entry.Password = pass.Password;
        entry.Note = note.Text;
        entry.Name = name.Text.Trim();
        if (entry.Name.Length == 0) entry.Name = VaultService.HostFromUrl(entry.Url) ?? "Sin nombre";
        entry.Modified = DateTime.Now;
        return entry;
    }

    // ───────── Copiar y ver ─────────

    private void CopyUser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VaultRow row) CopySecret(row.Entry.Username, clearLater: false);
    }

    private void CopyPass_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VaultRow row) CopySecret(row.Entry.Password, clearLater: true);
    }

    private async void Show_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRow row) return;
        var box = new TextBox { Text = row.Entry.Password, IsReadOnly = true };
        var dialog = new ContentDialog
        {
            Title = row.Entry.Name,
            Content = box,
            CloseButtonText = "Cerrar",
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        await Dialogs.Show(dialog);
    }

    /// <summary>Copia sin dejarlo en el historial del portapapeles de Windows; la contraseña se borra a los 30 segundos.</summary>
    private async void CopySecret(string text, bool clearLater)
    {
        if (text.Length == 0) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        }
        catch
        {
            return;
        }
        if (!clearLater) return;

        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text) && await view.GetTextAsync() == text) Clipboard.Clear();
        }
        catch
        {
        }
    }

    // ───────── Importar y exportar ─────────

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_s.Owner));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var text = await File.ReadAllTextAsync(file.Path);
            var (entries, skipped) = CsvPasswords.Parse(text);
            var added = Vault.Import(entries);
            var repeated = entries.Count - added;

            await Dialogs.Message("Importación lista",
                $"Se importaron {added} contraseñas." +
                (repeated > 0 ? $"\n{repeated} ya estaban guardadas y se omitieron." : "") +
                (skipped > 0 ? $"\n{skipped} filas no tenían datos suficientes." : ""));

            var delete = await Dialogs.Confirm(
                "Borrar el archivo CSV",
                "Ese archivo tiene tus contraseñas escritas sin cifrar. ¿Querés borrarlo ahora? (también conviene vaciar la Papelera)",
                "Borrar archivo");
            if (delete) CsvPasswords.SecureDelete(file.Path);
        }
        catch (Exception ex)
        {
            await Dialogs.Message("No se pudo importar", ex.Message);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var warn = await Dialogs.Confirm(
            "Exportar contraseñas",
            "El archivo CSV va a tener todas tus contraseñas sin cifrar: cualquiera que lo tenga puede verlas. " +
            "Guardalo en un lugar seguro y borralo apenas lo uses. ¿Seguir?",
            "Seguir");
        if (!warn) return;

        var master = await Dialogs.Password("Confirmá que sos vos", "Escribí tu contraseña maestra para exportar.", "Exportar");
        if (master is null) return;
        if (!await Vault.VerifyMasterAsync(master))
        {
            await Dialogs.Message("Contraseña incorrecta", "No se exportó nada.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "contraseñas-navegador"
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_s.Owner));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await File.WriteAllTextAsync(file.Path, CsvPasswords.Write(Vault.Entries));
            await Dialogs.Message("Exportación lista", "Recordá borrar el archivo cuando termines de usarlo.");
        }
        catch (Exception ex)
        {
            await Dialogs.Message("No se pudo exportar", ex.Message);
        }
    }

    private async void ChangeMaster_Click(object sender, RoutedEventArgs e)
    {
        var oldBox = new PasswordBox { Header = "Contraseña maestra actual" };
        var newBox = new PasswordBox { Header = "Contraseña maestra nueva" };
        var newBox2 = new PasswordBox { Header = "Repetir la nueva" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(oldBox);
        panel.Children.Add(newBox);
        panel.Children.Add(newBox2);

        var dialog = new ContentDialog
        {
            Title = "Cambiar la contraseña maestra",
            Content = panel,
            PrimaryButtonText = "Cambiar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        if (await Dialogs.Show(dialog) != ContentDialogResult.Primary) return;

        if (newBox.Password.Length < 8) { await Dialogs.Message("Muy corta", "Usá al menos 8 caracteres."); return; }
        if (newBox.Password != newBox2.Password) { await Dialogs.Message("No coinciden", "Las dos contraseñas nuevas son distintas."); return; }

        var ok = await Vault.ChangeMasterAsync(oldBox.Password, newBox.Password);
        var hello = ok && _s.Hello.IsEnabled;
        if (hello) _s.Hello.Disable(); // la clave cambió: hay que volver a activarlo
        await Dialogs.Message(ok ? "Listo" : "No se cambió",
            ok ? "La contraseña maestra se cambió." + (hello ? " Windows Hello se desactivó: volvé a activarlo cuando quieras." : "")
               : "La contraseña actual no es correcta.");
        Refresh();
    }
}
