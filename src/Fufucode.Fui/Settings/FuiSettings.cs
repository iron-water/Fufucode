using System.Text.Json;

namespace Fufucode.Fui;

internal sealed record FuiSettings
{
    internal const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Theme { get; init; } = "auto";
    public bool ShowThinking { get; init; } = true;
    public bool ExpandTools { get; init; }
    public string Hyperlinks { get; init; } = "auto";
    public string DesktopNotifications { get; init; } = "auto";
    public IReadOnlyList<string> StatusSegments { get; init; } = FuiStatusSegmentCatalog.DefaultEnabled;
    public bool CompactCards { get; init; } = true;
    public bool MouseWheel { get; init; } = true;
    public string EmojiAutocomplete { get; init; } = "auto";
    public bool VimMode { get; init; }
    public IReadOnlyDictionary<string, string> Keybindings { get; init; } = FuiKeyBindingCatalog.DefaultBindings;

    internal static FuiSettings Default { get; } = new();
}

internal sealed record FuiSettingsUpdateResult(bool Ok, string? Error)
{
    internal static FuiSettingsUpdateResult Success { get; } = new(true, null);
}

/// <summary>
/// FUI-only presentation settings. This deliberately does not read or mutate the GUI/CLI settings store.
/// </summary>
internal sealed class FuiSettingsStore
{
    private const string RelativePath = "fui/settings.json";
    private readonly object _gate = new();
    private readonly bool _persistent;

    private FuiSettingsStore(string dataRoot, string path, FuiSettings current, string? loadIssue, bool persistent)
    {
        DataRoot = dataRoot;
        Path = path;
        Current = current;
        LoadIssue = loadIssue;
        _persistent = persistent;
    }

    internal string DataRoot { get; }
    internal string Path { get; }
    internal FuiSettings Current { get; private set; }
    internal string? LoadIssue { get; private set; }

