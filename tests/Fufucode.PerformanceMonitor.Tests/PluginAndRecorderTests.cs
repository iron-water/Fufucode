using System.Diagnostics;
using System.Text;
using Fufucode.Core.Backend;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Plugins.PerformanceMonitor;
using static Fufucode.PerformanceMonitor.Tests.TestUtil;

namespace Fufucode.PerformanceMonitor.Tests;

internal static class RedactorTests
{
    public static void Run()
    {
        Check(!Redactor.Text("curl -H 'Authorization: Bearer sk-abcdef1234567890abcd' https://api.example.com")
            .Contains("abcdef1234567890"), "bearer + sk key masked");
        Check(Redactor.Text("export OPENAI_API_KEY=sk-proj-1234567890abcdef").EndsWith(Redactor.Mask),
            "api key env value masked");
        Check(!Redactor.Text("--token=abc123456 --password hunter2").Contains("hunter2"), "token/password args masked");
        Check(!Redactor.Text("--token=abc123456").Contains("abc123456"), "equals-form token arg masked");
        Check(!Redactor.Text("--password hunter2").Contains("hunter2"), "space-form password arg masked");
        Check(Redactor.Text("dotnet build --configuration Release") == "dotnet build --configuration Release",
            "benign long options untouched");
        Check(Redactor.Text("https://user:pass@github.com/org/repo") == "https://***@github.com/org/repo",
            "url userinfo masked");
        Check(Redactor.Text("cat ~/.config/credentials/api.txt").Contains("credentials/***"), "credential path masked");
        Check(Redactor.Text("dotnet build -c Release") == "dotnet build -c Release", "ordinary command untouched");
        var stack = Redactor.Multiline(new string('x', 10) + "\n" + new string('y', 10) + "\n" + new string('z', 10), maxLines: 2);
        Check(stack.Split('\n').Length == 3 && stack.EndsWith("…"), "multiline capped with ellipsis");
        Check(Redactor.Text(new string('a', 5000)).Length <= 4097, "long text bounded");
    }
}

internal static class JsonlTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-perf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var writer = new PerfJsonlWriter(root, segmentMb: 8))
            {
                for (var i = 0; i < 50; i++)
                {
                    writer.Enqueue(AggregatorTests.Record("tool", i, "Ok", ("tool", "run_command"),
                        ("command", "curl -H 'Authorization: Bearer sk-abcdef1234567890abcd' x")));
                }
                writer.Enqueue(new PerfEventRecord(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, "ui.stall",
                    "dispatcher.background", 350, "blocked"));
                writer.Flush(TimeSpan.FromSeconds(5));
            }

            var files = Directory.GetFiles(root, "perf-*.jsonl");
            Check(files.Length == 1, "one segment written");
            var lines = File.ReadAllLines(files[0]);
            Check(lines.Length == 51, "all records flushed");
            Check(lines.Any(line => line.Contains("\"type\":\"span\"")) &&
                  lines.Any(line => line.Contains("\"type\":\"event\"")), "span and event records distinguishable");
            Check(!string.Join('\n', lines).Contains("abcdef1234567890"), "jsonl content is redacted");
            Check(lines.Any(line => line.Contains("\"durationMs\"")), "span carries duration");

            // 轮转：第二个 writer 打开后超过上限会写新文件（用 8MB 上限 + 大记录无法直接触发，改为验证文件名规则）。
            Check(Path.GetFileName(files[0]).StartsWith($"perf-{DateTime.UtcNow:yyyyMMdd}-"), "segment named by day and index");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal static class RecorderTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-perf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new PerfSettings { Mode = "standard", CaptureCommandText = true, RetentionDays = 0 };
            var recorder = new PerfRecorder(settings, Path.Combine(root, "perf"));
            var record = AggregatorTests.Record("tool.process", 250, "Ok", ("tool", "run_command"),
                ("command", "echo sk-abcdef1234567890abcd"), ("exitCode", "0"));
            recorder.OnSpanEnd(ToEnd(record));
            var stored = recorder.Ring.Snapshot().Single();
            Check(!stored.Tag("command")!.Contains("abcdef1234567890"), "recorder redacts command tags");
            Check(recorder.Aggregator.Get("tool.process") is not null, "recorder aggregates spans");

            // 回放的 span 带真实结束时间：时长按开始/结束差值，而不是“收到时刻”。
            var replayStart = System.Diagnostics.Stopwatch.GetTimestamp();
            recorder.OnSpanEnd(new PerfSpanEnd(
                new PerfSpanStart(9, 10, 0, "startup.replayed", replayStart),
                PerfSpanStatus.Ok, null, null,
                replayStart + System.Diagnostics.Stopwatch.Frequency / 5)); // 200 ms
            var replayed = recorder.Ring.Snapshot().Last();
            Check(replayed.Name == "startup.replayed" && Math.Abs(replayed.DurationMs - 200) < 5,
                "recorder honors explicit end ticks for replayed spans");

            recorder.ApplySettings(settings with { CaptureCommandText = false });
            recorder.OnSpanEnd(ToEnd(AggregatorTests.Record("tool.process", 10, "Ok", ("tool", "run_command"),
                ("command", "echo secret-value"))));
            Check(recorder.Ring.Snapshot().Last().Tag("command") == "[已关闭命令文本记录]", "command text can be disabled");
            // 设置随数据根保存（数据根 = 调用方传入的目录；记录器内部再建 perf 子目录）。
            Check(File.Exists(Path.Combine(recorder.DataRoot, "perf-settings.json")), "settings persisted");

            // 命令失败触发异常规则。
            recorder.OnSpanEnd(ToEnd(AggregatorTests.Record("tool", 5, "Error", ("tool", "run_command"))));
            Check(recorder.Anomalies().Any(a => a.Rule == "tool.error"), "recorder surfaces anomalies");
            // 关闭模式：只计数，不记录、不聚合。
            recorder.ApplySettings(settings with { CaptureCommandText = true, Mode = "off" });
            var before = recorder.Ring.TotalWritten;
            recorder.OnSpanEnd(ToEnd(AggregatorTests.Record("tool", 1, "Ok", ("tool", "read_file"))));
            Check(recorder.Ring.TotalWritten == before, "off mode records nothing");
            recorder.ApplySettings(settings with { Mode = "standard" });

            recorder.Clear();
            Check(recorder.Ring.Count == 0 && recorder.Anomalies().Count == 0, "clear resets memory");

            // 保留天数在“仅内存”与“落盘”之间切换不抛异常，切换后仍能记录。
            recorder.ApplySettings(settings with { RetentionDays = 3 });
            recorder.ApplySettings(settings with { RetentionDays = 0 });
            recorder.OnSpanEnd(ToEnd(AggregatorTests.Record("tool", 2, "Ok", ("tool", "read_file"))));
            Check(recorder.Ring.Count == 1, "recorder keeps working after retention toggle");
            recorder.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}

