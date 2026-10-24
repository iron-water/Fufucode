using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fufucode.Core;

namespace Fufucode.App;

/// <summary>
/// 扩展 UI 对话框（decisions.md [轨道B] 冻结清单）：confirm/select/input/editor 四种。
/// 关闭按钮（X）一律按取消/拒绝处理（fail-safe）；每个请求只应答一次；
/// 本地超时或会话断开由 Core 发起 <see cref="IExtensionUiSession.ExtensionUiRequestClosed"/>，
/// 宿主据此撤下本窗口（不假设服务端清理）。
/// </summary>
public partial class ExtensionUiDialogWindow : Window
{
    private readonly ExtensionUiRequest? _request;
    private readonly Func<ExtensionUiRequest, PiExtensionUiResponse, Task>? _respond;
    private readonly Action? _alwaysAllow;
    private readonly TextBox? _entryBox;
    private readonly ListBox? _optionsList;
    private bool _answered;

    /// <summary>仅供设计器/XAML 运行时加载器实例化（AVLN3001）；运行时路径必须使用带参构造。</summary>
    public ExtensionUiDialogWindow()
    {
        InitializeComponent();
        DialogTitle.Text = "（设计预览）";
    }

    public ExtensionUiDialogWindow(ExtensionUiRequest request,
        Func<ExtensionUiRequest, PiExtensionUiResponse, Task> respond, Action? alwaysAllow = null)
    {
        _request = request;
        _respond = respond;
        _alwaysAllow = alwaysAllow;
        InitializeComponent();

        DialogTitle.Text = Field("title") ?? $"扩展请求（{request.Method}）";

        switch (request.Method)
        {
            case "confirm":
                AddBodyText(Field("message") ?? "（无说明）");
                AddButton("拒绝", () => Answer(PiExtensionUiResponse.Confirm(false)));
                // “始终允许”与系统通知上的同名按钮同义：落一条按项目 + 工具的记忆规则。
                if (_alwaysAllow is not null)
                    AddButton("始终允许", () => { _alwaysAllow(); Answer(PiExtensionUiResponse.Confirm(true)); });
                AddButton("允许", () => Answer(PiExtensionUiResponse.Confirm(true)), accent: true);
                break;

            case "select":
                _optionsList = new ListBox { MinHeight = 60 };
                foreach (var option in Options())
                {
                    _optionsList.Items.Add(new ListBoxItem { Content = option });
                }

                if (_optionsList.Items.Count > 0)
                {
                    _optionsList.SelectedIndex = 0;
                }

                DialogBody.Children.Add(_optionsList);
                AddButton("取消", () => Answer(PiExtensionUiResponse.Cancel()));
                AddButton("确定", () => Answer(_optionsList?.SelectedItem is ListBoxItem { Content: string value }
                    ? PiExtensionUiResponse.WithValue(value)
                    : PiExtensionUiResponse.Cancel()), accent: true);
                break;

            case "input":
                _entryBox = new TextBox
                {
                    PlaceholderText = Field("placeholder") ?? string.Empty,
                    MinWidth = 320,
                };
                DialogBody.Children.Add(_entryBox);
                AddButton("取消", () => Answer(PiExtensionUiResponse.Cancel()));
                AddButton("确定", () => Answer(PiExtensionUiResponse.WithValue(_entryBox?.Text ?? string.Empty)),
                    accent: true);
                break;

            case "editor":
                // editor 在 Pi 端无 timeout（[轨道B] 清单）：host 不应答会永久挂起，
                // 因此确定/取消两条路径都必须可达；Core 另有本地 60s 截止兜底。
                AddBodyText("编辑内容后确定，或取消（按拒绝处理）：");
                _entryBox = new TextBox
                {
                    Text = Field("prefill") ?? string.Empty,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 160,
                    MinWidth = 440,
                };
                DialogBody.Children.Add(_entryBox);
                AddButton("取消", () => Answer(PiExtensionUiResponse.Cancel()));
                AddButton("确定", () => Answer(PiExtensionUiResponse.WithValue(_entryBox?.Text ?? string.Empty)),
                    accent: true);
                break;

            default:
                // 契约外的 method 不应到达这里（Core 已忽略未知 method）；兜底按取消处理。
                AddBodyText($"未支持的请求类型 {request.Method}，将按取消处理。");
                AddButton("关闭", () => Answer(PiExtensionUiResponse.Cancel()), accent: true);
                break;
        }
    }

    private void AddBodyText(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Classes.Add("body");
        textBlock.Classes.Add("secondary");
        DialogBody.Children.Add(textBlock);
    }

    private void AddButton(string label, Action onClick, bool accent = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 76,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        button.Click += (_, _) => onClick();
        DialogButtons.Children.Add(button);
    }

    private void Answer(PiExtensionUiResponse response)
    {
        if (_answered)
        {
            return;
        }

        _answered = true;
        _ = RespondAndCloseAsync(response);
    }

    private async Task RespondAndCloseAsync(PiExtensionUiResponse response)
    {
        try
        {
            if (_respond is not null && _request is not null)
            {
                await _respond(_request, response);
            }
        }
        finally
        {
            Close();
        }
    }

    /// <summary>请求已失效（超时/会话断开/已应答）：不补应答，直接撤下。</summary>
    public void Dismiss()
    {
        _answered = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 用户点 X：按取消/拒绝应答（fail-safe），每个请求仅一次。
        if (!_answered)
        {
            _answered = true;
            _ = RespondAndCloseAsync(PiExtensionUiResponse.Cancel());
        }

        base.OnClosing(e);
    }

    private string? Field(string name) =>
        _request is not null &&
        _request.Payload.ValueKind == JsonValueKind.Object &&
        _request.Payload.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private string[] Options() =>
        _request is not null &&
        _request.Payload.ValueKind == JsonValueKind.Object &&
        _request.Payload.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
            ? options.EnumerateArray()
                .Where(option => option.ValueKind == JsonValueKind.String)
                .Select(option => option.GetString()!)
                .ToArray()
            : [];
}
