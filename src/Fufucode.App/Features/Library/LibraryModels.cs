using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fufucode.App.Features.Library;

/// <summary>资料条目类型。网页首版只保存链接与用户提供的快照，不抓取网络内容。</summary>
public enum LibraryEntryKind
{
    File = 0,
    WebPage = 1,
    SessionSnippet = 2,
    Artifact = 3,
    Text = 4,
}

/// <summary>引用：只记录来源路径/版本；副本：库内保存内容快照，源文件删除后仍可读。</summary>
public enum LibraryStorageMode
{
    Reference = 0,
    Copy = 1,
}

/// <summary>来源状态。Changed 表示来源内容与库内记录版本不一致，不能冒充原内容。</summary>
public enum LibraryEntryStatus
{
    Ok = 0,
    Missing = 1,
    Changed = 2,
}

/// <summary>提取/索引状态。Failed 保留原文件并在界面显示原因，不静默丢弃。</summary>
public enum LibraryTextState
{
    Pending = 0,
    Indexed = 1,
    Unsupported = 2,
    Failed = 3,
}

public enum LibraryImportStatus
{
    Imported = 0,
    Duplicate = 1,
    Failed = 2,
}

/// <summary>一次内容版本。Origin 说明版本来源：import / accepted-change / manual。</summary>
public sealed record LibraryVersion(
    int Number,
    string ContentHash,
    long Size,
    DateTimeOffset? SourceModifiedUtc,
    DateTimeOffset CapturedUtc,
    string? StoredPath,
    string Origin)
{
    public string ShortHash => ContentHash.Length >= 8 ? ContentHash[..8] : ContentHash;
    public string OriginText => Origin switch
    {
        "import" => "导入",
        "accepted-change" => "来源变化后同步",
        "manual" => "手动",
        _ => Origin,
    };
    public string Summary =>
        $"v{Number} · {ShortHash} · {OriginText} · {CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

public sealed record LibraryEntry(
    string Id,
    string Title,
    LibraryEntryKind Kind,
    LibraryStorageMode StorageMode,
    string? SourcePath,
    string? SourceUrl,
    string? StoredPath,
    string? ContentHash,
    long SourceSize,
    DateTimeOffset? SourceModifiedUtc,
    DateTimeOffset AddedUtc,
    DateTimeOffset UpdatedUtc,
    LibraryEntryStatus Status,
    DateTimeOffset? LastCheckedUtc,
    string? Note,
    LibraryTextState TextState,
    string? TextError,
    IReadOnlyList<string> Tags,
    int VersionCount)
{
    public bool HasLocalCopy => StorageMode == LibraryStorageMode.Copy && StoredPath is not null;

    /// <summary>内容当前是否可读：副本始终可读；引用在来源丢失后不可读。</summary>
    public bool ContentAvailable => HasLocalCopy || Status != LibraryEntryStatus.Missing;

    public string SourceDisplay => !string.IsNullOrWhiteSpace(SourceUrl) ? SourceUrl!
        : !string.IsNullOrWhiteSpace(SourcePath) ? SourcePath!
        : "(无来源)";

    public string KindText => Kind switch
    {
        LibraryEntryKind.File => "文件",
        LibraryEntryKind.WebPage => "网页",
        LibraryEntryKind.SessionSnippet => "会话片段",
        LibraryEntryKind.Artifact => "产物",
        _ => "文本",
    };

    public string StorageText => StorageMode == LibraryStorageMode.Copy ? "副本" : "引用";

    public string StatusText => Status switch
    {
        LibraryEntryStatus.Missing => "来源不可达",
        LibraryEntryStatus.Changed => "来源已变化",
        _ => "来源正常",
    };

    public string TextStateText => TextState switch
    {
        LibraryTextState.Indexed => "已索引",
        LibraryTextState.Pending => "待索引",
        LibraryTextState.Unsupported => "未索引（不支持的格式）",
        _ => "索引失败",
    };

    public string VersionText => "v" + VersionCount.ToString();
    /// <summary>列表行摘要：类型 · 存储方式 · 来源状态 · 索引状态 · 版本。</summary>
    public string RowSummary => $"{KindText} · {StorageText} · {StatusText} · {TextStateText} · {VersionText}";


    public string ShortHash => ContentHash is { Length: >= 8 } hash ? hash[..8] : (ContentHash ?? "无哈希");

    public string AddedText => AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string TagText => Tags.Count == 0 ? "无标签" : string.Join(" · ", Tags);
}

/// <summary>导入请求。Text 用于会话片段；Path 用于文件/产物；Url 用于网页链接。</summary>
public sealed record LibraryImportRequest(
    LibraryEntryKind Kind,
    LibraryStorageMode StorageMode,
    string? Path = null,
    string? Url = null,
    string? Text = null,
    string? Title = null,
    string? Note = null,
    IReadOnlyList<string>? Tags = null);

public sealed record LibraryImportOutcome(
    LibraryImportStatus Status,
    string? EntryId,
    string Title,
    string Message,
    string? DuplicateOfEntryId = null)
{
    public bool IsImported => Status == LibraryImportStatus.Imported;
}

public sealed record LibrarySearchHit(LibraryEntry Entry, int Score, string? Snippet);

public sealed record LibraryTagCount(string Tag, int Count)
{
    public string Label => Count > 0 ? $"{Tag} ({Count})" : Tag;
}

/// <summary>来源检查结果。Changed 由大小/时间戳或哈希差异判定，绝不以旧内容冒充当前来源。</summary>
public sealed record LibrarySourceState(
    bool Exists,
    bool Changed,
    long Size,
    DateTimeOffset? ModifiedUtc,
    string? Reason);

public sealed record LibraryIndexProgress(int Completed, int Total, string CurrentTitle)
{
    public string Text => Total == 0 ? "无待索引条目" : $"索引 {Completed}/{Total}：{CurrentTitle}";
}

public sealed record LibraryExportResult(
    string ManifestPath,
    int EntryCount,
    int ContentFiles,
    IReadOnlyList<string> Warnings);

public sealed record LibraryBundleImportResult(
    int Imported,
    int Duplicates,
    int Failed,
    IReadOnlyList<string> Messages);

/// <summary>
/// 显式聊天引用载荷：聊天接线（G1 后由 Codex 接入）只消费本记录，不直接读资料库内部结构。
/// </summary>
public sealed record LibraryCitation(
    string EntryId,
    string Title,
    string Source,
    string? VersionHash,
    string Snippet,
    DateTimeOffset GeneratedUtc)
{
    public string ShortHash => VersionHash is { Length: >= 8 } hash ? hash[..8] : (VersionHash ?? "无哈希");

    /// <summary>可插入输入框的 Markdown 引用块，包含来源与版本以便回溯。</summary>
    public string Markdown
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append("> 资料《").Append(Title).Append("》· 来源：").Append(Source)
                .Append(" · 版本 ").Append(ShortHash).Append('\n');
            foreach (var line in Snippet.Replace("\r\n", "\n").Split('\n'))
                builder.Append("> ").Append(line).Append('\n');
            return builder.ToString().TrimEnd('\n');
        }
    }
}
