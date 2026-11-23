namespace Navegador.Models;

/// <summary>Lo que se guarda en disco de cada descarga.</summary>
public sealed class DownloadRecord
{
    public string FilePath { get; set; } = "";
    public string State { get; set; } = "Completed"; // Completed | Interrupted
    public long Size { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}