internal static class PluginTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-perf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var recovery = new PerfRecovery();
        try
        {
            var package = new PerformanceMonitorPluginPackage();
            Check(package.Id == "performance.monitor" && package.Version == "1.0.0", "package identity");
            var manifestId = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(RepoRoot(), "src/Fufucode.Plugins.PerformanceMonitor/manifest.json")));
            Check(manifestId.RootElement.GetProperty("id").GetString() == package.Id, "manifest id matches package");
            Check(manifestId.RootElement.GetProperty("version").GetString() == package.Version, "manifest version matches package");

            var context = new Fufucode.Core.Plugins.PluginPackageContext(package.Id, root, null, null, root);
            package.InitializeAsync(context).GetAwaiter().GetResult();
            recovery.Attached = PerfTracer.Sink is PerfRecorder;
            Check(PerfTracer.Enabled, "plugin installs sink on initialize");
            // 没有宿主桥时不贡献页面，也不抛异常。
            Check(package.CreatePageContribution(context) is null, "no page without host bridge");

            using (PerfTracer.Step("plugin-test-span"))
            {
                Thread.Sleep(1);
            }
            var recorder = (PerfRecorder)PerfTracer.Sink!;
            Check(recorder.Ring.Count >= 1, "spans reach plugin recorder");

            // 模拟第二个管理器激活同一插件：复用接收端，不重复安装。
            var second = new PerformanceMonitorPluginPackage();
            second.InitializeAsync(context).GetAwaiter().GetResult();
            Check(ReferenceEquals(PerfTracer.Sink, recorder), "second activation reuses sink");
            second.ShutdownAsync().GetAwaiter().GetResult();
            Check(PerfTracer.Enabled, "first instance keeps sink while second stops");

            package.ShutdownAsync().GetAwaiter().GetResult();
            Check(!PerfTracer.Enabled, "shutdown detaches sink");
        }
        finally
        {
            if (PerfTracer.Sink is not null) recovery.Reset();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fufucode.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private sealed class PerfRecovery
    {
        public bool Attached;

        public void Reset()
        {
            if (PerfTracer.Sink is IPerfSink sink) PerfTracer.Detach(sink);
        }
    }
}
