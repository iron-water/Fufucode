using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// “复制诊断摘要”的纯文本构建器。约束：绝不包含 apiKey 值、环境变量值或凭据；
/// models.json 里的密钥经 <see cref="PiModelsConfig"/> 脱敏描述。
/// </summary>
public static class DiagnosticsSummary
{
    public sealed record Input(
        string ProjectPath,
        string ModeText,
        string? ApprovalText,
        string? ApprovalDiagnostic,
        IReadOnlyList<string> ActiveExtensionPaths,
        string? LastStartupFailure,
        string? AgentDirectory,
        string? DataRoot,
        string? LogsDirectory,
        AppSettings Settings,
        string? SettingsLoadError,
        string PiVersion,
        string NodeVersion);

    public static string Build(Input input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fufucode 诊断摘要");
        sb.AppendLine($"时间: {System.DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"应用版本: {AppVersion()}");
        sb.AppendLine($"运行时: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Avalonia: {AvaloniaVersion()}");
        sb.AppendLine($"Pi: {input.PiVersion}");
        sb.AppendLine($"Node: {input.NodeVersion}");
        sb.AppendLine($"模式: {input.ModeText}");
        sb.AppendLine($"项目: {input.ProjectPath}");
        sb.AppendLine($"数据目录: {input.DataRoot ?? "不可用"}");
        sb.AppendLine($"代理配置目录: {input.AgentDirectory ?? "不可用"}");
        sb.AppendLine($"日志目录: {input.LogsDirectory ?? "不可用"}");
        sb.AppendLine($"审批桥: {input.ApprovalText ?? "未知"}" +
                      (input.ApprovalDiagnostic is { } d ? $"（{d}）" : string.Empty));
        sb.AppendLine($"已加载扩展: {(input.ActiveExtensionPaths.Count == 0
            ? "（无）" : string.Join("; ", input.ActiveExtensionPaths))}");
        sb.AppendLine($"最近启动失败: {input.LastStartupFailure ?? "（无）"}");
        sb.AppendLine($"设置: theme={input.Settings.Theme}, proxy={(input.Settings.ProxyUrl is null ? "未设置" : "已设置")}, " +
                      $"endpoint={(input.Settings.Endpoint?.ProviderName ?? "未设置")}");
        if (input.SettingsLoadError is { } loadError)
        {
            sb.AppendLine($"settings.json 加载错误: {loadError}");
        }

        if (input.AgentDirectory is { } agentDir)
        {
            var models = PiModelsConfig.Read(agentDir);
            if (!models.FileExists)
            {
                sb.AppendLine("models.json: 不存在");
            }
            else if (models.Error is { } error)
            {
                sb.AppendLine("models.json: " + error);
            }
            else
            {
                foreach (var provider in models.Providers)
                {
                    sb.AppendLine($"provider {provider.Name}: baseUrl={provider.BaseUrl ?? "?"}, " +
                                  $"api={provider.Api ?? "?"}, models=[{string.Join(", ", provider.ModelIds)}], " +
                                  $"key={provider.KeyText}");
                }
            }
        }

        return sb.ToString();
    }

    internal static string AppVersion()
    {
        var assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "未知";
    }

    internal static string AvaloniaVersion() =>
        typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "未知";

    /// <summary>从 Pi cli.js 向上找 @earendil-works/pi-coding-agent/package.json 读 version。</summary>
    internal static string ResolvePiVersion(string? cliPath)
    {
        try
        {
            if (cliPath is null)
            {
                return "不可用";
            }

            var directory = new FileInfo(cliPath).Directory;
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "package.json");
                if (File.Exists(candidate) &&
                    directory.Name == "pi-coding-agent")
                {
                    using var document = System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(candidate));
                    return document.RootElement.TryGetProperty("version", out var v)
                        ? v.GetString() ?? "未知"
                        : "未知";
                }

                directory = directory.Parent;
            }

            return "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    /// <summary>读 node.exe 的 ProductVersion（不启动进程）。</summary>
    internal static string ResolveNodeVersion(string? nodePath)
    {
        try
        {
            if (nodePath is null || !File.Exists(nodePath))
            {
                return "不可用";
            }

            return FileVersionInfo.GetVersionInfo(nodePath).ProductVersion ?? "未知";
        }
        catch
        {
            return "不可用";
        }
    }
}
