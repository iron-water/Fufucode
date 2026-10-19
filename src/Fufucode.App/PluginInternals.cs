// 一等方插件与定向测试使用宿主内部接缝（例如 SkinDialog、终端测试注入点）。
// 可选插件仍在运行时动态加载，App 不静态引用插件程序集。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Fufucode.Plugins.Terminal")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Fufucode.App.Terminal.Tests")]
