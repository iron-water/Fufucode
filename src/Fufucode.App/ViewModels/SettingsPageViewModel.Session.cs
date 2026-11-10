namespace Fufucode.App.ViewModels;

public sealed partial class SettingsPageViewModel
{
    /// <summary>其他窗口改变共享会话模型后更新事实显示，不重新提交模型选择命令。</summary>
    internal void RefreshSharedSessionModel()
    {
        var model = _context?.CurrentModel();
        if (model == ConfirmedModel) return;
        ConfirmedModel = model;
        CurrentModelText = model ?? "未知";
        _context?.ModelApplied?.Invoke(model ?? string.Empty);
    }
}
