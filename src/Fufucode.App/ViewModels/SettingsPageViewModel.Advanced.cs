namespace Fufucode.App.ViewModels;

/// <summary>设置 → 高级（一级分类）：FUMO 内核与其他高级配置。</summary>
public sealed partial class SettingsPageViewModel
{
    public bool ShowAdvanced => SelectedCategory?.Id == "advanced";
    public AdvancedSettingsViewModel Advanced { get; } = new();
}
