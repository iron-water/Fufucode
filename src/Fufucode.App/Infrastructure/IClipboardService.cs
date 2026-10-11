using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace Fufucode.App.Infrastructure;

/// <summary>剪贴板抽象，便于在无头自测中替换实现。</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>基于 Avalonia 主窗口剪贴板的实现（Avalonia 12 的剪贴板在 TopLevel 上）。</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return Task.CompletedTask;
        }

        var clipboard = desktop.MainWindow?.Clipboard;
        return clipboard is null ? Task.CompletedTask : clipboard.SetTextAsync(text);
    }
}

/// <summary>不访问真实剪贴板的实现（自测用）：记录最后一次复制的文本。</summary>
public sealed class RecordingClipboardService : IClipboardService
{
    public string? LastCopied { get; private set; }

    public Task SetTextAsync(string text)
    {
        LastCopied = text;
        return Task.CompletedTask;
    }
}
