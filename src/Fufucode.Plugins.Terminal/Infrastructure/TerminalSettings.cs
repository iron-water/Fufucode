using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.Plugins.Terminal.Infrastructure;

/// <summary>
/// 终端插件的持久化设置。由插件自己读写，宿主不感知具体字段；
/// 未知/损坏值一律回退到默认值，绝不把非法状态写回。
/// </summary>
public sealed record TerminalSettings
{
    public const int CurrentVersion = 1;

    /// <summary>shell 配置：<see cref="ShellProfiles"/> 中的 id。</summary>
    public string ShellProfile { get; init; } = "cmd";
    /// <summary>启动命令覆盖（可选）；为空时使用配置的默认命令。</summary>
    public string CommandOverride { get; init; } = "";
    /// <summary>字号（DIP）。</summary>
    public double FontSize { get; init; } = 13;
    /// <summary>回滚行数上限。</summary>
    public int ScrollbackLines { get; init; } = 1500;
    /// <summary>响铃方式：none / visual / beep。</summary>
    public string Bell { get; init; } = "visual";
    /// <summary>光标是否闪烁。</summary>
    public bool BlinkCursor { get; init; } = true;
    /// <summary>Ctrl+Click 打开 OSC 8 链接。</summary>
    public bool OpenLinks { get; init; } = true;
    /// <summary>关闭运行中的终端前确认。</summary>
    public bool ConfirmClose { get; init; } = true;

    public static readonly string[] BellModes = { "none", "visual", "beep" };

    public static TerminalSettings Default => new();

    public TerminalSettings Normalize() => this with
    {
        ShellProfile = ShellProfiles.Find(ShellProfile)?.Id ?? "cmd",
        CommandOverride = (CommandOverride ?? string.Empty).Trim(),
        FontSize = Math.Clamp(double.IsFinite(FontSize) ? FontSize : 13, 9, 28),
        ScrollbackLines = Math.Clamp(ScrollbackLines, 0, 100_000),
        Bell = BellModes.Contains(Bell, StringComparer.OrdinalIgnoreCase) ? Bell.ToLowerInvariant() : "visual",
        OpenLinks = OpenLinks,
        ConfirmClose = ConfirmClose,
    };
}

/// <summary>一个可选的 shell 配置；命令在 <see cref="Command"/> 为空时按探测结果回退。</summary>
public sealed record ShellProfile(string Id, string DisplayName, string FileName, string Arguments, string? ProbePath = null)
{
    public string ResolveExecutable() => ProbePath ?? FileName;
}

/// <summary>内置 shell 列表。只列真实存在的可执行文件；未找到时由调用方显式报告不可用。</summary>
public static class ShellProfiles
{
    public static readonly IReadOnlyList<ShellProfile> All =
    [
        new("cmd", "命令提示符 (cmd.exe)", "cmd.exe", "/Q /K chcp 65001 >nul"),
        new("powershell", "Windows PowerShell", "powershell.exe", "-NoLogo -NoProfile -NoExit -Command \"$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8\""),
        new("pwsh", "PowerShell 7 (pwsh)", "pwsh.exe", "-NoLogo -NoProfile -NoExit"),
        new("git-bash", "Git Bash", @"C:\Program Files\Git\bin\bash.exe", "--login -i"),
    ];

    public static ShellProfile? Find(string? id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>该配置的可执行文件绝对路径；不存在时返回 null。</summary>
    public static string? Resolve(ShellProfile profile)
    {
        if (profile.Id == "git-bash")
        {
            foreach (var candidate in new[]
                     {
                         Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Git\bin\bash.exe"),
                         Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Git\bin\bash.exe"),
                         Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Git\bin\bash.exe"),
                     })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
        if (Path.IsPathRooted(profile.FileName))
            return File.Exists(profile.FileName) ? profile.FileName : null;
        var system = Path.Combine(Environment.SystemDirectory, profile.FileName);
        if (File.Exists(system)) return system;
        // PowerShell/pwsh 也可能在 PATH 上；不做 shell 展开，只查已知目录。
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "PowerShell", "7", profile.FileName);
            if (profile.Id == "pwsh" && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>构造完整的命令行；引号由调用方按 CreateProcess 规则处理。</summary>
    public static string BuildCommandLine(ShellProfile profile, string? executable, string? overrideCommand)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand)) return overrideCommand.Trim();
        var path = executable ?? profile.FileName;
        return $"\"{path}\" {profile.Arguments}".TrimEnd();
    }
}

/// <summary>终端设置文件的原子读写（与宿主设置存储互不依赖）。</summary>
public sealed class TerminalSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TerminalSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Path = System.IO.Path.Combine(dataRoot, "terminal-settings.json");
    }

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public TerminalSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return TerminalSettings.Default;
            var settings = JsonSerializer.Deserialize<TerminalSettings>(File.ReadAllText(Path), Json);
            return (settings ?? TerminalSettings.Default).Normalize();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return TerminalSettings.Default;
        }
    }

    public void Save(TerminalSettings settings)
    {
        var normalized = settings.Normalize();
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, Json));
        File.Move(temporary, Path, overwrite: true);
    }
}
