using Fufucode.Fui.Tests;

try
{
    ControllerTests.Run();
    Console.WriteLine("PASS controller: commands, streaming, drafts, approvals, stale requests and subagent activity");
    TerminalTests.Run();
    Console.WriteLine("PASS terminal: editor, layout and terminal rendering");
    ThemeTests.Run();
    Console.WriteLine("PASS theme: dark/light/mono/auto, color depth and terminal style safety");
    FuzzyPickerTests.Run();
    Console.WriteLine("PASS fuzzy picker: stable word-aware ranking, Unicode and cached large-list search");
    WorkbenchWidgetTests.Run();
    Console.WriteLine("PASS workbench widgets: selection, markdown, folding, navigation and editing");
    await ImageRenderTests.RunAsync();
    Console.WriteLine("PASS image render: multi-format decoders, SVG audit, SIXEL color, streaming data-URI, clipboard paste and legacy paste");
    await WorkbenchFlowTests.RunAsync();
    Console.WriteLine("PASS workbench flows: command dispatch, drafts, project context and file protections");
    await RuntimeTests.RunAsync();
    Console.WriteLine("PASS runtime: FUMO session integration");
    await WorkbenchSessionTests.RunAsync();
    Console.WriteLine("PASS workbench sessions: native actions, model selection, branching and export");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
