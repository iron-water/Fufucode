using System.Runtime.CompilerServices;

// 自动开发自测已迁出 App，作为 App 的测试组件（InternalsVisibleTo Fufucode.App.Automation.SelfTests）
// 继续验证页面、停靠与截图；发布裁剪时整体移除（见 Fufucode.App.csproj TrimmerRootAssembly）。
[assembly: InternalsVisibleTo("Fufucode.App.Automation.SelfTests")]
