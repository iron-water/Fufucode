using Avalonia.Controls;
using Fufucode.Core;

namespace Fufucode.App;

/// <summary>
/// 项目信任询问对话框：ShowDialog 返回 <see cref="ProjectTrustDecision"/>；
/// 点 X 关闭（未作选择）返回 null，调用方按"放弃"处理。
/// </summary>
public partial class ProjectTrustDialog : Window
{
    /// <summary>仅供设计器/XAML 加载器实例化；运行时必须传入 <see cref="ProjectTrustStatus"/>。</summary>
    public ProjectTrustDialog()
    {
        InitializeComponent();
    }

    public ProjectTrustDialog(ProjectTrustStatus status) : this()
    {
        ProjectPathText.Text = status.ProjectPath;
        ResourceList.ItemsSource = status.ProtectedResources;
    }

    private void OnTrust(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(ProjectTrustDecision.Trust);

    private void OnDoNotTrust(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(ProjectTrustDecision.DoNotTrust);
}
