using System.Text;

namespace Fufucode.Core.Shell;

/// <summary>按实际生效的解释器生成给模型的 run_command 语法提示（系统提示的一部分）。</summary>
public static class CommandShellPrompt
{
    /// <summary>环境行里的解释器名称；解析失败时如实写出原因。</summary>
    public static string DescribeShell(ResolvedCommandShell? shell, string? error) =>
        shell is null ? $"不可用（{error}）" : shell.Kind.DisplayName;

    /// <summary>run_command 使用要点；不同语法家族给不同的提示。</summary>
    public static string BuildGuidance(ResolvedCommandShell? shell)
    {
        var sb = new StringBuilder();
        if (shell is null)
        {
            sb.AppendLine("2. 当前没有可用的命令解释器，run_command 会失败；请改用内置文件工具，并在最终回复中说明。");
            return sb.ToString();
        }

        sb.AppendLine($"2. run_command 默认在 {shell.Kind.DisplayName} 中执行：");
        switch (shell.Kind.Family)
        {
            case CommandShellFamily.PowerShell:
                sb.AppendLine(shell.Kind.Id == CommandShells.Pwsh
                    ? "   - 用 ';' 或换行分隔语句；'&&' / '||' 可按外部程序成败串联。"
                    : "   - 用 ';' 或换行分隔语句；Windows PowerShell 5.1 不支持 '&&' / '||'，用 `if ($LASTEXITCODE -eq 0) { ... }`。");
                sb.AppendLine("   - 环境变量写 $env:NAME；含空格的路径用引号并以 '&' 调用，如 & \"C:\\Program Files\\x\\x.exe\" arg。");
                sb.AppendLine("   - ls、cat、rm 等是 PowerShell 别名，参数与 Unix 不同；grep、head、sed 不存在，用 Select-String、Select-Object -First/-Last 等代替。");
                sb.AppendLine("   - 退出码取最后一条语句：外部程序失败时为其退出码，cmdlet 出错为 1。");
                break;
            case CommandShellFamily.Cmd:
                sb.AppendLine("   - 串联命令使用 '&&' 或 '&'，不要使用 ';'。");
                sb.AppendLine("   - 使用 Windows 原生命令（如 dir、where.exe、findstr、type），不要假设存在 ls、cat、grep、head 等 Unix 工具。");
                break;
            default:
                sb.AppendLine("   - POSIX shell 语法：'&&'、'|'、$VAR、单引号字面量。");
                if (shell.Kind.Id == CommandShells.GitBash)
                    sb.AppendLine("   - Windows 路径写成 /c/Projects/... 或用引号包住反斜杠路径。");
                break;
        }

        var others = CommandShells.Available().Where(item => item.Kind.Id != shell.Kind.Id).Select(item => item.Kind.Id).ToArray();
        if (others.Length > 0)
            sb.AppendLine($"   - 某条命令需要其他语法时可传 shell 参数，本机可用：{string.Join("、", others)}。");
        return sb.ToString();
    }
}
