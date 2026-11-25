using Fufucode.Fui.Tests;

try
{
    ControllerTests.Run();
    Console.WriteLine("PASS controller: commands, streaming, drafts, approvals, stale requests and subagent activity");
    TerminalTests.Run();
    Console.WriteLine("PASS terminal: editor, layout and terminal rendering");
    ToolCardTests.Run();
    Console.WriteLine("PASS tool cards: registry, bounded projection, sanitization and layout");
    ThemeTests.Run();
    Console.WriteLine("PASS theme: dark/light/mono/auto, color depth and terminal style safety");
    await SettingsTests.RunAsync();
    Console.WriteLine("PASS settings: persistence, corruption repair, theme, status, keys, notifications and terminal matrix");
    MouseTests.Run();
    Console.WriteLine("PASS mouse: SGR input, wheel, hit testing and approval safety");
    FuzzyPickerTests.Run();
    Console.WriteLine("PASS fuzzy picker: stable word-aware ranking, Unicode and cached large-list search");
    WorkbenchWidgetTests.Run();
    Console.WriteLine("PASS workbench widgets: selection, markdown, folding, navigation and editing");
    await MarkdownFeatureTests.RunAsync();
    Console.WriteLine("PASS markdown features: syntax, wide glyphs, streaming, links, OSC 8, Mermaid and copy");
    await WorkbenchFlowTests.RunAsync();
    Console.WriteLine("PASS workbench flows: command dispatch, drafts, project context and file protections");
    await RuntimeTests.RunAsync();
    Console.WriteLine("PASS runtime: FUMO session integration");
    await WorkbenchSessionTests.RunAsync();
    Console.WriteLine("PASS workbench sessions: native actions, model selection, branching and export");
    await TranscriptWorkbenchTests.RunAsync();
    Console.WriteLine("PASS transcript workbench: outline, search, jump, streaming, copy, filters, info, retry and large lists");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
