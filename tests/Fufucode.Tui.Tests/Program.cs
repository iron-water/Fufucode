using Fufucode.Tui.Tests;

try
{
    ControllerTests.Run();
    Console.WriteLine("PASS controller: commands, streaming, drafts, approval keys and stale requests");
    TerminalTests.Run();
    Console.WriteLine("PASS terminal: editor, layout and terminal rendering");
    await RuntimeTests.RunAsync();
    Console.WriteLine("PASS runtime: FUMO session integration");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
