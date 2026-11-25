using Microsoft.Win32;

namespace Navegador.Services;

/// <summary>
/// Anota la app en el registro del usuario (sin permisos de administrador) para que Windows la muestre en la lista de
/// navegadores. Windows no deja fijar el predeterminado por programa: lo elige la persona en Configuración.
/// </summary>
public static class DefaultBrowser
{
    public static bool Register()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return false;

            using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\NavegadorURL"))
            {
                cls.SetValue("", "Navegador URL");
                cls.SetValue("URL Protocol", "");
                using var cmd = cls.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }

            const string root = @"Software\Clients\StartMenuInternet\Navegador";
            using (var app = Registry.CurrentUser.CreateSubKey(root))
            {
                app.SetValue("", "Navegador");
                using var cmd = app.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\"");
            }

            using (var cap = Registry.CurrentUser.CreateSubKey(root + @"\Capabilities"))
            {
                cap.SetValue("ApplicationName", "Navegador");
                cap.SetValue("ApplicationDescription", "Navegador web liviano para Windows");
                using var urls = cap.CreateSubKey("URLAssociations");
                urls.SetValue("http", "NavegadorURL");
                urls.SetValue("https", "NavegadorURL");
                using var files = cap.CreateSubKey("FileAssociations");
                files.SetValue(".htm", "NavegadorURL");
                files.SetValue(".html", "NavegadorURL");
            }

            using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            registered.SetValue("Navegador", root + @"\Capabilities");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
