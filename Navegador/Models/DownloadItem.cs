using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Navegador.Services;

namespace Navegador.Models;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private readonly CoreWebView2DownloadOperation? _op; // null si se restauró desde el disco
    private readonly bool _restoredCompleted;
    private readonly long _restoredSize;

    public string FilePath { get; }
    public DateTime Time { get; }
    public string FileName => Path.GetFileName(FilePath);

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadItem(CoreWebView2DownloadOperation op, string filePath)
    {
        _op = op;
        FilePath = filePath;
        Time = DateTime.Now;
        op.BytesReceivedChanged += (_, _) => Refresh();
        op.StateChanged += (_, _) =>
        {
            Refresh();
            DownloadsStore.Save();
        };
    }

    private DownloadItem(DownloadRecord record)
    {
        FilePath = record.FilePath;
        Time = record.Time;
        _restoredCompleted = record.State == "Completed";
        _restoredSize = record.Size;
    }

    public static DownloadItem FromRecord(DownloadRecord record) => new(record);

    public DownloadRecord ToRecord() => new()
    {
        FilePath = FilePath,
        Time = Time,
        Size = Received,
        State = IsCompleted ? "Completed" : IsInProgress ? "InProgress" : "Interrupted"
    };

    private long Received => _op is null ? _restoredSize : Convert.ToInt64((object)_op.BytesReceived);
    private long Total => _op is null ? _restoredSize : Convert.ToInt64((object?)_op.TotalBytesToReceive ?? 0L);
    private bool IsCompleted => _op is null ? _restoredCompleted : _op.State == CoreWebView2DownloadState.Completed;
    private bool IsInProgress => _op is not null && _op.State == CoreWebView2DownloadState.InProgress;

    public double Progress
    {
        get
        {
            if (IsCompleted) return 100;
            return Total > 0 ? Math.Min(100.0, Received * 100.0 / Total) : 0;
        }
    }

    public string Status
    {
        get
        {
            if (IsCompleted)
                return File.Exists(FilePath)
                    ? $"Completada · {FormatSize(Received)} · {Time:g}"
                    : "El archivo se movió o se borró";

            if (IsInProgress)
                return Total > 0
                    ? $"Descargando… {FormatSize(Received)} de {FormatSize(Total)}"
                    : $"Descargando… {FormatSize(Received)}";

            return "Cancelada o interrumpida";
        }
    }

    private void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    public void Cancel()
    {
        if (_op is not null && IsInProgress) _op.Cancel();
    }

    public void OpenFile()
    {
        if (!IsCompleted || !File.Exists(FilePath)) return;
        Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
    }

    public void ShowInFolder()
    {
        if (File.Exists(FilePath))
            Process.Start("explorer.exe", $"/select,\"{FilePath}\"");
        else if (Directory.Exists(Path.GetDirectoryName(FilePath)))
            Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(FilePath)}\"");
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
