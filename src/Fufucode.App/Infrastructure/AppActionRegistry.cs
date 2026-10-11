using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Fufucode.App.Infrastructure;

/// <summary>All UI entry points use the same action and current capability condition.</summary>
public sealed record AppActionDefinition(string Id, string Title, string Group, string Shortcut,
    string Description, Func<Task> Execute, Func<string?>? DisabledReason = null, bool IsDestructive = false);

public sealed record AppActionExecutionResult(bool Executed, string? Reason);

public sealed class AppActionRegistry
{
    private readonly Dictionary<string, AppActionDefinition> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAsyncRelayCommand> _commands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _executing = new(StringComparer.Ordinal);

    public IEnumerable<AppActionDefinition> Actions => _actions.Values;
    public event Action? Changed;
    public event Action<string>? ExecutionFailed;

    public void UpdateShortcutLabels(Func<string, string> label)
    {
        var changed = false;
        foreach (var id in new List<string>(_actions.Keys))
        {
            var value = label(id);
            if (_actions[id].Shortcut == value) continue;
            _actions[id] = _actions[id] with { Shortcut = value };
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void Register(AppActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Id);
        if (!_actions.TryAdd(action.Id, action)) throw new InvalidOperationException("Action already registered: " + action.Id);
        _commands.Add(action.Id, new AsyncRelayCommand(async () => { await ExecuteAsync(action.Id); },
            () => GetDisabledReason(action.Id) is null));
        RefreshCapabilities();
    }

    public IAsyncRelayCommand GetCommand(string id) => _commands.TryGetValue(id, out var command)
        ? command : throw new KeyNotFoundException("Action is not registered: " + id);

    public string? GetDisabledReason(string id)
    {
        if (!_actions.TryGetValue(id, out var action)) return "动作尚未注册";
        if (_executing.Contains(id)) return "此动作正在执行";
        return action.DisabledReason?.Invoke();
    }

    public async Task<AppActionExecutionResult> ExecuteAsync(string id)
    {
        if (!_actions.TryGetValue(id, out var action)) return new(false, "动作尚未注册");
        if (GetDisabledReason(id) is { } reason) return new(false, reason);
        _executing.Add(id);
        RefreshCapabilities();
        try { await action.Execute(); return new(true, null); }
        catch (Exception error)
        {
            var message = error.GetBaseException().Message;
            ExecutionFailed?.Invoke(message);
            return new(false, message);
        }
        finally { _executing.Remove(id); RefreshCapabilities(); }
    }

    public void RefreshCapabilities()
    {
        foreach (var command in _commands.Values) command.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }
}
