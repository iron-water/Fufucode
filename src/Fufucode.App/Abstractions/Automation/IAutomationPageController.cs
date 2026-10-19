using System;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions.Automation;

/// <summary>
/// 宿主对自动开发页面的最小控制面：项目切换、连接/断开，以及“已连接数据库”通知。
/// 页面由插件贡献；宿主不引用插件的视图模型类型。
/// </summary>
public interface IAutomationPageController
{
    /// <summary>当前数据库路径；未连接时为空字符串。</summary>
    string DatabasePath { get; }

    /// <summary>切换项目上下文；实现方负责断开旧库并保留各自的草稿。</summary>
    void SetProjectContext(string? projectPath);

    /// <summary>连接用户显式选择的工作台数据库。</summary>
    Task ConnectAsync(string databasePath);

    /// <summary>断开当前数据库，不删除任何数据。</summary>
    void Disconnect();

    /// <summary>连接成功后触发：数据库绝对路径 + 项目上下文。</summary>
    event Action<string, string?>? DatabaseConnected;
}

/// <summary>
/// 插件贡献的命令入口：宿主 CLI 在没有静态引用插件程序集时仍然能执行 control/host。
/// 返回退出码与已序列化的标准输出；长驻 host serve 直接写 <see cref="System.IO.TextWriter"/>。
/// </summary>
public interface IAutomationCommandHost
{
    Task<int> RunControlAsync(string[] arguments, System.IO.TextWriter output,
        System.Threading.CancellationToken cancellationToken = default);

    Task<int> RunHostAsync(string[] arguments, System.IO.TextWriter output,
        System.Threading.CancellationToken cancellationToken = default);
}
