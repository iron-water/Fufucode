namespace Fufucode.App.Features.BrowserExperience;

/// <summary>地址栏下拉的一条候选：收藏优先，其次浏览历史。</summary>
public sealed record BrowserSuggestion(string Address, string Title, bool IsBookmark, string Detail)
{
    public string Header => string.IsNullOrWhiteSpace(Title) ? Address : Title;

    public string Badge => IsBookmark ? "收藏" : "历史";

    public string Description => IsBookmark
        ? Address
        : string.IsNullOrWhiteSpace(Detail) ? Address : Detail + " · " + Address;
}
