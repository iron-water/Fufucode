using System;
using Fufucode.Core.Updates;

namespace Fufucode.App.Integration;

/// <summary>
/// 产品 URL scheme 的唯一来源。
///
/// 正式构建注册 <c>fufucode:</c>，实验性测试构建注册 <c>fufucode-test:</c>：
/// 两个安装可以并存，若共用同一 scheme，后装的会抢走先装那一个的通知/链接。
/// 渠道来自 build-info.json（<see cref="BuildInfo.IsTestChannel"/>），
/// 因此安装器与程序在两处独立推导出同一个值，不需要额外的编译期开关。
/// </summary>
internal static class ProductLinks
{
    private static readonly Lazy<BuildInfo> Build = new(BuildInfo.ResolveCurrent);

    /// <summary>是否为实验性测试渠道构建。</summary>
    internal static bool IsTestBuild => Build.Value.IsTestChannel;

    /// <summary>会话深链 scheme（安装器注册的协议名）。</summary>
    internal static string SessionScheme => IsTestBuild ? "fufucode-test" : "fufucode";

    /// <summary>审批按钮 scheme：与安装器注册的会话链接分开，由应用启动时自注册。</summary>
    internal static string ApprovalScheme => SessionScheme + "-approval";
}
