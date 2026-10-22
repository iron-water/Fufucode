using Avalonia.Controls;
using Fufucode.Core;

namespace Fufucode.Fufu.Pet;

/// <summary>FUFU 的 OpenAI 兼容端点设置窗口；密钥值只通过环境变量提供。</summary>
public sealed partial class EndpointSettingsWindow : Window
{
    private readonly AppSettingsStore _settings;
    private readonly Action _saved;

    public EndpointSettingsWindow(AppSettingsStore settings, Action saved)
    {
        _settings = settings;
        _saved = saved;
        InitializeComponent();

        var endpoint = settings.Current.Endpoint;
        ProviderBox.Text = endpoint?.ProviderName ?? "openai";
        BaseUrlBox.Text = endpoint?.BaseUrl ?? "https://api.openai.com/v1";
        ModelBox.Text = endpoint?.ModelId ?? string.Empty;
        ApiKeyVariableBox.Text = endpoint?.ApiKeyEnvironmentVariable ?? "OPENAI_API_KEY";

        SaveButton.Click += (_, _) => Save();
        ClearButton.Click += (_, _) => Clear();
        CancelButton.Click += (_, _) => Close();
    }

    private void Save()
    {
        var provider = ProviderBox.Text?.Trim() ?? string.Empty;
        var baseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = ModelBox.Text?.Trim() ?? string.Empty;
        var keyVariable = ApiKeyVariableBox.Text?.Trim() ?? string.Empty;
        try
        {
            _settings.Update(current => current with
            {
                Endpoint = new CompatibleEndpoint(provider, baseUrl, model, keyVariable),
            });
            _saved();
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message switch
            {
                "Invalid compatible endpoint." => "请填写有效的提供商、HTTPS/HTTP 地址、模型 ID 和环境变量名。",
                _ => $"保存失败：{ex.Message}",
            };
            ErrorBorder.IsVisible = true;
        }
    }

    private void Clear()
    {
        _settings.Update(current => current with { Endpoint = null });
        _saved();
        Close();
    }
}
