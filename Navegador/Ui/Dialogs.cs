using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Navegador.Ui;

/// <summary>Diálogos del explorador (se arman en código). Todos devuelven null/false si se cancelan.</summary>
internal static class Dialogs
{
    public static XamlRoot? Root { get; set; }
    public static ElementTheme Theme { get; set; } = ElementTheme.Default;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>WinUI deja abrir un solo ContentDialog a la vez: si ya hay uno, este espera su turno.</summary>
    public static async Task<ContentDialogResult> Show(ContentDialog d)
    {
        await OneAtATime.WaitAsync();
        try
        {
            d.XamlRoot ??= Root;
            return await d.ShowAsync();
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static ContentDialog Make(string title, string primary) => new()
    {
        Title = title,
        XamlRoot = Root,
        RequestedTheme = Theme,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancelar",
        DefaultButton = ContentDialogButton.Primary
    };

    public static async Task Message(string title, string text)
    {
        var d = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Aceptar", XamlRoot = Root, RequestedTheme = Theme
        };
        await Show(d);
    }

    public static async Task<bool> Confirm(string title, string text, string okText)
    {
        var d = Make(title, okText);
        d.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        return await Show(d) == ContentDialogResult.Primary;
    }

    /// <summary>Un texto de una línea. Devuelve null si se cancela.</summary>
    public static async Task<string?> Text(string title, string initial, string okText)
    {
        var box = new TextBox { Text = initial, TextWrapping = TextWrapping.NoWrap };
        var d = Make(title, okText);
        d.Content = box;
        d.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            var dot = initial.LastIndexOf('.');
            if (dot > 0) box.Select(0, dot); else box.SelectAll();
        };
        return await Show(d) == ContentDialogResult.Primary ? box.Text : null;
    }
}
