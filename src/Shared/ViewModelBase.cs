using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

// 视图模型的公共基类。宿主与插件使用同一份源码（主程序集直接编译，插件通过文件链接编译），
// 因此插件的页面视图模型不需要、也不允许引用 Fufucode.App.exe。

/// <summary>ObservableObject 的可复用基类。</summary>
public abstract class ViewModelBase : ObservableObject
{
}
