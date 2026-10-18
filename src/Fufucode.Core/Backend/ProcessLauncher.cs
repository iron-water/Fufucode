using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Fufucode.Core.Backend;

/// <summary>
/// 跨平台进程启动器：规范化 Windows 平台对 .cmd / .bat（需通过 comspec /c 启动）
/// 以及测试夹具 .dll（通过 dotnet 宿主执行）的调用，保证子进程平稳启动。
/// </summary>
public static class ProcessLauncher
{
    public static ProcessStartInfo Create(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var dotnet = ResolveDotnetHost();
            startInfo.FileName = dotnet;
            startInfo.ArgumentList.Add(executable);
            foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
            return startInfo;
        }

        var isCmd = OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        if (isCmd)
        {
            var targetJs = TryFindNpmTargetJs(executable);
            if (targetJs is not null)
            {
                startInfo.FileName = "node";
                startInfo.ArgumentList.Add(targetJs);
                foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
                return startInfo;
            }

            var comspec = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.FileName = comspec;
            var quotedArgs = arguments.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"");
            startInfo.Arguments = $"/c \"\"{executable}\" {string.Join(" ", quotedArgs)}\"";
            return startInfo;
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string? TryFindNpmTargetJs(string cmdPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(cmdPath);
            if (dir is null) return null;
            var content = File.ReadAllText(cmdPath);
            var marker = "%dp0%\\";
            var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var start = idx + marker.Length;
                var end = content.IndexOf('"', start);
                if (end > start)
                {
                    var relative = content[start..end];
                    if (relative.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = Path.Combine(dir, relative);
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                }
                idx = content.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return null;
    }

    private static string ResolveDotnetHost()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(pf, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(standard)) return standard;
        return "dotnet";
    }
}
