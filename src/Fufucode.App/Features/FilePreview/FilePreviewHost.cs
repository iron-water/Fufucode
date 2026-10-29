using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.App.Features.FilePreview;

/// <summary>统一预览宿主：视图随切换释放，provider 由注册表持有，旧请求不能覆盖新结果。</summary>
public sealed class FilePreviewHost : ContentControl, IDisposable
{
    private readonly FilePreviewRegistry _registry;
    private readonly List<FilePreviewRequest> _history = [];
    private CancellationTokenSource? _loadCancellation;
    private int _historyIndex = -1;
    private int _generation;
    private IFilePreviewProvider? _activeProvider;
    private bool _disposed;

    public FilePreviewHost() : this(null) { }
    public FilePreviewHost(FilePreviewRegistry? registry) => _registry = registry ?? FilePreviewRegistry.CreateDefault();
    public FilePreviewResult? Result { get; private set; }
    public FilePreviewRequest? CurrentRequest { get; private set; }
    public FilePreviewRegistry Registry => _registry;
    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    public bool IsLoading { get; private set; }
    public bool IsSourceMode { get; private set; }
    public bool CanShowSource => Result is { Text: not null } &&
        Result.Kind is FilePreviewKind.Markdown or FilePreviewKind.Mermaid or FilePreviewKind.Structured or FilePreviewKind.Table;
    public event EventHandler? PreviewChanged;
    public string FindStatus => Content is TextPreviewControl text ? text.FindStatus : string.Empty;
    public bool IsWordWrapEnabled => Content is TextPreviewControl text && text.IsWordWrapEnabled;
    public ImagePreviewControl? ImageContent => Content as ImagePreviewControl;

    public string Find(string? query, bool forward = true)
    {
        var status = Content is TextPreviewControl text ? text.Find(query, forward) : string.Empty;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        return status;
    }

    public void ToggleWordWrap()
    {
        if (Content is not TextPreviewControl text) return;
        text.SetWordWrap(!text.IsWordWrapEnabled);
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleSource()
    {
        if (!CanShowSource || Result is not { } result) return;
        IsSourceMode = !IsSourceMode;
        try
        {
            ReplaceContent(IsSourceMode
                ? new TextPreviewControl(result.Text!, Path.GetExtension(result.Path).TrimStart('.'))
                : _activeProvider!.CreateView(result));
        }
        catch (Exception error) { ReplaceContent(ErrorView("预览失败：" + error.Message)); }
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ImageZoomIn() => ImageContent?.ZoomBy(1.25);
    public void ImageZoomOut() => ImageContent?.ZoomBy(0.8);
    public void ImageFit() => ImageContent?.SetFitMode();
    public void ImageOriginal() => ImageContent?.SetOriginalSize();
    public void ImageRotateLeft() => ImageContent?.RotateBy(-90);
    public void ImageRotateRight() => ImageContent?.RotateBy(90);
    public void ImageToggleCheckerboard() => ImageContent?.ToggleCheckerboard();

    public async Task OpenAsync(string projectRoot, string path, bool addHistory = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new FilePreviewRequest(projectRoot, path);
        try { request = request with { Path = request.FullPath }; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        if (addHistory && (_historyIndex < 0 || _history[_historyIndex] != request))
        {
            if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(request);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCancellation.Token;
        var generation = ++_generation;
        _activeProvider = null;
        CurrentRequest = request;
        Result = null;
        IsLoading = true;
        IsSourceMode = false;
        ReplaceContent(LoadingView());
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var provider = _registry.Resolve(request);
            if (provider is null) throw new IOException("没有可用的文件预览。");
            var result = await provider.LoadAsync(request, token);
            if (generation != _generation || _disposed) return;
            token.ThrowIfCancellationRequested();
            _activeProvider = provider;
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation && !_disposed && !token.IsCancellationRequested)
                ApplyResult(FilePreviewResult.Failure(request.Path, $"预览失败：{error.Message}"));
        }
        finally
        {
            if (generation == _generation && !_disposed)
            {
                IsLoading = false;
                if (token.IsCancellationRequested && Result is null)
                    ApplyResult(FilePreviewResult.Failure(request.Path, "读取已取消。"));
                PreviewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Task GoBackAsync() => CanGoBack ? NavigateHistoryAsync(_historyIndex - 1) : Task.CompletedTask;
    public Task GoForwardAsync() => CanGoForward ? NavigateHistoryAsync(_historyIndex + 1) : Task.CompletedTask;

    public void Close()
    {
        ++_generation;
        _loadCancellation?.Cancel();
        _activeProvider = null;
        CurrentRequest = null;
        Result = null;
        IsLoading = false;
        IsSourceMode = false;
        ReplaceContent(null);
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        _history.Clear();
        _historyIndex = -1;
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
        var view = result.IsError ? ErrorView(result.Error ?? "无法预览此文件。") : _activeProvider?.CreateView(result);
        Result = result;
        ReplaceContent(view ?? ErrorView("未能创建文件预览。"));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceContent(Control? view)
    {
        var previous = Content;
        Content = view;
        if (previous is IDisposable disposable) disposable.Dispose();
    }

    private static Control LoadingView() => new TextBlock
    {
        Text = "正在读取文件…", Margin = new Avalonia.Thickness(16), Foreground = Brushes.Gray,
    };
    private static Control ErrorView(string message) => new Border
    {
        Padding = new Avalonia.Thickness(16),
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
    };

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _loadCancellation?.Dispose();
        _registry.Dispose();
    }
}
