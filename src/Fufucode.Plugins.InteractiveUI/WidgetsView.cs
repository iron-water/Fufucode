using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.InteractiveUI;

/// <summary>
/// 交互组件渲染器。
///
/// 输入是一行一个 JSON 对象的文本（JSONL）；每个对象渲染成一个原生控件。
/// 解析失败的行走降级显示，不影响同一块里的其它组件。
/// 组件状态只存在于本地；点动作按钮时把当前选择汇总成一段文本交给宿主提交或填入输入框。
/// </summary>
public sealed partial class WidgetsView : Border
{
    private readonly PluginContentContext _context;
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly List<(string Label, Func<string> Read)> _readers = new();
    private readonly List<Control> _actionButtons = new();
    private readonly TextBlock _feedback = new() { FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };

    public WidgetsView(PluginContentContext context)
    {
        _context = context;
        Padding = new Thickness(14, 12);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = Res("AppDividerSubtle", Brushes.Gray);
        Background = Res("AppInnerWell", Brushes.Transparent);

        _feedback.Foreground = Res("AppTextSecondary", Brushes.Gray);

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_rows);
        root.Children.Add(_feedback);
        Child = root;

        Build(context.Payload);
    }

    private void Build(string payload)
    {
        foreach (var raw in payload.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    AddError(line, "每一行必须是一个 JSON 对象");
                    continue;
                }
                AddWidget(element);
            }
            catch (JsonException error)
            {
                AddError(line, error.Message);
            }
            catch (Exception error)
            {
                AddError(line, error.Message);
            }
        }

        if (_rows.Children.Count == 0) AddError(string.Empty, "没有可显示的组件");
    }

    private void AddWidget(JsonElement element)
    {
        var type = GetString(element, "type").ToLowerInvariant();
        var label = GetString(element, "label");

        switch (type)
        {
            case "slider": AddSlider(element, label); break;
            case "select": AddSelect(element, label); break;
            case "toggle": AddToggle(element, label); break;
            case "chips": AddChips(element, label); break;
            case "input": AddInput(element, label); break;
            case "number": AddNumber(element, label); break;
            case "rating": AddRating(element, label); break;
            case "progress": AddProgress(element, label); break;
            case "actions": AddActions(element); break;
#if ENHANCED_PREVIEW
            case "chart": AddChart(element, label); break;
            case "table": AddTable(element, label); break;
#endif
            default: AddError($"{type}", "不支持的组件类型"); break;
        }
    }

    private void AddSlider(JsonElement element, string label)
    {
        var minimum = GetDouble(element, "min", 0);
        var maximum = GetDouble(element, "max", 1);
        if (maximum <= minimum) maximum = minimum + 1;
        var value = Math.Clamp(GetDouble(element, "value", minimum), minimum, maximum);
        var step = GetDouble(element, "step", 0);

        var readout = new TextBlock { Text = Format(value), VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Right };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (step > 0) slider.TickFrequency = step;
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == RangeBase.ValueProperty) readout.Text = Format(slider.Value);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(readout, 1);
        row.Children.Add(slider);
        row.Children.Add(readout);
        AddRow(label, row);
        _readers.Add((label, () => Format(slider.Value)));
    }

    private void AddSelect(JsonElement element, string label)
    {
        var options = GetOptions(element);
        var selected = GetString(element, "value");
        var combo = new ComboBox
        {
            ItemsSource = options,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var index = options.IndexOf(selected);
        combo.SelectedIndex = index >= 0 ? index : (options.Count > 0 ? 0 : -1);
        AddRow(label, combo);
        _readers.Add((label, () => combo.SelectedItem as string ?? string.Empty));
    }

    private void AddToggle(JsonElement element, string label)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = GetBool(element, "value", false),
            OnContent = GetString(element, "on", "开启"),
            OffContent = GetString(element, "off", "关闭"),
        };
        AddRow(label, toggle);
        _readers.Add((label, () => toggle.IsChecked == true ? "是" : "否"));
    }

    private void AddChips(JsonElement element, string label)
    {
        var options = GetOptions(element);
        var multi = GetBool(element, "multi", false);
        var initial = GetString(element, "value");
        var selected = new HashSet<string>(
            initial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var chips = new List<(string Option, ToggleButton Chip)>();
        foreach (var option in options)
        {
            var chip = new ToggleButton
            {
                Content = option,
                IsChecked = selected.Contains(option),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(6),
                MinHeight = 0,
            };
            chip.PropertyChanged += (_, args) =>
            {
                if (args.Property != ToggleButton.IsCheckedProperty) return;
                if (multi) return;
                // 单选：取消其它选项。
                if (chip.IsChecked == true)
                {
                    foreach (var other in chips.Where(c => !ReferenceEquals(c.Chip, chip))) other.Chip.IsChecked = false;
                }
            };
            chips.Add((option, chip));
            panel.Children.Add(chip);
        }

        AddRow(label, panel);
        _readers.Add((label, () => string.Join("、", chips.Where(c => c.Chip.IsChecked == true).Select(c => c.Option))));
    }

    private void AddInput(JsonElement element, string label)
    {
        var box = new TextBox
        {
            Text = GetString(element, "value"),
            PlaceholderText = GetString(element, "placeholder", "输入内容"),
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            AcceptsReturn = false,
        };
        AddRow(label, box);
        _readers.Add((label, () => box.Text ?? string.Empty));
    }

    private void AddNumber(JsonElement element, string label)
    {
        var input = new NumericUpDown
        {
            Minimum = (decimal)GetDouble(element, "min", 0),
            Maximum = (decimal)GetDouble(element, "max", 1000),
            Increment = (decimal)GetDouble(element, "step", 1),
            Value = (decimal)GetDouble(element, "value", 0),
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0.##",
        };
        AddRow(label, input);
        _readers.Add((label, () => input.Value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty));
    }

    private void AddRating(JsonElement element, string label)
    {
        var maximum = (int)Math.Clamp(GetDouble(element, "max", 5), 1, 10);
        var current = (int)Math.Clamp(GetDouble(element, "value", 0), 0, maximum);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var stars = new List<Button>();

        void Refresh()
        {
            for (var index = 0; index < stars.Count; index++)
                stars[index].Content = index < current ? "★" : "☆";
        }

        for (var index = 0; index < maximum; index++)
        {
            var value = index + 1;
            var button = new Button
            {
                Content = "☆",
                FontSize = 16,
                Padding = new Thickness(4, 0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => { current = current == value ? 0 : value; Refresh(); };
            stars.Add(button);
            panel.Children.Add(button);
        }

        Refresh();
        AddRow(label, panel);
        _readers.Add((label, () => current == 0 ? "未评分" : current + "/" + maximum));
    }

    private void AddProgress(JsonElement element, string label)
    {
        var maximum = Math.Max(1, GetDouble(element, "max", 100));
        var value = Math.Clamp(GetDouble(element, "value", 0), 0, maximum);
        var bar = new ProgressBar { Minimum = 0, Maximum = maximum, Value = value, Width = 320, Height = 6 };
        var text = new TextBlock { Text = (int)Math.Round(value / maximum * 100) + "%", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(bar);
        row.Children.Add(text);
        AddRow(label, row);
    }

    private void AddActions(JsonElement element)
    {
        var options = GetOptions(element);
        var mode = GetString(element, "mode", "submit");
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var option in options)
        {
            var button = new Button
            {
                Content = option,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(14, 6),
                CornerRadius = new CornerRadius(6),
                MinHeight = 0,
            };
            if (panel.Children.Count == 0) button.Classes.Add("accent");
            else button.Classes.Add("subtle");

            button.Click += async (_, _) => await RunActionAsync(option, mode);
            _actionButtons.Add(button);
            panel.Children.Add(button);
        }

        _rows.Children.Add(panel);
    }

    private async Task RunActionAsync(string option, string mode)
    {
        var summary = BuildSummary();
        var text = string.IsNullOrWhiteSpace(summary)
            ? option
            : option + Environment.NewLine + Environment.NewLine + summary;

        if (string.Equals(mode, "insert", StringComparison.OrdinalIgnoreCase))
        {
            _context.InsertDraft(text);
            ShowFeedback("已填入输入框，可继续修改后发送。");
            return;
        }

        SetActionsEnabled(false);
        ShowFeedback("提交中…");
        try
        {
            var accepted = await _context.Submit(text);
            if (accepted)
            {
                ShowFeedback("已提交，等待回复。");
            }
            else
            {
                SetActionsEnabled(true);
                ShowFeedback("提交失败：当前会话不可发送。");
            }
        }
        catch (Exception error)
        {
            SetActionsEnabled(true);
            ShowFeedback("提交失败：" + error.Message);
        }
    }

    /// <summary>把各组件当前值汇总为一段模型可读的文本。</summary>
    private string BuildSummary()
    {
        var builder = new StringBuilder();
        foreach (var (label, read) in _readers)
        {
            var value = read();
            if (string.IsNullOrWhiteSpace(value)) continue;
            builder.Append("- ").Append(label.Length > 0 ? label : "选择").Append("：").Append(value).AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var button in _actionButtons) button.IsEnabled = enabled;
    }

    private void ShowFeedback(string text)
    {
        _feedback.Text = text;
        _feedback.IsVisible = true;
    }

    private void AddRow(string label, Control control)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            _rows.Children.Add(control);
            return;
        }

        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Res("AppTextSecondary", Brushes.Gray),
        });
        row.Children.Add(control);
        _rows.Children.Add(row);
    }

    private void AddError(string line, string message)
    {
        _feedback.IsVisible = true;
        _feedback.Text = string.IsNullOrEmpty(line)
            ? message
            : message + "：" + (line.Length > 120 ? line[..120] + "…" : line);
        _feedback.Foreground = Res("AppCriticalForeground", Brushes.IndianRed);
    }

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    private static string GetString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static double GetDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) ? number : fallback;

    private static List<string> GetOptions(JsonElement element)
    {
        var options = new List<string>();
        if (element.TryGetProperty("options", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    options.Add(text);
            }
        }
        return options;
    }

    private static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0001
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