    internal static FuiSettingsStore Load(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = System.IO.Path.GetFullPath(dataRoot);
        var path = System.IO.Path.Combine(root, RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var current = FuiSettings.Default;
        string? issue = null;
        if (File.Exists(path))
        {
            try
            {
                current = Parse(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or ArgumentException)
            {
                issue = $"设置文件损坏或不可读，已使用默认值：{path} · {error.GetBaseException().Message}";
            }
        }
        return new(root, path, current, issue, persistent: true);
    }

    internal static FuiSettingsStore CreateEphemeral()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fui-ephemeral-" + Guid.NewGuid().ToString("N"));
        return new(root, System.IO.Path.Combine(root, RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
            FuiSettings.Default, null, persistent: false);
    }

    internal FuiSettingsUpdateResult Update(Func<FuiSettings, FuiSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            FuiSettings next;
            try
            {
                next = Normalize(change(Current));
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                return new(false, error.Message);
            }
            if (next == Current) return FuiSettingsUpdateResult.Success;
            if (!_persistent)
            {
                Current = next;
                return FuiSettingsUpdateResult.Success;
            }

            var directory = System.IO.Path.GetDirectoryName(Path)!;
            var temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temp, Serialize(next));
                if (LoadIssue is not null && File.Exists(Path))
                {
                    var backup = Path + ".before-repair-" +
                        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                    File.Copy(Path, backup, overwrite: false);
                }
                Replace(temp);
                Current = next;
                LoadIssue = null;
                return FuiSettingsUpdateResult.Success;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new(false, $"设置未保存：{error.GetBaseException().Message}");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static FuiSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("设置根节点必须是 JSON 对象。");
        var version = ReadInt(root, "version", FuiSettings.CurrentVersion);
        if (version is < 1 or > FuiSettings.CurrentVersion)
            throw new InvalidDataException($"不支持的 FUI 设置版本：{version}。");

        var theme = ReadString(root, "theme", "auto");
        RequireOneOf(theme, "theme", FuiSettingCatalog.ThemeValues);
        var hyperlinks = ReadString(root, "hyperlinks", "auto");
        RequireOneOf(hyperlinks, "hyperlinks", FuiSettingCatalog.HyperlinkValues);
        var notifications = ReadString(root, "desktopNotifications", "auto");
        RequireOneOf(notifications, "desktopNotifications", FuiSettingCatalog.NotificationValues);
        var emoji = ReadString(root, "emojiAutocomplete", "auto");
        RequireOneOf(emoji, "emojiAutocomplete", FuiSettingCatalog.EmojiValues);

        var segments = new List<string>();
        if (root.TryGetProperty("statusSegments", out var segmentNode))
        {
            if (segmentNode.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("statusSegments 必须是数组。");
            foreach (var item in segmentNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("statusSegments 只能包含字符串。");
                var id = item.GetString()!;
                if (FuiStatusSegmentCatalog.Find(id) is null)
                    throw new InvalidDataException($"未知状态分段：{id}。");
                if (!segments.Contains(id, StringComparer.Ordinal)) segments.Add(id);
            }
        }
        else
        {
            segments.AddRange(FuiStatusSegmentCatalog.DefaultEnabled);
        }

        var keybindings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("keybindings", out var keyNode))
        {
            if (keyNode.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("keybindings 必须是 JSON 对象。");
            foreach (var property in keyNode.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"keybindings.{property.Name} 必须是字符串。");
                keybindings[property.Name] = property.Value.GetString()!;
            }
        }
        else
        {
            foreach (var pair in FuiKeyBindingCatalog.DefaultBindings) keybindings[pair.Key] = pair.Value;
        }
        if (!FuiKeyBindingCatalog.TryNormalizeBindings(keybindings, out var normalizedKeys, out var keyError))
            throw new InvalidDataException(keyError);

        return Normalize(new FuiSettings
        {
            Version = version,
            Theme = theme,
            ShowThinking = ReadBool(root, "showThinking", true),
            ExpandTools = ReadBool(root, "expandTools", false),
            Hyperlinks = hyperlinks,
            DesktopNotifications = notifications,
            StatusSegments = segments,
            CompactCards = ReadBool(root, "compactCards", true),
            MouseWheel = ReadBool(root, "mouseWheel", true),
            EmojiAutocomplete = emoji,
            VimMode = ReadBool(root, "vimMode", false),
            Keybindings = normalizedKeys
        });
    }

    private static FuiSettings Normalize(FuiSettings settings)
    {
        if (settings.Version != FuiSettings.CurrentVersion)
            throw new InvalidDataException($"不支持的 FUI 设置版本：{settings.Version}。");
        if (!FuiSettingCatalog.ThemeValues.Contains(settings.Theme, StringComparer.Ordinal))
            throw new ArgumentException($"无效主题：{settings.Theme}。");
        if (!FuiSettingCatalog.HyperlinkValues.Contains(settings.Hyperlinks, StringComparer.Ordinal))
            throw new ArgumentException($"无效链接设置：{settings.Hyperlinks}。");
        if (!FuiSettingCatalog.NotificationValues.Contains(settings.DesktopNotifications, StringComparer.Ordinal))
            throw new ArgumentException($"无效通知设置：{settings.DesktopNotifications}。");
        if (!FuiSettingCatalog.EmojiValues.Contains(settings.EmojiAutocomplete, StringComparer.Ordinal))
            throw new ArgumentException($"无效 Emoji 补全设置：{settings.EmojiAutocomplete}。");

        var segments = new List<string>();
        foreach (var id in settings.StatusSegments)
        {
            if (FuiStatusSegmentCatalog.Find(id) is null) throw new ArgumentException($"未知状态分段：{id}。");
            if (!segments.Contains(id, StringComparer.Ordinal)) segments.Add(id);
        }
        if (!FuiKeyBindingCatalog.TryNormalizeBindings(settings.Keybindings, out var keys, out var keyError))
            throw new ArgumentException(keyError);
        return settings with
        {
            Version = FuiSettings.CurrentVersion,
            StatusSegments = segments,
            Keybindings = keys
        };
    }

    private static string Serialize(FuiSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", settings.Version);
            writer.WriteString("theme", settings.Theme);
            writer.WriteBoolean("showThinking", settings.ShowThinking);
            writer.WriteBoolean("expandTools", settings.ExpandTools);
            writer.WriteString("hyperlinks", settings.Hyperlinks);
            writer.WriteString("desktopNotifications", settings.DesktopNotifications);
            writer.WriteStartArray("statusSegments");
            foreach (var segment in settings.StatusSegments) writer.WriteStringValue(segment);
            writer.WriteEndArray();
            writer.WriteBoolean("compactCards", settings.CompactCards);
            writer.WriteBoolean("mouseWheel", settings.MouseWheel);
            writer.WriteString("emojiAutocomplete", settings.EmojiAutocomplete);
            writer.WriteBoolean("vimMode", settings.VimMode);
            writer.WriteStartObject("keybindings");
            foreach (var action in FuiKeyBindingCatalog.All)
            {
                if (FuiKeyBindingCatalog.TryResolve(settings.Keybindings, action.Id, out var chord, out _))
                    writer.WriteString(action.Id, chord.Format());
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    private void Replace(string temp)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, Path, overwrite: true);
                return;
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                                             (error.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(40 * (attempt + 1));
            }
        }
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 必须是字符串。");
        return value.GetString()!;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{name} 必须是布尔值。")
        };
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"{name} 必须是整数。");
        return number;
    }

    private static void RequireOneOf(string value, string name, IReadOnlyList<string> allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"{name} 的值无效：{value}。");
    }
}
