using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 插件交给宿主的对象：设置页视图、导出、打开数据目录、清空，以及停靠页内容与异常摘要。
/// 宿主只通过 Core 契约（<see cref="IPerfPageHost"/> / <see cref="IPerfPageViewSource"/> /
/// <see cref="IPerfAnomalySource"/>）使用它。
/// </summary>
public sealed class PerfPageHost : IPerfPageHost, IPerfPageViewSource, IPerfAnomalySource
{
    private readonly PerfRecorder _recorder;
    private readonly string _dataRoot;
    private readonly string _exportDirectory;
    private PerfSettingsView? _settingsView;

    public PerfPageHost(PerfRecorder recorder, string dataRoot, Func<Avalonia.Controls.Control> pageContent)
    {
        _recorder = recorder;
        _dataRoot = dataRoot;
        _exportDirectory = Path.Combine(dataRoot, "exports");
        _pageContent = pageContent;
    }

    private readonly Func<Avalonia.Controls.Control> _pageContent;

    public object PageContent => _pageContent();

    public object SettingsView => _settingsView ??= new PerfSettingsView(_recorder, ExportSafely, () => { });

    public string? DataDirectory => _dataRoot;

    public int Count => _recorder.Anomalies().Count;

    public string Latest => _recorder.Anomalies().FirstOrDefault() is { } anomaly
        ? $"{anomaly.Rule}：{anomaly.Detail}"
        : "暂无异常。";

    public string Execute(PerfUiRequest request) => request switch
    {
        PerfUiRequest.Export => Export(),
        PerfUiRequest.OpenDataDirectory => OpenDirectory(),
        PerfUiRequest.Clear => Clear(),
        _ => "未识别的动作。",
    };

    private string ExportSafely()
    {
        try { return "已导出：" + Export(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "导出失败：" + error.Message;
        }
    }

    private string Clear()
    {
        _recorder.Clear();
        (_settingsView)?.RefreshStatus();
        return "已清空内存缓冲、聚合与异常列表。";
    }

    private string OpenDirectory()
    {
        Directory.CreateDirectory(_dataRoot);
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dataRoot)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"无法打开资源管理器：{error.Message}（路径：{_dataRoot}）";
        }
        return "已在资源管理器中打开：" + _dataRoot;
    }

    /// <summary>导出：汇总 + 脱敏后的 JSONL 原文，打包为 zip。</summary>
    public string Export()
    {
        Directory.CreateDirectory(_exportDirectory);
        var path = Path.Combine(_exportDirectory, $"fufucode-perf-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var summary = archive.CreateEntry("summary.json");
            using (var stream = summary.Open())
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("exportedAt", DateTimeOffset.Now);
                writer.WriteString("mode", _recorder.Settings.Mode);
                writer.WriteBoolean("captureCommandText", _recorder.Settings.CaptureCommandText);
                writer.WriteNumber("spansInMemory", _recorder.Ring.Count);
                writer.WriteNumber("spansTotal", _recorder.Ring.TotalWritten);
                writer.WriteNumber("ringOverwritten", _recorder.Ring.Overwritten);
                writer.WriteNumber("writerDropped", _recorder.Dropped);
                writer.WriteStartArray("anomalies");
                foreach (var anomaly in _recorder.Anomalies())
                {
                    writer.WriteStartObject();
                    writer.WriteString("rule", anomaly.Rule);
                    writer.WriteString("span", anomaly.SpanName);
                    writer.WriteNumber("valueMs", Math.Round(anomaly.ValueMs, 1));
                    if (anomaly.BaselineMs is { } baseline) writer.WriteNumber("baselineMs", Math.Round(baseline, 1));
                    writer.WriteString("detail", Redactor.Text(anomaly.Detail));
                    writer.WriteString("kind", anomaly.Kind.ToString());
                    writer.WriteString("timestampUtc", anomaly.TimestampUtc);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("stats");
                foreach (var stats in _recorder.Aggregator.All())
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", stats.Name);
                    writer.WriteNumber("samples", stats.Samples);
                    writer.WriteNumber("p50", Math.Round(stats.P50, 2));
                    writer.WriteNumber("p95", Math.Round(stats.P95, 2));
                    writer.WriteNumber("max", Math.Round(stats.Max, 2));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (Directory.Exists(_dataRoot))
            {
                foreach (var file in Directory.EnumerateFiles(_dataRoot, "perf-*.jsonl"))
                {
                    var entry = archive.CreateEntry("raw/" + Path.GetFileName(file));
                    using var output = entry.Open();
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    using var writer = new StreamWriter(output, new UTF8Encoding(false));
                    string? line;
                    while ((line = reader.ReadLine()) is not null) writer.WriteLine(Redactor.Text(line));
                }
                // 页面实际消费的宿主样本也必须在诊断包里，否则导出的“概览/时间线”无法复现。
                foreach (var file in Directory.EnumerateFiles(_dataRoot, "host-*.jsonl"))
                {
                    var entry = archive.CreateEntry("host/" + Path.GetFileName(file));
                    using var output = entry.Open();
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    using var writer = new StreamWriter(output, new UTF8Encoding(false));
                    string? line;
                    while ((line = reader.ReadLine()) is not null) writer.WriteLine(Redactor.Text(line));
                }
                foreach (var file in Directory.EnumerateFiles(_dataRoot, "turns-*.jsonl"))
                {
                    var entry = archive.CreateEntry("turns/" + Path.GetFileName(file));
                    using var output = entry.Open();
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    using var writer = new StreamWriter(output, new UTF8Encoding(false));
                    string? line;
                    while ((line = reader.ReadLine()) is not null) writer.WriteLine(Redactor.Text(line));
                }
            }
        }
        return path;
    }
}
