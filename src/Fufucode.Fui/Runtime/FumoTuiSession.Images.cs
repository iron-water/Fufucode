using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

internal sealed partial class FumoTuiSession
{
    // Core's native bit follows the model configured on open, even after /model or restoring a different model.
    // Look up the actual model at the endpoint this chat's native HTTP client was constructed with.
    private readonly CompatibleEndpoint? _imageEndpoint;
    private readonly string? _nativeProbeModel;
    private string? _imageModel;
    private Task<bool?>? _imageProbe;
    private DateTimeOffset _imageProbeTime;

    public bool CanSendImages
    {
        get
        {
            lock (_lifecycle)
            {
                if (_closing || _imageEndpoint is null || _session.Chat is not IChatAttachments media ||
                    Model is not { Length: > 0 } model) return false;
                var probe = GetImageProbe(model);
                return probe.IsCompletedSuccessfully && AllowsImages(model, media, probe.Result);
            }
        }
    }

    public async Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) { await SendAsync(text, cancellationToken); return; }

        string model;
        long revision;
        Task<bool?> probe;
        lock (_lifecycle)
        {
            EnsureIdleForPrompt(cancellationToken);
            if (_imageEndpoint is null || Model is not { Length: > 0 } selected)
                throw new InvalidOperationException("未配置图片输入所需的模型端点或模型。");
            model = selected;
            revision = _turnRevision;
            probe = GetImageProbe(model);
        }
        var declared = await probe.WaitAsync(cancellationToken);
        Task submitted;
        lock (_lifecycle)
        {
            EnsureIdleForPrompt(cancellationToken);
            if (Model != model || revision != _turnRevision) throw new InvalidOperationException("模型或回合已切换；请重新发送。");
            if (_session.Chat is not IChatAttachments media) throw new InvalidOperationException("此 FUMO 会话未提供图片发送接口。");
            if (!AllowsImages(model, media, declared))
                throw new InvalidOperationException(declared == false
                    ? $"模型 {model} 不支持图片输入。"
                    : $"无法确认模型 {model} 的图片输入能力。");
            var displayImages = ToDisplayImages(images);
            submitted = SubmitPrompt(() => media.SendWithImagesAsync(text, images, cancellationToken), displayImages);
        }
        await submitted;
    }

    private static TuiImage[] ToDisplayImages(IReadOnlyList<ChatImage> images)
    {
        var list = new TuiImage[images.Count];
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            list[i] = new TuiImage($"图片 {i + 1}", img.MimeType, LoadBytes: () =>
            {
                try { return Convert.FromBase64String(img.Data); }
                catch (FormatException) { return null; }
            }, MaxRows: 12);
        }
        return list;
    }

    private bool AllowsImages(string model, IChatAttachments media, bool? declared) =>
        model == _nativeProbeModel ? media.CanSendImages && declared != false : declared == true;

    private Task<bool?> GetImageProbe(string model)
    {
        if (_imageModel == model && _imageProbe is { IsFaulted: false, IsCanceled: false } &&
            DateTimeOffset.UtcNow - _imageProbeTime < TimeSpan.FromMinutes(5))
            return _imageProbe;
        _imageModel = model;
        _imageProbeTime = DateTimeOffset.UtcNow;
        return _imageProbe = QueryImageSupportAsync(_imageEndpoint!, model, _workbenchLifetime.Token);
    }

    private static async Task<bool?> QueryImageSupportAsync(CompatibleEndpoint endpoint, string model, CancellationToken ct)
    {
        ICredentialResolver credentials = OperatingSystem.IsWindows()
            ? new WindowsPasswordVaultCredentialResolver() : new EnvironmentCredentialResolver();
        var apiKey = await credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, ct);
        return await ModelCapabilityProbe.SupportsImageInputAsync(endpoint.BaseUrl, apiKey, model, ct, endpoint.Api);
    }
}
