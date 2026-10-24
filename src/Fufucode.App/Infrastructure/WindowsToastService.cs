using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Fufucode.App.Infrastructure;

internal sealed record WindowsToastResult(bool Delivered, string Status);

/// <summary>系统通知上的一个按钮：点击后以协议激活回本应用，参数即 <see cref="Argument"/>。</summary>
internal sealed record WindowsToastAction(string Label, string Argument);

internal static class WindowsToastService
{
    internal const string AppId = "Fufucode.Desktop";
    private const string ProtocolKey = @"Software\Classes\" + Integration.ApprovalLink.Scheme;
    internal const string ProtocolCommandKey = ProtocolKey + @"\shell\open\command";

    internal static string CreateXml(string title, string message, string? link,
        IReadOnlyList<WindowsToastAction>? actions = null)
    {
        var toast = new XElement("toast", new XElement("visual",
            new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", title), new XElement("text", message))));
        if (link is not null)
        {
            toast.Add(new XAttribute("activationType", "protocol"));
            toast.Add(new XAttribute("launch", link));
        }
        // 按钮只走协议激活：不需要 COM 激活器，适用未打包的桌面应用。
        var buttons = (actions ?? []).Where(action => !string.IsNullOrWhiteSpace(action.Label)).Take(5).ToArray();
        if (buttons.Length > 0)
        {
            toast.Add(new XElement("actions", buttons.Select(action => new XElement("action",
                new XAttribute("content", action.Label),
                new XAttribute("activationType", "protocol"),
                new XAttribute("arguments", action.Argument)))));
        }
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// 让审批 scheme（<c>fufucode-approval://</c>）指向当前可执行文件，否则通知按钮会唤醒另一个（可能是旧版）实例。
    /// 已指向当前程序时不写注册表；写入失败不阻止通知投递。
    /// </summary>
    internal static void EnsureProtocolRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var expected = $"\"{executable}\" \"%1\"";
            using (var root = Registry.CurrentUser.CreateSubKey(ProtocolKey))
                root.SetValue("URL Protocol", string.Empty);
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolCommandKey);
            if (key.GetValue(null) as string == expected) return;
            key.SetValue(null, expected);
        }
        catch (Exception)
        {
            // 权限/注册表不可用：保留原有注册，按钮可能落到其它实例；不影响应用内通知。
        }
    }

    internal static async Task<WindowsToastResult> SendAsync(string key, string title, string message, string? link = null,
        IReadOnlyList<WindowsToastAction>? actions = null)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "此系统不支持 Windows 通知，已回退应用内提醒");
        try
        {
            using var registration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            registration.SetValue("DisplayName", "Fufucode");
            var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var script = """
                $ErrorActionPreference='Stop'
                [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
                [Windows.UI.Notifications.ToastNotifier, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
                [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
                [Windows.UI.Notifications.NotificationSetting, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
                [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] > $null
                $xml=[Windows.Data.Xml.Dom.XmlDocument]::new()
                $xml.LoadXml([Console]::In.ReadToEnd())
                $notifier=[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Fufucode.Desktop')
                if ($null -ne $notifier.Setting -and [int]$notifier.Setting -ne 0) { throw ('Notification setting: '+$notifier.Setting) }
                $toast=[Windows.UI.Notifications.ToastNotification]::new($xml)
                $toast.Tag=$env:FUFUCODE_TOAST_TAG
                $toast.Group='session'
                $notifier.Show($toast)
                Start-Sleep -Milliseconds 300
                $history=[Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory('Fufucode.Desktop')
                if (-not @($history | Where-Object { $_.Tag -eq $toast.Tag }).Count) { throw 'Toast absent from Windows notification history' }
                [Console]::Out.Write('accepted-in-windows-history')
                """;
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            script = "try {\n" + script + "\n} catch { [Console]::Error.Write($_.Exception.Message); exit 1 }";
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            start.Environment["FUFUCODE_TOAST_TAG"] = tag;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("通知进程启动失败");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(CreateXml(title, message, link, actions));
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            if (process.ExitCode != 0 || await output != "accepted-in-windows-history")
                return new(false, "系统通知失败，已回退应用内提醒：" + (await error).Trim());
            return new(true, "Windows 已接收通知；横幅显示仍受勿扰设置控制");
        }
        catch (Exception error) { return new(false, "系统通知失败，已回退应用内提醒：" + error.GetBaseException().Message); }
    }
}
