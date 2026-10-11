using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Fufucode.App.Infrastructure;

internal sealed record WindowsToastResult(bool Delivered, string Status);

internal static class WindowsToastService
{
    internal const string AppId = "Fufucode.Desktop";
    internal static string CreateXml(string title, string message, string? link)
    {
        var toast = new XElement("toast", new XElement("visual",
            new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", title), new XElement("text", message))));
        if (link is not null)
        {
            toast.Add(new XAttribute("activationType", "protocol"));
            toast.Add(new XAttribute("launch", link));
        }
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    internal static async Task<WindowsToastResult> SendAsync(string key, string title, string message, string? link = null)
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
            await process.StandardInput.WriteAsync(CreateXml(title, message, link));
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
