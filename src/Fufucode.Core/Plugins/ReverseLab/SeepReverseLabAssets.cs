using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Fufucode.Core.Plugins;

/// <summary>
/// Seep 逆向工程工作台的外部资产解析（唯一真源）。
///
/// 本类只做路径解析与只读读取，不复制、不改写 <c>C:\Tool\Reverse\Seep-Reverse-Lab</c>
/// 下的任何工作台源文件；未部署工作台时如实返回“不可用”，不伪造路径存在。
/// </summary>
public static class SeepReverseLabAssets
{
    /// <summary>插件 ID（与 <see cref="PluginMarketplaceCatalog"/> 一致）。</summary>
    public const string PluginId = "tool.seep-reverse-lab";

    /// <summary>工作台默认部署根目录。</summary>
    public const string DefaultLabRoot = @"C:\Tool\Reverse\Seep-Reverse-Lab";

    /// <summary>覆盖工作台根目录的环境变量名。</summary>
    public const string LabRootEnvironmentVariable = "SEEP_REVERSE_LAB_ROOT";

    /// <summary>覆盖 Python 解释器的环境变量名。</summary>
    public const string PythonEnvironmentVariable = "SEEP_PYTHON";

    /// <summary>MCP 服务脚本相对工作台根目录的路径。</summary>
    public const string McpServerRelativePath = @"Tool\mcp\seep_mcp_server.py";

    /// <summary>合规转译中继脚本相对工作台根目录的路径。</summary>
    public const string RelayScriptRelativePath = @"Tool\scripts\compliance-relay\relay.py";

    /// <summary>合规转译中继默认监听端口。</summary>
    public const int RelayPort = 17890;

    /// <summary>seep MCP 服务实际导出的工具数量（2026-10-16 实测 tools/list = 23）。</summary>
    public const int ExpectedToolCount = 23;

    /// <summary>打包时随插件内嵌的作战协议人格资源文件名。</summary>
    private const string PersonaAssetFileName = "seep-combat-protocol.md";

    /// <summary>
    /// 打包进本程序集的作战协议人格原文（<c>~/.pi/agent/AGENTS.md</c> 的逐字副本）。
    /// 仅在插件“已安装且已启用”时，由 <see cref="PluginSystemPromptComposer"/> 注入会话 System 指令。
    /// </summary>
    public static string SystemPromptFragment => PersonaHolder.Value.Text;

    /// <summary>内嵌人格资源是否成功加载（false 表示打包缺资源，注入会跳过并如实记录）。</summary>
    public static bool IsPersonaAvailable => PersonaHolder.Value.Text.Length > 0;

    /// <summary>内嵌人格资源来源标识（资源名或失败原因），供交付记录与诊断使用。</summary>
    public static string PersonaSource => PersonaHolder.Value.Source;

    private static readonly Lazy<(string Text, string Source)> PersonaHolder =
        new(LoadPersona, isThreadSafe: true);

    private static (string Text, string Source) LoadPersona()
    {
        try
        {
            var assembly = typeof(SeepReverseLabAssets).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(PersonaAssetFileName, StringComparison.Ordinal));
            if (name is null)
                return (string.Empty, "未找到内嵌资源 " + PersonaAssetFileName);

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return (string.Empty, "内嵌资源不可读：" + name);

            using var reader = new StreamReader(stream);
            return (reader.ReadToEnd(), name);
        }
        catch (Exception error)
        {
            return (string.Empty, "读取内嵌人格失败：" + error.GetType().Name);
        }
    }

    /// <summary>
    /// 解析工作台根目录：环境变量优先，其次默认路径；目录不存在时仍返回候选路径，
    /// 由 <see cref="Probe"/> 如实报告可用性，不在解析阶段抛异常。
    /// </summary>
    public static string ResolveLabRoot(string? configuredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot)) return configuredRoot!;

        var fromEnvironment = Environment.GetEnvironmentVariable(LabRootEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultLabRoot : fromEnvironment!;
    }

    /// <summary>解析 Python 解释器：环境变量优先，其次 PATH 上的 <c>python</c>。</summary>
    public static string ResolvePythonExecutable()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(PythonEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? "python" : fromEnvironment!;
    }

    /// <summary>工作台资产可用性快照：每一项都来自真实文件系统探测。</summary>
    public static SeepReverseLabProbe Probe(string? configuredRoot = null)
    {
        var root = ResolveLabRoot(configuredRoot);
        var mcp = Path.Combine(root, McpServerRelativePath);
        var relay = Path.Combine(root, RelayScriptRelativePath);
        return new SeepReverseLabProbe(
            root,
            Directory.Exists(root),
            File.Exists(mcp),
            File.Exists(relay),
            mcp,
            relay);
    }

    /// <summary>构造 MCP 子进程启动参数（绝对脚本路径 + 工作台根目录作为工作目录）。</summary>
    public static SeepProcessSpec CreateMcpProcessSpec(string? configuredRoot = null)
    {
        var root = ResolveLabRoot(configuredRoot);
        var spec = new SeepProcessSpec(
            ResolvePythonExecutable(),
            new[] { Path.Combine(root, McpServerRelativePath) },
            root,
            SeepProcessEnvironment());
        return spec;
    }

    /// <summary>构造合规转译中继子进程启动参数。</summary>
    public static SeepProcessSpec CreateRelayProcessSpec(int port = RelayPort, string? configuredRoot = null)
    {
        var root = ResolveLabRoot(configuredRoot);
        return new SeepProcessSpec(
            ResolvePythonExecutable(),
            new[] { Path.Combine(root, RelayScriptRelativePath), "--port", port.ToString() },
            Path.GetDirectoryName(Path.Combine(root, RelayScriptRelativePath)) ?? root,
            SeepProcessEnvironment());
    }

    /// <summary>子进程统一的编码环境：强制 UTF-8，避免中文工具输出在管道里被本地代码页破坏。</summary>
    private static IReadOnlyDictionary<string, string> SeepProcessEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
        };
}

