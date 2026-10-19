using System.Runtime.CompilerServices;

// T1-TERMINAL 定向测试入口（tests/Fufucode.App.Terminal.Tests）需要访问终端输入接收端的
// 测试接缝（剪贴板替身、焦点/字符捕获钩子）。与 Browser 轨道同样只开放给对应测试程序集。
[assembly: InternalsVisibleTo("Fufucode.App.Terminal.Tests")]
