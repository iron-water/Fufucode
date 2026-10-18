using System.Linq;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 真实会话的扩展 UI 接线（计划 §8.4、decisions.md [轨道B] 冻结清单）：
/// 对话框类请求由 <see cref="ExtensionUiDialogHost"/> 弹窗处理；
/// 免答通知类在此映射——notify/setWidget → 时间线轻提示，setStatus → 状态栏，
/// setTitle → 窗口标题（无标题通道时降级为提示），set_editor_text → 输入框（保护未发送草稿）。
/// 已知通知类只转发展示，不应答；扩展错误透出诊断。
/// </summary>
public static class SessionUiWiring
{
    /// <summary>
    /// 接线扩展 UI 通知。返回句柄用于解除订阅；会话未实现 <see cref="IExtensionUiSession"/> 时返回 null。
    /// 旧视图模型被替换时必须释放句柄，否则旧时间线/输入框仍会被扩展事件驱动。
    /// </summary>
    public static System.IDisposable? AttachExtensionUi(IChatSession chat, TimelineViewModel timeline,
        InputViewModel input, StatusBarViewModel statusBar, System.Action<string>? titleSetter = null)
    {
        if (chat is not IExtensionUiSession extensionUi)
        {
            return null;
        }

        void OnNotification(PiExtensionUiRequestEvent notice)
        {
            switch (notice.Method)
            {
                case "notify":
                    timeline.AddNotification($"扩展通知：{StringField(notice.Raw, "message") ?? notice.Raw.GetRawText()}");
                    break;
                case "setStatus":
                    statusBar.SetExtensionStatus(
                        StringField(notice.Raw, "statusKey") ?? string.Empty,
                        StringField(notice.Raw, "statusText") ?? string.Empty);
                    break;
                case "setWidget":
                    // OMP clears built-in widgets with an absent/null widgetLines payload.
                    // A clear operation has no displayable text; don't expose its raw RPC record.
                    if (chat is IBackendChatSession { BackendId: "omp" } &&
                        (!notice.Raw.TryGetProperty("widgetLines", out var ompLines) || ompLines.ValueKind == JsonValueKind.Null))
                        break;
                    var lines = notice.Raw.ValueKind == JsonValueKind.Object &&
                                notice.Raw.TryGetProperty("widgetLines", out var widgetLines) &&
                                widgetLines.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", widgetLines.EnumerateArray()
                            .Where(line => line.ValueKind == JsonValueKind.String)
                            .Select(line => line.GetString()))
                        : notice.Raw.GetRawText();
                    timeline.AddNotification($"扩展部件（{StringField(notice.Raw, "widgetKey") ?? "?"}）：\n{lines}");
                    break;
                case "setTitle":
                    var title = StringField(notice.Raw, "title");
                    if (title is not null && titleSetter is not null)
                    {
                        titleSetter(title);
                    }
                    else
                    {
                        timeline.AddNotification($"扩展标题：{title ?? notice.Raw.GetRawText()}");
                    }

                    break;
                case "set_editor_text":
                    var text = StringField(notice.Raw, "text") ?? string.Empty;
                    var appliedDirectly = input.ApplyExternalText(text);
                    if (!appliedDirectly)
                    {
                        timeline.AddNotification("扩展已写入输入内容；因存在未发送草稿，已存为待恢复文本（计划 §8.4）。");
                    }

                    break;
                default:
                    timeline.AddNotification($"扩展通知（{notice.Method}）：{notice.Raw.GetRawText()}");
                    break;
            }
        }

        void OnError(PiExtensionErrorEvent error) =>
            timeline.AddNotification($"扩展错误：{error.ExtensionPath} 在 {error.EventName} 中失败——{error.Error}");

        extensionUi.ExtensionUiNotification += OnNotification;
        extensionUi.ExtensionError += OnError;
        return new ExtensionUiWiringHandle(extensionUi, OnNotification, OnError);
    }

    /// <summary>一次扩展 UI 接线：释放时从会话事件上移除同一对委托（可重复释放）。</summary>
    private sealed class ExtensionUiWiringHandle(
        IExtensionUiSession session,
        System.Action<PiExtensionUiRequestEvent> notification,
        System.Action<PiExtensionErrorEvent> error) : System.IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            session.ExtensionUiNotification -= notification;
            session.ExtensionError -= error;
        }
    }

    private static string? StringField(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
}
