using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Plugins;

/// <summary>插件分类。</summary>
public enum PluginCategory
{
    /// <summary>后端内核插件（Pi、OMP、Codex、DSH 等）。</summary>
    BackendEngine,

    /// <summary>智能体高级能力（浏览器控制、电脑操控等，默认不安装）。</summary>
    AgentCapability,

    /// <summary>后台服务类插件（手机远控、语音朗读等）。</summary>
    Service,

    /// <summary>自动化与调度插件（自动开发工作台、用户定时任务等）。</summary>
    Automation,

    /// <summary>个性化与非基线皮肤包（Aero 玻璃皮肤等）。</summary>
    Theme,

    /// <summary>通用工具与开发扩展。</summary>
    ToolExtension,
}

/// <summary>插件静态定义信息。</summary>
public sealed record PluginDefinition(
    string Id,
    string Name,
    PluginCategory Category,
    string Description,
    string Version,
    string Author,
    IReadOnlyList<string> RequiredPermissions,
    bool DefaultInstalled = true,
    bool DefaultEnabled = true,
    bool IsCoreBuiltin = false,
    // 载荷内嵌在 Core（例如 Seep 逆向工作台）时，不要求 plugins/<id>/manifest.json。
    bool IsEmbedded = false)
{
    /// <summary>
    /// 插件提供的会话人格片段。仅当该插件处于"已安装且已启用"时，
    /// 才由 <see cref="PluginSystemPromptComposer"/> 拼装进会话 System 指令。
    /// 为 null 表示该插件不注入人格，属正常情况（多数插件不需要）。
    /// </summary>
    public string? SystemPromptFragment { get; init; }
}

/// <summary>插件安装运行状态。</summary>
public sealed record PluginState(
    string Id,
    bool IsInstalled,
    bool IsEnabled,
    DateTime LastUpdatedUtc);

/// <summary>插件持久化配置。</summary>
public sealed record PluginConfiguration(
    int Version = 1,
    Dictionary<string, PluginState>? States = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginConfiguration))]
[JsonSerializable(typeof(PluginState))]
internal partial class PluginConfigJsonContext : JsonSerializerContext;

