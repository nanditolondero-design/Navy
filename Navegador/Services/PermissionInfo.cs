namespace Navegador.Services;

/// <summary>Textos de los permisos de sitios (cámara, micrófono, ubicación...).</summary>
public static class PermissionInfo
{
    /// <summary>Lo que se le pregunta al usuario («quiere usar la cámara»), o null si ese permiso no se pregunta.</summary>
    public static string? AskText(string kind) => kind switch
    {
        "Microphone" => "usar el micrófono",
        "Camera" => "usar la cámara",
        "Geolocation" => "conocer tu ubicación",
        "Notifications" => "mostrar notificaciones",
        "ClipboardRead" => "ver lo que copiaste",
        _ => null
    };

    public static string Label(string kind) => kind switch
    {
        "Microphone" => "Micrófono",
        "Camera" => "Cámara",
        "Geolocation" => "Ubicación",
        "Notifications" => "Notificaciones",
        "ClipboardRead" => "Portapapeles",
        _ => kind
    };
}