/// <summary>工作台资产探测结果；字段全部来自真实文件系统检查。</summary>
public sealed record SeepReverseLabProbe(
    string LabRoot,
    bool LabRootExists,
    bool McpServerExists,
    bool RelayScriptExists,
    string McpServerPath,
    string RelayScriptPath)
{
    /// <summary>MCP 工具链是否可用（脚本真实存在）。</summary>
    public bool McpAvailable => LabRootExists && McpServerExists;

    /// <summary>合规转译中继是否可用（脚本真实存在）。</summary>
    public bool RelayAvailable => LabRootExists && RelayScriptExists;

    /// <summary>人类可读的探测摘要；不可用时给出具体缺项，不泛化失败。</summary>
    public string Describe()
    {
        if (!LabRootExists) return $"未找到 Seep 工作台根目录：{LabRoot}";
        if (!McpServerExists) return $"未找到 seep MCP 服务脚本：{McpServerPath}";
        return RelayScriptExists
            ? "Seep 工作台资产完整。"
            : $"Seep 工作台已就位，但未找到合规转译中继脚本：{RelayScriptPath}";
    }
}

/// <summary>启动一个本地子进程所需的参数。</summary>
public sealed record SeepProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// 本地子进程接缝。真实实现走 <see cref="System.Diagnostics.Process"/>；
/// 定向测试注入脚本化替身，不需要真实 Python 运行时。
/// </summary>
public interface ISeepProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>真实操作系统进程 ID；替身或尚未启动时为 null。</summary>
    int? ProcessId { get; }
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }

    /// <summary>终止子进程；已退出时是幂等空操作。</summary>
    void Kill();
}

/// <summary>子进程工厂接缝；唯一职责是把 <see cref="SeepProcessSpec"/> 变成运行中的进程。</summary>
public interface ISeepProcessFactory
{
    ISeepProcess Start(SeepProcessSpec spec);
}

/// <summary>
/// 真实子进程工厂：重定向 stdin/stdout/stderr，UTF-8 无 BOM，
/// 并把 stderr 异步排空（避免管道写满阻塞子进程），保留最近片段供诊断。
/// </summary>
public sealed class SeepProcessFactory : ISeepProcessFactory
{
    public ISeepProcess Start(SeepProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        if (spec.Environment is { } environment)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动子进程：{spec.FileName}");
        return new SeepProcess(process);
    }

    private sealed class SeepProcess : ISeepProcess
    {
        private readonly System.Diagnostics.Process _process;
        private readonly System.Text.StringBuilder _stderr = new();
        private readonly object _gate = new();
        private readonly Task _stderrPump;

        public SeepProcess(System.Diagnostics.Process process)
        {
            _process = process;
            StandardInput = process.StandardInput;
            StandardOutput = process.StandardOutput;
            StandardError = process.StandardError;
            _stderrPump = Task.Run(PumpStderrAsync);
        }

        public bool HasExited => _process.HasExited;
        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
        public int? ProcessId
        {
            get
            {
                try { return _process.Id; }
                catch (InvalidOperationException) { return null; }
            }
        }
        public TextWriter StandardInput { get; }
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }

        /// <summary>最近 4KB stderr；只用于诊断，不参与协议判定。</summary>
        public string StderrTail
        {
            get { lock (_gate) return _stderr.ToString(); }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Kill();
            try { _stderrPump.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _process.Dispose();
        }

        private async Task PumpStderrAsync()
        {
            var buffer = new char[1024];
            try
            {
                while (true)
                {
                    var read = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) break;
                    lock (_gate)
                    {
                        _stderr.Append(buffer, 0, read);
                        if (_stderr.Length > 4096) _stderr.Remove(0, _stderr.Length - 4096);
                    }
                }
            }
            catch (Exception)
            {
                // 进程退出时读取管道会中断，属正常收尾，不作为协议错误上报。
            }
        }
    }
}
