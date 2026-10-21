using Avalonia;

namespace ProbeWebView2;

class Program
{
    public static string ModelName = "Hiyori";
    public static int RecordSeconds = 0;
    public static string OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fufu-live2d", "out");

    [STAThread]
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": ModelName = args[++i]; break;
                case "--record": RecordSeconds = int.Parse(args[++i]); break;
                case "--out": OutDir = args[++i]; break;
            }
        }
        Directory.CreateDirectory(OutDir);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
