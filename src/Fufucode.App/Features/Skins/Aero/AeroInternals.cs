using System.Runtime.CompilerServices;

// E7-AERO：限定测试程序集可见内部重载（AeroLease.ApplyCore 的 URI 注入仅用于失败回滚反例）。
[assembly: InternalsVisibleTo("Fufucode.App.Aero.Tests")]
