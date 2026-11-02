namespace Fufucode.App.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private object? _graphSettingsContent;
    public bool ShowGraphSettings => SelectedCategory?.Id == "automation";
    public object? GraphSettingsContent
    {
        get => _graphSettingsContent;
        set
        {
            if (ReferenceEquals(_graphSettingsContent, value)) return;
            _graphSettingsContent = value;
            OnPropertyChanged();
        }
    }
}
