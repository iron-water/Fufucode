using System;
using Avalonia;
using Avalonia.Controls;

namespace Fufucode.App.Views;

/// <summary>与文件和会话行一致，使用一个逻辑像素的选中描边。</summary>
public sealed class ModelChoiceBorder : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    public ModelChoiceBorder() => BorderThickness = new Thickness(1);
}
