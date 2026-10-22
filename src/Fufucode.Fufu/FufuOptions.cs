namespace Fufucode.Fufu;

/// <summary>FUFU 启动参数。数据根独立于 Fufucode：默认 %LOCALAPPDATA%\FufuPet。</summary>
public sealed record FufuOptions(
    string? DataRootOverride = null,
    bool NoSingleInstance = false,
    // --autostart：登录自启动入口使用；当前只作标记，行为与正常启动一致。
    bool StartedFromAutoRun = false)
{
    public static FufuOptions Mock { get; } = new(NoSingleInstance: true);

    public string DataRoot => DataRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FufuPet");

    public static FufuOptions Parse(string[] args)
    {
        string? dataRoot = null;
        var noSingleInstance = false;
        var fromAutoRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length:
                    dataRoot = args[++i];
                    break;
                case "--no-single-instance":
                    noSingleInstance = true;
                    break;
                case "--autostart":
                    fromAutoRun = true;
                    break;
            }
        }
        return new FufuOptions(dataRoot, noSingleInstance, fromAutoRun);
    }
}
