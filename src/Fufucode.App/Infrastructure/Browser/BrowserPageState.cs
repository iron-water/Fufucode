namespace Fufucode.App.Infrastructure.Browser;

internal sealed record BrowserPageState(int Version = 1, string AddressDraft = "",
    string? NavigationAddress = null, string? FilePath = null);
