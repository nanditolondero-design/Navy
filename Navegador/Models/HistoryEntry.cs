using System.Text.Json.Serialization;

namespace Navegador.Models;

public sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string TimeText => Time.ToString("g");
}
