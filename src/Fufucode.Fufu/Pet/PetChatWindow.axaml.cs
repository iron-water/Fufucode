using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Fufu.Bot;

namespace Fufucode.Fufu.Pet;

/// <summary>聊天气泡行（供 XAML 编译绑定引用，须顶级公开类型）。</summary>
public sealed record ChatLine(string Text, HorizontalAlignment Alignment, string BubbleColor);

/// <summary>FUFU 的聊天窗：真实 FUMO 会话流渲染（消息块重建）、发送/停止、工具审批条。</summary>
public sealed partial class PetChatWindow : Window
{

    private const string UserBubble = "#2B5C6E";
    private const string BotBubble = "#33333C";
    private const string SysBubble = "#4A2A2A";

    private readonly FufuSessionHost _host;
    private readonly ObservableCollection<ChatLine> _lines = new();
    private readonly Dictionary<string, (StringBuilder Text, ChatLine Line)> _drafts = new(StringComparer.Ordinal);
    private ExtensionUiRequest? _pendingApproval;

    public PetChatWindow(FufuSessionHost host)
    {
        _host = host;
        InitializeComponent();
        Messages.ItemsSource = _lines;

        NoEndpointBanner.IsVisible = host.Endpoint is null;
        SendButton.Click += async (_, _) => await SendAsync();
        InputBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SendAsync(); };
        StopButton.Click += async (_, _) => await _host.StopAsync();
        ApproveButton.Click += async (_, _) => await RespondAsync(true);
        DenyButton.Click += async (_, _) => await RespondAsync(false);

        host.SessionEventReceived += OnHostEvent;
        host.ApprovalRequested += r => Dispatcher.UIThread.Post(() => ShowApproval(r));
        host.ApprovalClosed += r => Dispatcher.UIThread.Post(() => HideApproval(r));
        Closed += (_, _) => host.SessionEventReceived -= OnHostEvent;
        Opened += async (_, _) => await RestoreHistoryAsync();
    }

    private async Task RestoreHistoryAsync()
    {
        try
        {
            var history = await _host.LoadChatHistoryAsync();
            if (history.Count == 0) return;

            foreach (var message in history)
            {
                var text = MessageText(message);
                if (string.IsNullOrWhiteSpace(text)) continue;
                Append(text,
                    message.Role == MessageRole.User ? UserBubble : BotBubble,
                    message.Role == MessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }
        }
        catch (Exception ex)
        {
            Append($"（历史恢复失败：{ex.Message}）", SysBubble, HorizontalAlignment.Left);
        }
    }

    private void OnHostEvent(object? sender, SessionEvent e) =>
        Dispatcher.UIThread.Post(() => OnSessionEvent(e));

    private void OnSessionEvent(SessionEvent e)
    {
        switch (e)
        {
            case SessionEvent.MessageStarted m when m.Role == MessageRole.Assistant:
                _drafts[m.MessageId] = (new StringBuilder(), Append("…", BotBubble, HorizontalAlignment.Left));
                break;
            case SessionEvent.TextAppended t:
                if (_drafts.TryGetValue(t.MessageId, out var d))
                {
                    d.Text.Append(t.Delta);
                    ReplaceLine(d.Line, d.Text.ToString(), BotBubble);
                }
                break;
            case SessionEvent.MessageFinished f:
                if (f.FinalMessage.Role == MessageRole.User)
                    Append(MessageText(f.FinalMessage), UserBubble, HorizontalAlignment.Right);
                else
                {
                    var text = MessageText(f.FinalMessage);
                    if (_drafts.Remove(f.MessageId, out var d2)) ReplaceLine(d2.Line, text, BotBubble);
                    else Append(text, BotBubble, HorizontalAlignment.Left);
                }
                break;
            case SessionEvent.TurnSettled s when s.Summary is { Length: > 0 } sum:
                Append($"（回合结束：{sum}）", SysBubble, HorizontalAlignment.Left);
                break;
            case SessionEvent.SessionError err:
                Append($"错误：{err.Message}", SysBubble, HorizontalAlignment.Left);
                break;
            case SessionEvent.QueueCleared cleared:
                foreach (var text in cleared.ClearedTexts) Append($"（已取消排队：{text}）", SysBubble, HorizontalAlignment.Left);
                break;
        }
        StopButton.IsEnabled = _host.CanStop;
        SendButton.IsEnabled = _host.Endpoint is not null;
    }

    /// <summary>显示文本 = 消息的 TextBlock 拼接（Thinking/ToolCall 块不入正文流）。</summary>
    private static string MessageText(ChatMessage msg) =>
        string.Concat(msg.Blocks.OfType<Fufucode.App.Abstractions.TextBlock>().Select(b => b.Text));

    private ChatLine Append(string text, string color, HorizontalAlignment align)
    {
        var line = new ChatLine(text, align, color);
        _lines.Add(line);
        MessageScroll.ScrollToEnd();
        return line;
    }

    private void ReplaceLine(ChatLine old, string text, string color)
    {
        var i = _lines.IndexOf(old);
        if (i >= 0) _lines[i] = old with { Text = text };
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (_host.Endpoint is null) { Append("（未配置端点，无法发送）", SysBubble, HorizontalAlignment.Left); return; }
        InputBox.Text = "";
        try { await _host.SendAsync(text); }
        catch (Exception ex) { Append($"发送失败：{ex.Message}", SysBubble, HorizontalAlignment.Left); }
        MessageScroll.ScrollToEnd();
    }

    private void ShowApproval(ExtensionUiRequest request)
    {
        _pendingApproval = request;
        ApprovalText.Text = request.Payload.TryGetProperty("message", out var m) ? m.GetString() : request.Method;
        ApprovalBar.IsVisible = true;
    }

    private void HideApproval(ExtensionUiRequest request)
    {
        if (_pendingApproval?.Id == request.Id) { _pendingApproval = null; ApprovalBar.IsVisible = false; }
    }

    private async Task RespondAsync(bool approved)
    {
        if (_pendingApproval is { } req)
        {
            _pendingApproval = null;
            ApprovalBar.IsVisible = false;
            await _host.RespondToApprovalAsync(req, approved);
        }
    }
}
