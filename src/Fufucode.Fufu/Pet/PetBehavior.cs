using Avalonia.Threading;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// 确定性宠物行为状态机（任务书 §四）：
/// Idle → (自由移动时) Walk → Idle/Rest 循环；Interact 由点击触发；
/// Thinking/Working/Success/Error/Attention 由 Bot 宿主经 <see cref="Notify"/> 驱动。
/// 不调用任何模型；时间片全部为确定性计时器。
/// </summary>
public sealed class PetBehavior : IDisposable
{
    private static readonly TimeSpan InteractDuration = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan ResultDuration = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan AttentionMinimum = TimeSpan.FromMilliseconds(4000);

    private readonly DispatcherTimer _scheduler;
    private readonly Random _random = new();
    private PetState _state = PetState.Idle;
    private PetState _base = PetState.Idle;   // 自由移动循环内的底层状态
    private DateTimeOffset _until;            // 短暂状态（Interact/Success/Error）到期时刻
    private DateTimeOffset _restUntil;

    public PetBehavior()
    {
        _scheduler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _scheduler.Tick += (_, _) => Tick();
        _scheduler.Start();
    }

    public event Action<PetState>? StateChanged;

    /// <summary>自由移动是否允许进入 Walk（设置项）；false 时只做 Idle/Rest。</summary>
    public bool FreeRoam { get; set; } = true;

    /// <summary>暂停移动：冻结在原地，与 FreeRoam 的语义分离（暂停是临时态）。</summary>
    public bool MovementPaused { get; set; }

    public PetState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>点击角色：进入短暂 Interact。</summary>
    public void Interact()
    {
        _until = DateTimeOffset.UtcNow + InteractDuration;
        State = PetState.Interact;
    }

    /// <summary>
    /// Bot 宿主上报的事件状态（任务开始/完成/失败/需关注/思考中）。
    /// 短暂状态（Success/Error）到期自动回落；持续状态（Thinking/Working/Attention）由下一次事件解除。
    /// </summary>
    public void Notify(PetState eventState)
    {
        switch (eventState)
        {
            case PetState.Success:
            case PetState.Error:
                _until = DateTimeOffset.UtcNow + ResultDuration;
                State = eventState;
                break;
            case PetState.Thinking:
            case PetState.Working:
            case PetState.Attention:
                _until = DateTimeOffset.MaxValue;
                if (eventState == PetState.Attention) _until = DateTimeOffset.UtcNow + AttentionMinimum;
                State = eventState;
                break;
            case PetState.Idle:
                _until = DateTimeOffset.MinValue;
                State = _base;
                break;
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _until) return;   // 事件态/交互态未到期，优先保持
        _until = DateTimeOffset.MinValue;

        if (_state is PetState.Interact or PetState.Success or PetState.Error
            or PetState.Thinking or PetState.Working or PetState.Attention)
        {
            State = _base;          // 回落到自由移动循环的底层状态
            return;
        }

        if (!FreeRoam || MovementPaused)
        {
            _base = PetState.Idle;
            State = PetState.Idle;
            _restUntil = now + TimeSpan.FromSeconds(4);
            return;
        }

        // 自由移动节奏：休息结束 → 走一段 → 停一会 → 偶尔休息。
        if (_base == PetState.Rest && now < _restUntil) return;

        var roll = _random.NextDouble();
        if (_base == PetState.Walk)
        {
            // 走完一段：短暂待机，或进入休息
            _base = roll < 0.25 ? PetState.Rest : PetState.Idle;
            if (_base == PetState.Rest) _restUntil = now + TimeSpan.FromSeconds(3 + _random.NextDouble() * 6);
            State = _base;
            return;
        }

        if (roll < 0.35)
        {
            _base = PetState.Walk;
            State = PetState.Walk;
            return;
        }

        _base = PetState.Idle;
        State = PetState.Idle;
    }

    public void Dispose() => _scheduler.Stop();
}
