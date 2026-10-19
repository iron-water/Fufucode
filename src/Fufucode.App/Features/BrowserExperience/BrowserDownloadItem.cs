using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.App.Views;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>下载列表中的一行；进度来自 WebView2 的真实字节数，不估算。</summary>
public sealed partial class BrowserDownloadItem : ObservableObject
{
    public BrowserDownloadItem(string id, string fileName) => (Id, FileName) = (id, fileName);

    public string Id { get; }
    [ObservableProperty] private string _fileName;
    [ObservableProperty] private string _resultFilePath = string.Empty;
    [ObservableProperty] private long _bytesReceived;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private BrowserDownloadState _state = BrowserDownloadState.InProgress;
    [ObservableProperty] private string? _interruptReason;

    public bool IsCompleted => State == BrowserDownloadState.Completed;
    public bool IsInterrupted => State == BrowserDownloadState.Interrupted;
    public bool IsActive => State == BrowserDownloadState.InProgress;
    public bool CanCancel => IsActive;

    public double ProgressPercent =>
        TotalBytes > 0 ? Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100) : 0;

    public string ProgressText => State switch
    {
        BrowserDownloadState.Completed => "已完成",
        BrowserDownloadState.Interrupted => "已中断" + (string.IsNullOrWhiteSpace(InterruptReason) ? "" : "：" + InterruptReason),
        _ => TotalBytes > 0
            ? $"{BytesReceived / 1024.0 / 1024.0:0.0} / {TotalBytes / 1024.0 / 1024.0:0.0} MB"
            : $"{BytesReceived / 1024.0 / 1024.0:0.0} MB",
    };

    public string Summary => FileName + " · " + ProgressText;

    public void Apply(BrowserDownloadProgress progress)
    {
        ResultFilePath = progress.ResultFilePath ?? string.Empty;
        BytesReceived = progress.BytesReceived;
        TotalBytes = progress.TotalBytes;
        State = progress.State;
        InterruptReason = progress.InterruptReason;
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Summary));
    }
}