/// <summary>
/// 插件市场权威目录（唯一真源）。
/// 包含原生核心、四后端插件（默认启用）以及高级能力（浏览器控制/电脑操控，默认未安装）。
/// </summary>
public static class PluginMarketplaceCatalog
{
    public static readonly IReadOnlyList<PluginDefinition> All =
    [
        // ── 核心内置 ──
        new("core.csharp", "C# Native 原生内核", PluginCategory.BackendEngine,
            "Fufucode 原生内置 Agent 运行时，基于 .NET 10.0 全托管构建，零外部进程与环境依赖。",
            "1.0.0", "Fufucode Core", [],
            DefaultInstalled: true, DefaultEnabled: true, IsCoreBuiltin: true),

        // ── 后端插件：默认安装状态以真实载荷为准 ──
        // 五类后端目录条目在安装契约上完全同级；默认是否安装由“随包分发”决定，
        // 不把没有载荷的条目标成已安装（那会让用户看到假状态）。
        new("backend.pi", "Pi 后端插件", PluginCategory.BackendEngine,
            "Pi RPC 协议扩展后端，支持官方插件体系与 TypeScript 扩展生态。",
            "1.0.0", "Earendil Works", ["Node.js 运行时", "RPC 管道通信"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("backend.omp", "OMP 后端插件", PluginCategory.BackendEngine,
            "Oh My Pi 独立命令行执行引擎，轻量高效，适合常规批量开发与脚本生成。",
            "1.0.0", "Community", ["外部 CLI 进程"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("backend.codex", "Codex 协议插件", PluginCategory.BackendEngine,
            "Codex 官方 CLI 的 `codex exec --json` 协议适配后端。",
            "1.0.0", "Codex", ["官方 CLI 进程", "网络访问"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("backend.dsh", "DeepSeek Harness 插件", PluginCategory.BackendEngine,
            "DeepSeek Harness 官方 CLI 的 headless JSON 协议适配后端。",
            "1.0.0", "Community", ["外部 CLI 进程", "网络访问"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("backend.claude", "Claude Code 插件", PluginCategory.BackendEngine,
            "Anthropic 官方 Claude Code CLI 的 stream-json 协议适配后端，直接调用官方接口，不读源码；审批沿用统一权限流程。",
            "1.0.0", "Anthropic", ["官方 CLI 进程", "网络访问"],
            DefaultInstalled: false, DefaultEnabled: false),

        // ── 智能体高级能力（默认未安装，按需从市场安装） ──
        new("workspace.subagents", "Subagent 子代理", PluginCategory.AgentCapability,
            "可选子代理运行时：安装后默认使用 faster，stronger 由用户手动切换。",
            "1.0.0", "Fufucode Team", ["模型调用", "独立 Agent 上下文"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("agent.browser-use", "Browser Use 浏览器控制", PluginCategory.AgentCapability,
            "赋予智能体自动化网页操作、CDP 协议调用、DOM 元素提取与页面状态交互能力。",
            "1.0.0", "Fufucode Team", ["WebView2 CDP 控制", "页面 DOM 读取", "网络导航"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("agent.computer-use", "Computer Use 电脑操控", PluginCategory.AgentCapability,
            "允许智能体截取当前桌面屏幕、模拟鼠标点击与键盘输入，直接操控 Windows 应用。",
            "1.0.0", "Fufucode Team", ["屏幕截图获取", "鼠标与键盘模拟", "窗口激活"],
            DefaultInstalled: false, DefaultEnabled: false),

        // ── 工具扩展（默认未安装，按需从市场安装） ──
        new("tool.seep-reverse-lab", "Seep 逆向工程工作台", PluginCategory.ToolExtension,
            "接入 Seep Reverse Lab 工作台：23 个 Radare2 / JADX / Apktool / Frida 逆向工具、"
            + "ReverseLab 知识库检索，以及 127.0.0.1:17890 合规转译中继。"
            + "安装并启用后才启动本地 Python MCP 子进程并注册工具；停用或卸载即刻终止子进程、撤销注册。",
            "1.0.0", "Seep Reverse Lab",
            ["本地 Python 运行时", "启动本地 MCP 子进程（radare2 / JADX / Apktool / Frida）",
             "本机回环端口 17890（合规转译中继）", "读取/写入工作台内任务沙箱目录"],
            DefaultInstalled: false, DefaultEnabled: false, IsEmbedded: true)
        {
            SystemPromptFragment = SeepReverseLabAssets.SystemPromptFragment,
        },

        // ── 对话增强插件（随包分发并默认启用） ──
        new("chat.interactive-ui", "交互增强", PluginCategory.ToolExtension,
            "把模型输出的 ```ui 围栏渲染成原生交互组件：滑块、下拉、开关、多选、输入、评分、进度与提交按钮。",
            "1.0.0", "Fufucode Team", ["读取消息正文", "把汇总文本作为用户消息提交"],
            DefaultInstalled: true, DefaultEnabled: true),

        // ── 工作区页面插件（默认未安装，按需从市场安装） ──
        new("workspace.browser", "内置浏览器", PluginCategory.ToolExtension,
            "在右栏打开内置 WebView2 浏览器：导航、下载、站点权限与 Agent 页面控制。默认不安装。",
            "1.0.0", "Fufucode Team", ["WebView2 运行时", "网页导航与本地文件预览", "可选 CDP 控制"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("workspace.library", "资料库", PluginCategory.ToolExtension,
            "资料条目、来源回溯与聊天引用；安装后出现在工作区页面中。",
            "1.0.0", "Fufucode Team", ["本地资料存储", "引用复制到聊天"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("workspace.local-review", "本地审查", PluginCategory.ToolExtension,
            "基于 Git 差异的本地只读审查、发现项与导出；安装后出现在工作区页面中。",
            "1.0.0", "Fufucode Team", ["读取项目 Git 差异", "调用审查执行器"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("workspace.terminal", "交互终端", PluginCategory.ToolExtension,
            "项目级交互式终端：ConPTY 原生 shell、备用屏与全屏 TUI、滚动区域、查找、选中复制、IME 与命令历史。",
            "2.0.0", "Fufucode Team", ["启动本机 shell 进程", "读写项目工作目录", "系统剪贴板"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("sandbox.nvx", "NVX 微虚拟机沙箱", PluginCategory.AgentCapability,
            "通过微软 NVX/OpenVMM 在 Windows WHP 微虚拟机中执行不受信任工作负载。默认不安装，不改变现有宿主命令执行路径。",
            "0.1.0", "Microsoft / Fufucode", ["Windows Hypervisor Platform", "NVX 运行时载荷", "独立沙箱工作目录"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("performance.monitor", "性能监控", PluginCategory.ToolExtension,
            "逐回合追踪 harness 每一次模型请求、工具与命令执行、审批等待、上下文压缩、会话持久化与界面线程卡顿，" +
            "提供瀑布图、慢调用、异常规则与脱敏诊断包导出。未启用时埋点开销仅为一次空判断。",
            "1.0.0", "Fufucode Team",
            ["读取进程 CPU/内存/线程/GC 采样", "写入应用数据目录下的性能记录（默认脱敏）", "导出诊断包到数据目录"],
            // 默认安装并启用（用户 2026-10-20 明确）：可在插件市场随时停用或卸载。
            DefaultInstalled: true, DefaultEnabled: true),

        // ── 后台服务插件 ──
        new("service.remote-control", "手机远程控制服务", PluginCategory.Service,
            "提供局域网配对与远程会话管理服务，支持 WorkBuddy 手机客户端通过动态配对码连接操控桌面 Agent。",
            "1.0.0", "Fufucode Team", ["局域网网络监听", "会话控制与消息收发"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("service.fufu-bridge", "FUFU 桌宠桥接", PluginCategory.Service,
            "通过本机命名管道连接桌面宠物 FUFU：上报会话目录与 Agent 状态、转发工具审批，并可经 FUFU 授权向选中会话提交任务。",
            "1.0.0", "Fufucode Team", ["本机进程间通信", "会话控制与消息收发"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("service.speech-synthesis", "语音朗读服务", PluginCategory.Service,
            "通过系统语音引擎为助手回复提供 TTS 语音朗读播放能力。",
            "1.0.0", "Fufucode Team", ["音频输出", "系统语音合成组件"],
            DefaultInstalled: false, DefaultEnabled: false),

        // ── 自动化与调度插件 ──
        new("automation.auto-dev", "自动开发工作台", PluginCategory.Automation,
            "多 Agent 自动化开发流水线、DAG 依赖规划、隔离工作树调度与交付卡自动对账审查闭环。",
            "1.0.0", "Fufucode Team", ["多 Agent 派单调度", "Git 工作树生命周期", "自动化验收测试"],
            DefaultInstalled: false, DefaultEnabled: false),

        new("automation.cron", "用户定时任务", PluginCategory.Automation,
            "定时与周期性执行用户自定义任务，支持 Heartbeat、Cron 与后台状态监听。",
            "1.0.0", "Fufucode Team", ["后台计时调度器"],
            DefaultInstalled: false, DefaultEnabled: false),

        // ── 皮肤与主题插件 ──
        new("skin.aero", "Windows Aero 玻璃皮肤", PluginCategory.Theme,
            "Windows Vista / 7 经典毛玻璃质感皮肤包，含光泽按钮、半透明边框与拟物窗口装饰。",
            "1.0.0", "Fufucode Community", ["窗口亚克力/毛玻璃混合特效"],
            DefaultInstalled: false, DefaultEnabled: false),
        new("skin.metro", "Metro 皮肤", PluginCategory.Theme,
            "Windows 8 风格直角平面色块与整块强调色选择态，固定 Metro 调色板。",
            "1.0.0", "Fufucode Community", ["主题资源与控件样式覆盖"],
            DefaultInstalled: false, DefaultEnabled: false),
        new("skin.material3", "Material 3 皮肤", PluginCategory.Theme,
            "Material 3 胶囊按钮、色调分层容器与状态层叠加，固定 M3 调色板。",
            "1.0.0", "Fufucode Community", ["主题资源与控件样式覆盖"],
            DefaultInstalled: false, DefaultEnabled: false),
        new("skin.luna", "Windows XP Luna 皮肤", PluginCategory.Theme,
            "Windows XP Luna Blue 经典圆润控件与高对比蓝色标题栏。",
            "1.0.0", "Fufucode Community", ["主题资源与控件样式覆盖"],
            DefaultInstalled: false, DefaultEnabled: false),
        new("skin.liquidglass", "Liquid Glass 皮肤", PluginCategory.Theme,
            "iOS 26 液态玻璃：SKSL 自绘折射、色散与边缘高光，跟随系统强调色。",
            "1.0.0", "Fufucode Community", ["Skia GPU 着色器渲染"],
            DefaultInstalled: false, DefaultEnabled: false),
    ];

    public static PluginDefinition? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 进程内插件状态变更通知（跨 <see cref="PluginStateStore"/> 实例）。
///
/// 背景：设置页与组合根各自持有独立的 <see cref="PluginStateStore"/> 实例（同一 plugins.json）。
/// 设置页写入后，组合根持有的实例内存快照不会自动更新，插件工具门禁就会读到过期状态。
/// 本通知让任何一次真实写入广播给全部订阅方，订阅方随后调用
/// <see cref="PluginStateStore.Reload"/> 重新读数，避免轮询文件或使用文件监视器。
/// </summary>
public static class PluginStateNotifier
{
    /// <summary>任一 PluginStateStore 发生真实状态写入时触发；订阅方需自行重新读取状态。</summary>
    public static event Action? Changed;

    /// <summary>广播一次状态变更；订阅方异常不得影响写入方。</summary>
    public static void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("PluginStateNotifier subscriber: {0}", error); }
        }
    }
}

/// <summary>
/// 插件市场状态存储（原子落盘至 &lt;dataRoot&gt;/plugins.json）。
/// </summary>
public sealed class PluginStateStore
{
    private readonly string _path;
    private readonly string _pluginsDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginState> _states = new(StringComparer.OrdinalIgnoreCase);

    public PluginStateStore(AppDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.EnsureCreated();
        _path = Path.Combine(layout.Root, "plugins.json");
        _pluginsDirectory = layout.PluginsDirectory;
        Load();
    }

    /// <summary>
    /// 目录条目是否真有载荷。核心内置与内嵌插件不需要 plugins/&lt;id&gt;/manifest.json。
    /// </summary>
    private bool HasPayload(PluginDefinition def) =>
        def.IsCoreBuiltin || def.IsEmbedded ||
        File.Exists(Path.Combine(_pluginsDirectory, def.Id, "manifest.json"));

    public event Action? Changed;

    /// <summary>
    /// 重新从 plugins.json 读取状态；与内存快照不同时替换并触发 <see cref="Changed"/>。
    /// 返回是否真的发生了变化。用于跨实例（例如设置页写入后组合根刷新）读取最新真实状态。
    /// </summary>
    public bool Reload()
    {
        Dictionary<string, PluginState> loaded;
        lock (_gate)
        {
            loaded = ReadFromDiskLocked();
            if (SameAs(loaded)) return false;
            _states.Clear();
            foreach (var (key, value) in loaded) _states[key] = value;
        }
        Changed?.Invoke();
        return true;
    }

    private bool SameAs(Dictionary<string, PluginState> other)
    {
        if (other.Count != _states.Count) return false;
        foreach (var (key, value) in other)
        {
            if (!_states.TryGetValue(key, out var current)) return false;
            if (current.IsInstalled != value.IsInstalled || current.IsEnabled != value.IsEnabled) return false;
        }
        return true;
    }

    private Dictionary<string, PluginState> ReadFromDiskLocked()
    {
        var loaded = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return loaded;
        try
        {
            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize(json, PluginConfigJsonContext.Default.PluginConfiguration);
            if (config?.States is not null)
            {
                foreach (var (key, value) in config.States) loaded[key] = value;
            }
        }
        catch { }
        return loaded;
    }

    /// <summary>
    /// 获取指定插件的生效状态。已安装但没有真实载荷的条目一律报告为未安装，
    /// 避免旧 plugins.json 或目录默认值制造“已安装但不可用”的假状态。
    /// </summary>
    /// <summary>
    /// 读取磁盘上“显式保存过”的状态；没有条目时返回 false。
    /// 用于区分“从未安装（走目录默认值）”与“用户明确卸载”，随包默认插件据此决定是否重新部署载荷。
    /// </summary>
    public bool TryGetStoredState(string pluginId, out PluginState state)
    {
        lock (_gate) return _states.TryGetValue(pluginId, out state!);
    }

    public PluginState GetState(string pluginId)
    {
        lock (_gate)
        {
            var def = PluginMarketplaceCatalog.Find(pluginId);
            if (_states.TryGetValue(pluginId, out var state))
            {
                if (def is not null && state.IsInstalled && !HasPayload(def))
                    return state with { IsInstalled = false, IsEnabled = false };
                return state;
            }
            if (def is null) return new PluginState(pluginId, false, false, DateTime.UtcNow);
            var installed = def.DefaultInstalled && HasPayload(def);
            return new PluginState(def.Id, installed, installed && def.DefaultEnabled, DateTime.UtcNow);
        }
    }

    /// <summary>插件是否处于“已安装且已启用”的可用态；未安装或已停用均为 false。</summary>
    /// <summary>用户是否对该插件做过显式选择（安装/启用状态曾落盘）。</summary>
    public bool HasExplicitState(string pluginId)
    {
        lock (_gate) return _states.ContainsKey(pluginId);
    }

    public bool IsActive(string pluginId)
    {
        var state = GetState(pluginId);
        return state.IsInstalled && state.IsEnabled;
    }

    internal void RestoreState(PluginState state) => WriteState(state.Id, _ => state);

    /// <summary>查询全部插件的状态快照。</summary>
    public IReadOnlyList<PluginState> GetAllStates()
    {
        lock (_gate)
        {
            return PluginMarketplaceCatalog.All.Select(def => GetState(def.Id)).ToArray();
        }
    }

    /// <summary>安装插件。</summary>
    public void Install(string pluginId)
    {
        WriteState(pluginId, static def => new PluginState(def.Id, true, true, DateTime.UtcNow), validate: null);
    }

    /// <summary>卸载插件（核心内置组件不可卸载）。</summary>
    public void Uninstall(string pluginId)
    {
        WriteState(pluginId, static def => new PluginState(def.Id, false, false, DateTime.UtcNow),
            validate: def =>
            {
                if (def.IsCoreBuiltin)
                    throw new InvalidOperationException($"核心内置组件 '{def.Name}' 不可卸载。");
            });
    }

    /// <summary>启用或停用插件。</summary>
    public void SetEnabled(string pluginId, bool enabled)
    {
        WriteState(pluginId, def => GetState(def.Id) with { IsEnabled = enabled, LastUpdatedUtc = DateTime.UtcNow },
            validate: def =>
            {
                if (!GetState(def.Id).IsInstalled)
                    throw new InvalidOperationException($"插件 '{def.Name}' 尚未安装，无法设置启用状态。");
                if (def.IsCoreBuiltin && !enabled)
                    throw new InvalidOperationException($"核心内置组件 '{def.Name}' 不可禁用。");
            });
    }

    /// <summary>
    /// 统一写入路径：先做门禁校验（<paramref name="validate"/>），再落盘并广播。
    /// 所有写入都经此路径，保证 <see cref="Changed"/> 与 <see cref="PluginStateNotifier"/> 不漏发。
    /// </summary>
    private void WriteState(string pluginId, Func<PluginDefinition, PluginState> next,
        Action<PluginDefinition>? validate = null)
    {
        lock (_gate)
        {
            var def = PluginMarketplaceCatalog.Find(pluginId)
                ?? throw new ArgumentException($"未知的插件 ID '{pluginId}'。", nameof(pluginId));
            validate?.Invoke(def);
            var hadPrevious = _states.TryGetValue(def.Id, out var previous);
            _states[def.Id] = next(def);
            try { SaveLocked(); }
            catch
            {
                if (hadPrevious) _states[def.Id] = previous!;
                else _states.Remove(def.Id);
                throw;
            }
        }
        Changed?.Invoke();
        // 广播给其它 PluginStateStore 实例的持有方（例如组合根的工具门禁）。
        PluginStateNotifier.Notify();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize(json, PluginConfigJsonContext.Default.PluginConfiguration);
            if (config?.States is not null)
            {
                foreach (var (k, v) in config.States)
                {
                    _states[k] = v;
                }
            }
        }
        catch { }
    }

    private void SaveLocked()
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var config = new PluginConfiguration(1, new Dictionary<string, PluginState>(_states, StringComparer.OrdinalIgnoreCase));
            var json = JsonSerializer.Serialize(config, PluginConfigJsonContext.Default.PluginConfiguration);
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
