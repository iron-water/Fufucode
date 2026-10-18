namespace Fufucode.App.Features.TerminalExperience;

/// <summary>终端本地控制序列：只作用于本机输出缓冲区，不写回 shell。</summary>
public static class TerminalControlSequences
{
    /// <summary>
    /// 清屏 + 清空滚动缓冲 + 光标归位。终端缓冲区已支持 CSI 2J / 3J / H，
    /// 因此右键菜单“清屏”是纯本地操作，不会把任何字节发给正在运行的 shell。
    /// </summary>
    public const string ClearScreen = "\u001b[2J\u001b[3J\u001b[H";
}
