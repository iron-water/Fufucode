using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.App.Features.FilePreview;

/// <summary>统一预览宿主：管理 provider 生命周期、取消、错误页和内部前进/后退历史。</summary>
public sealed class FilePreviewHost : ContentControl, IDisposable
{
    private readonly FilePreviewRegistry _registry;
    private readonly List<FilePreviewRequest> _history = [];
    private CancellationTokenSource? _loadCancellation;
    private int _historyIndex = -1;
    private IFilePreviewProvider? _activeProvider;
    private bool _disposed;

    public FilePreviewHost() : this(null) { }

    public FilePreviewHost(FilePreviewRegistry? registry) => _registry = registry ?? FilePreviewRegistry.CreateDefault();
    public FilePreviewResult? Result { get; private set; }
    public FilePreviewRegistry Registry => _registry;
    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    public event EventHandler? PreviewChanged;

    public async Task OpenAsync(string projectRoot, string path, bool addHistory = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new FilePreviewRequest(projectRoot, path);
        if (addHistory)
        {
            if (_historyIndex >= 0 && string.Equals(_history[_historyIndex].Path, path, StringComparison.OrdinalIgnoreCase))
                addHistory = false;
            else
            {
                if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(request);
                _historyIndex = _history.Count - 1;
            }
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCancellation.Token;
        _activeProvider?.Dispose();
        _activeProvider = null;
        Result = null;
        Content = LoadingView();
        PreviewChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var provider = _registry.Resolve(request);
            if (provider is null)
            {
                ApplyResult(FilePreviewResult.Failure(path, "没有可用的预览 provider。"));
                return;
            }
            var result = await provider.LoadAsync(request, token);
            token.ThrowIfCancellationRequested();
            _activeProvider = provider;
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ApplyResult(FilePreviewResult.Failure(path, $"预览失败：{error.Message}"));
        }
    }

    public Task GoBackAsync() => CanGoBack
        ? NavigateHistoryAsync(_historyIndex - 1)
        : Task.CompletedTask;

    public Task GoForwardAsync() => CanGoForward
        ? NavigateHistoryAsync(_historyIndex + 1)
        : Task.CompletedTask;

    public void Close()
    {
        _loadCancellation?.Cancel();
        _activeProvider?.Dispose();
        _activeProvider = null;
        Result = null;
        Content = null;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task NavigateHistoryAsync(int index)
    {
        if (index < 0 || index >= _history.Count) return;
        _historyIndex = index;
        var request = _history[index];
        await OpenAsync(request.ProjectRoot, request.Path, addHistory: false);
    }

    private void ApplyResult(FilePreviewResult result)
    {
        Result = result;
        Content = result.IsError
            ? ErrorView(result.Error ?? "无法预览此文件。")
            : _activeProvider?.CreateView(result) ?? ErrorView("预览 provider 未创建视图。");
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Control LoadingView() => new TextBlock
    {
        Text = "正在读取文件…",
        Margin = new Avalonia.Thickness(16),
        Foreground = Brushes.Gray,
    };

    private static Control ErrorView(string message) => new Border
    {
        Padding = new Avalonia.Thickness(16),
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _activeProvider?.Dispose();
        _registry.Dispose();
    }
}
