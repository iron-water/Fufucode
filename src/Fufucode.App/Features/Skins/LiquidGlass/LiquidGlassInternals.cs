using System.Runtime.CompilerServices;

// LiquidGlass：限定测试程序集可见内部成员（LiquidGlassLease.ApplyCore 的 URI 注入、
// LiquidGlassSurface.CreateDrawParameters 用于参数传递与降级断言）。
[assembly: InternalsVisibleTo("Fufucode.App.LiquidGlass.Tests")]
