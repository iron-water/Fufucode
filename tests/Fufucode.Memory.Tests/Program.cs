using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Memory;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.Memory;

// 记忆插件定向测试：dotnet run --project tests/Fufucode.Memory.Tests
var failures = 0;
var total = 0;
void Check(bool condition, string name)
{
    total++;
    if (condition) Console.WriteLine("PASS " + name);
    else { failures++; Console.WriteLine("FAIL " + name); }
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-memory-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    // ── 1. 文件格式 ──
    var notebook = new MemoryNotebook(root);
    Check(notebook.Read().Count == 0 && notebook.BuildPromptSection() is null, "没有记忆时不注入任何内容");
    var day = new DateOnly(2026, 11, 26);
    notebook.Add("默认 shell 用 PowerShell 7", day);
    notebook.Add("  默认 shell   用 powershell 7 ", day);
    Check(notebook.Read().Count == 1, "相同正文（忽略大小写与空白）不重复写入");
    Check(File.ReadAllText(notebook.FilePath).Contains("- 2026-11-26 默认 shell 用 PowerShell 7"), "条目以日期 + 正文的列表行落盘");

    File.AppendAllText(notebook.FilePath, "用户手写的说明段落\n* 手写条目没有日期\n");
    var entries = notebook.Read();
    Check(entries.Count == 2 && entries[1] is { Text: "手写条目没有日期", Date: null }, "用户手写的列表项也被识别");
    notebook.Add("回答用中文", day);
    Check(File.ReadAllText(notebook.FilePath).Contains("用户手写的说明段落"), "写入时保留非列表行");

    try { notebook.Add(new string('x', MemoryNotebook.MaxEntryChars + 1)); Check(false, "超长条目被拒绝"); }
    catch (ArgumentException) { Check(true, "超长条目被拒绝"); }

    var ambiguous = notebook.Forget("用");
    Check(ambiguous.Removed.Count == 0 && ambiguous.Candidates.Count >= 2, "片段匹配多条时不删除并列出候选");
    var removed = notebook.Forget("中文");
    Check(removed.Removed.Count == 1 && notebook.Read().All(e => e.Text != "回答用中文"), "唯一片段删除对应条目");
    Check(notebook.Forget("不存在").Removed.Count == 0, "无匹配时不删除");

    var prompt = notebook.BuildPromptSection()!;
    Check(prompt.Contains(notebook.FilePath) && prompt.Contains("默认 shell 用 PowerShell 7"), "注入段包含文件路径与条目");

    var budgetRoot = Path.Combine(root, "budget");
    var big = new MemoryNotebook(budgetRoot);
    for (var i = 0; i < 40; i++) big.Add($"第 {i:D2} 条 " + new string('长', 300), day);
    var bigPrompt = big.BuildPromptSection()!;
    Check(bigPrompt.Length < MemoryNotebook.PromptBudgetChars + 600 && bigPrompt.Contains("第 39 条") && !bigPrompt.Contains("第 00 条"),
        "超出预算时保留最新条目并说明其余在文件中");

    // ── 2. 工具 ──
    var handler = new MemoryToolHandler(new MemoryNotebook(Path.Combine(root, "tools")));
    async Task<PureToolResult> Call(string name, string json) => await handler.ExecuteAsync(new PureToolCall("c", name, json));
    var saved = await Call("memory_save", "{\"text\":\"提交前跑定向测试\"}");
    Check(saved.Success && saved.Content.StartsWith("已记住"), "memory_save 写入");
    var again = await Call("memory_save", "{\"text\":\"提交前跑定向测试\"}");
    Check(again.Success && again.Content.StartsWith("已存在"), "memory_save 重复时如实说明");
    var secret = await Call("memory_save", "{\"text\":\"key is sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH\"}");
    Check(!secret.Success, "memory_save 拒绝密钥");
    Check(!(await Call("memory_save", "{bad")).Success, "参数 JSON 无效时失败");
    Check(!(await Call("memory_save", "{}")).Success, "缺少 text 时失败");
    var forgot = await Call("memory_forget", "{\"text\":\"定向测试\"}");
    Check(forgot.Success && forgot.Content.Contains("提交前跑定向测试"), "memory_forget 删除");

    // ── 3. 插件门禁：只有已安装且已启用时注入与提供工具 ──
    var appRoot = Path.Combine(root, "app");
    var layout = new AppDataLayout(appRoot);
    var store = new PluginStateStore(layout);
    new MemoryNotebook(appRoot).Add("记忆插件测试条目", day);
    var package = new MemoryPluginPackage();
    var context = new PluginPackageContext(MemoryNotebook.PluginId, Path.Combine(layout.PluginsDirectory, MemoryNotebook.PluginId), null, null, appRoot);
    await package.InitializeAsync(context);
    var registry = new PluginToolRegistry(new PluginCapabilityGateway(store));
    registry.Register(package.CreateToolHandler(context)!);

    Check(PluginSystemPromptComposer.Compose(store) is not { } p0 || !p0.Contains("记忆插件测试条目"), "未安装时不注入记忆");
    Check(!registry.ActiveRegistrations.Any(r => r.Name == "memory_save"), "未安装时不提供记忆工具");

    Directory.CreateDirectory(context.PackageDirectory);
    File.WriteAllText(Path.Combine(context.PackageDirectory, "manifest.json"), "{\"id\":\"tool.memory\"}");
    store.Install(MemoryNotebook.PluginId);
    store.SetEnabled(MemoryNotebook.PluginId, true);
    Check(PluginSystemPromptComposer.Compose(store)?.Contains("记忆插件测试条目") == true, "启用后注入记忆");
    Check(registry.ActiveRegistrations.Any(r => r.Name == "memory_save") && registry.ActiveRegistrations.Any(r => r.Name == "memory_forget"),
        "启用后提供 memory_save / memory_forget");

    store.SetEnabled(MemoryNotebook.PluginId, false);
    Check(PluginSystemPromptComposer.Compose(store)?.Contains("记忆插件测试条目") != true, "停用后下一次组装即不注入");
    var blocked = await registry.DispatchAsync(new PureToolCall("c", "memory_save", "{\"text\":\"x\"}"));
    Check(!blocked.Success && blocked.Content.Contains("停用"), "停用后工具调用给出真实原因");

    store.SetEnabled(MemoryNotebook.PluginId, true);
    await package.ShutdownAsync();
    Check(PluginSystemPromptComposer.Compose(store)?.Contains("记忆插件测试条目") != true, "插件关闭后撤回注入");
    Check(File.Exists(new MemoryNotebook(appRoot).FilePath), "插件关闭不删除记忆文件");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine(failures == 0 ? $"全部通过：{total} 项断言" : $"失败 {failures} / {total}");
return failures == 0 ? 0 : 1;
