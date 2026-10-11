using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Fufucode.App.Features.Shortcuts;
using Avalonia.VisualTree;

namespace Fufucode.App.SelfTest;

/// <summary>
/// O1 快捷键模块的独立定向自测：手势解析、保留键规则、冲突检测、
/// 持久化往返、损坏回退与不覆盖、编辑/禁用/恢复、搜索/范围过滤、
/// 录制器（含输入法组合期间忽略）。不依赖真实内核或注册表接线。
/// 用真实 <see cref="ShortcutsPage"/> 无头构建，验证界面可渲染。
/// </summary>
public static class ShortcutManagerSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string label) { if (value) pass("O1 快捷键：" + label); else fail("O1 快捷键：" + label); }

        var root = Path.Combine(Path.GetTempPath(), "fufucode-o1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestGestureParsing(Check);
            TestReservedRules(Check);
            TestConflicts(Check);
            TestPersistence(root, Check);
            TestCorruptFileProtection(root, Check);
            TestManagerEditing(root, Check);
            TestSearchAndScopeFilter(root, Check);
            TestRecorder(Check);
            await TestPageRendersAsync(root, Check);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 证据目录可能仍被占用 */ }
        }
    }

    private static void TestGestureParsing(Action<bool, string> check)
    {
        check(ShortcutGesture.Parse("Ctrl+K")?.Text == "Ctrl+K", "解析 Ctrl+K");
        check(ShortcutGesture.Parse("ctrl + k")?.Text == "Ctrl+K", "大小写与空格归一");
        check(ShortcutGesture.Parse("Ctrl+Shift+P")?.Text == "Ctrl+Shift+P", "修饰键顺序固定为 Ctrl→Shift");
        check(ShortcutGesture.Parse("Shift+Ctrl+P")?.Text == "Ctrl+Shift+P", "修饰键顺序与输入顺序无关");
        check(ShortcutGesture.Parse("K") is null, "无修饰键的裸键拒绝解析");
        check(ShortcutGesture.Parse("Ctrl") is null, "只写修饰键拒绝解析");
        check(ShortcutGesture.Parse("Ctrl+Foo") is null, "未知主键拒绝解析");
        check(ShortcutGesture.Parse("") is null && ShortcutGesture.Parse(null) is null, "空输入返回 null");
        check(ShortcutGesture.Parse("Ctrl+K / Ctrl+Shift+P")?.Text == "Ctrl+K", "多绑定展示串只取第一段");

        var all = ShortcutGesture.ParseAll("Ctrl+K / Ctrl+Shift+P / Ctrl+K");
        check(all.Count == 2, "ParseAll 去重且保留两条");

        check(ShortcutGesture.Parse("Alt+Left")?.Key == Key.Left, "Alt+Left 解析出 Left 主键");
        check(ShortcutGesture.Parse("Ctrl+OemComma")?.Text == "Ctrl+OemComma", "Oem 键名可解析");
    }

    private static void TestReservedRules(Action<bool, string> check)
    {
        ShortcutGesture G(string text) => ShortcutGesture.Parse(text)!;

        check(ShortcutRules.Validate(G("Ctrl+K"), ShortcutScope.Global, false) is null, "全局 Ctrl+K 合法");
        check(ShortcutRules.Validate(G("Ctrl+Enter"), ShortcutScope.Global, false) is null,
            "带 Ctrl 的 Enter 合法（组合期由输入法守卫拦截，绑定本身不属保留）");
        check(ShortcutRules.Validate(G("Ctrl+Space"), ShortcutScope.Global, false) is null, "带 Ctrl 的 Space 合法");
        check(ShortcutRules.Validate(G("Ctrl+Up"), ShortcutScope.Terminal, false) is null, "终端范围带修饰键仍可绑定");
        check(ShortcutRules.Validate(ShortcutGesture.None, ShortcutScope.Global, false) is not null, "空手势被拒绝");
        check(ShortcutRules.Validate(G("Ctrl+A"), ShortcutScope.Global, false)?.IsReserved == true, "编辑语义键在全局范围被拒绝");
        check(ShortcutRules.Validate(G("Ctrl+A"), ShortcutScope.Editor, false) is null, "编辑语义键在文本编辑范围允许");
        check(ShortcutRules.Validate(G("Ctrl+Alt+Z"), ShortcutScope.Global, false) is null, "Ctrl+Alt+Z 非固定编辑语义，全局可绑定");
        check(ShortcutRules.Validate(G("Ctrl+Shift+Z"), ShortcutScope.Global, false) is null, "Ctrl+Shift+Z 非固定编辑语义，全局可绑定");
        check(ShortcutRules.Validate(G("Meta+L"), ShortcutScope.Global, false)?.IsReserved == true, "系统外壳组合被拒绝");
        check(ShortcutRules.Validate(G("Ctrl+K"), ShortcutScope.Terminal, true) is null, "终端输入动作的 Ctrl+K 通过保留键检查（非自有键）");
    }

    private static void TestConflicts(Action<bool, string> check)
    {
        var a = new ShortcutAction("a", "动作甲", "组一", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+K"));
        var b = new ShortcutAction("b", "动作乙", "组一", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+K"));
        var c = new ShortcutAction("c", "动作丙", "组一", ShortcutScope.Terminal, ShortcutGesture.ParseAll("Ctrl+K"));
        var d = new ShortcutAction("d", "动作丁", "组一", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+P"));

        var conflicts = ShortcutConflicts.Detect([(a, a.DefaultGestures), (b, b.DefaultGestures), (c, c.DefaultGestures), (d, d.DefaultGestures)]);
        check(conflicts.Count == 1, "只报告 1 处冲突（终端范围不算）");
        check(conflicts[0].ActionIds.Count == 2, "冲突列出双方动作");
        check(conflicts[0].ActionIds.Contains("a") && conflicts[0].ActionIds.Contains("b"), "冲突包含甲与乙");
        check(conflicts[0].GestureText == "Ctrl+K", "冲突手势正确");

        var none = ShortcutConflicts.Detect([(d, d.DefaultGestures)]);
        check(none.Count == 0, "单一动作无冲突");

        var forCandidate = ShortcutConflicts.DetectFor(
            [(a, a.DefaultGestures), (b, b.DefaultGestures)], ShortcutGesture.Parse("Ctrl+K")!, ShortcutScope.Global, "a");
        check(forCandidate.Count == 1 && forCandidate[0].ActionIds.Single() == "b", "改绑检测排除自身并给出对手");
        check(forCandidate[0].Scope == ShortcutScope.Global, "候选冲突带正确范围");

        // R-028：候选冲突与全表冲突必须使用同一范围规则。
        var crossScope = ShortcutConflicts.DetectFor(
            [(c, c.DefaultGestures)], ShortcutGesture.Parse("Ctrl+K")!, ShortcutScope.Chat, "x");
        check(crossScope.Count == 0, "跨范围候选不误报冲突（Chat 候选 vs Terminal 绑定）");

        var sameScope = ShortcutConflicts.DetectFor(
            [(c, c.DefaultGestures)], ShortcutGesture.Parse("Ctrl+K")!, ShortcutScope.Terminal, "x");
        check(sameScope.Count == 1 && sameScope[0].ActionIds.Single() == "c", "同范围候选准确列出对手");

        var detectVsCandidate = ShortcutConflicts.Detect([(c, c.DefaultGestures)]);
        check(detectVsCandidate.Count == 0 && crossScope.Count == 0, "全表与候选对同一跨范围组合结论一致");
    }

    private static void TestPersistence(string root, Action<bool, string> check)
    {
        var storeRoot = Path.Combine(root, "persist");
        var store = new ShortcutBindingsStore(storeRoot);
        check(store.LoadError is null && store.Writable, "首次加载无错误且可写");
        check(store.Current.Bindings.Count == 0, "无文件时为默认空绑定");

        store.Save(new Dictionary<string, string[]> { ["commands"] = ["Ctrl+Alt+K"], ["files"] = [] });
        check(File.Exists(Path.Combine(storeRoot, ShortcutBindingsStore.FileName)), "写入 keybindings.json");

        var reloaded = new ShortcutBindingsStore(storeRoot);
        check(reloaded.LoadError is null, "重新加载无错误");
        check(reloaded.Current.Bindings["commands"].Single() == "Ctrl+Alt+K", "自定义绑定往返保留");
        check(reloaded.Current.Bindings["files"].Length == 0, "禁用（空数组）往返保留");
        check(reloaded.Current.Version == ShortcutBindingsDocument.CurrentVersion, "写入版本为当前版本");
    }

    private static void TestCorruptFileProtection(string root, Action<bool, string> check)
    {
        var storeRoot = Path.Combine(root, "corrupt");
        Directory.CreateDirectory(storeRoot);
        var path = Path.Combine(storeRoot, ShortcutBindingsStore.FileName);
        File.WriteAllText(path, "{ this is not json");

        var store = new ShortcutBindingsStore(storeRoot);
        check(store.LoadError is not null, "损坏文件产生可见加载错误");
        check(!store.Writable, "损坏文件标记为不可写");
        check(store.Current.Bindings.Count == 0, "损坏时回退默认绑定");

        var threw = false;
        try { store.Save(new Dictionary<string, string[]> { ["commands"] = ["Ctrl+K"] }); }
        catch (InvalidOperationException) { threw = true; }
        check(threw, "损坏时保存被拒绝");
        check(File.ReadAllText(path) == "{ this is not json", "损坏原文件未被覆盖");

        // 版本不支持同样拒绝。
        File.WriteAllText(path, "{\"version\":99,\"bindings\":{}}");
        var unsupported = new ShortcutBindingsStore(storeRoot);
        check(unsupported.LoadError is not null && !unsupported.Writable, "未知版本拒绝加载且不可写");
        check(File.ReadAllText(path).Contains("99"), "未知版本原文件未被覆盖");

        // R-026：各类错误结构必须可见回退、拒绝覆盖，且原文件字节不变。
        void RejectsStructure(string json, string label)
        {
            var root2 = Path.Combine(root, "corrupt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root2);
            var file = Path.Combine(root2, ShortcutBindingsStore.FileName);
            File.WriteAllText(file, json);
            var bytesBefore = File.ReadAllBytes(file);
            var s = new ShortcutBindingsStore(root2);
            var loadRejected = s.LoadError is not null && !s.Writable && s.Current.Bindings.Count == 0;
            var saveRejected = false;
            try { s.Save(new Dictionary<string, string[]> { ["commands"] = ["Ctrl+K"] }); }
            catch (InvalidOperationException) { saveRejected = true; }
            var unchanged = File.ReadAllBytes(file).SequenceEqual(bytesBefore);
            check(loadRejected && saveRejected && unchanged, label);
        }

        RejectsStructure("{\"version\":1,\"bindings\":[]}", "bindings 为数组：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":\"nope\"}", "bindings 为字符串：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":null}", "bindings 为 null：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"bindings\":{}}", "缺少 version：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":\"1\",\"bindings\":{}}", "version 为字符串：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":true,\"bindings\":{}}", "version 为布尔：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":{\"commands\":\"Ctrl+K\"}}", "动作绑定为字符串：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":{\"commands\":[123]}}", "绑定条目非字符串：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":{\"commands\":[\"\"]}}", "绑定条目为空白：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":{\"commands\":[\"Ctrl+NotAKey\"]}}", "绑定条目为非法手势：可见回退、拒绝覆盖、字节不变");
        RejectsStructure("{\"version\":1,\"bindings\":{\"commands\":[\"Ctrl\"]}}", "绑定条目为纯修饰键：可见回退、拒绝覆盖、字节不变");

        // 合法配置能力不退化：自定义、禁用（空数组）与未知动作 ID 均保留。
        var legalRoot = Path.Combine(root, "legal");
        Directory.CreateDirectory(legalRoot);
        var legalFile = Path.Combine(legalRoot, ShortcutBindingsStore.FileName);
        File.WriteAllText(legalFile,
            "{\"version\":1,\"bindings\":{\"commands\":[\"Ctrl+Alt+K\"],\"files\":[],\"future.action\":[\"Ctrl+Alt+Q\"]}}");
        var legal = new ShortcutBindingsStore(legalRoot);
        check(legal.LoadError is null && legal.Writable, "合法配置正常加载");
        check(legal.Current.Bindings["commands"].Single() == "Ctrl+Alt+K", "合法自定义保留");
        check(legal.Current.Bindings["files"].Length == 0, "合法禁用（空数组）保留");
        check(legal.Current.Bindings["future.action"].Single() == "Ctrl+Alt+Q", "未知动作 ID 保留");
        legal.Save(legal.Current);
        check(new ShortcutBindingsStore(legalRoot).Current.Bindings.Count == 3, "合法配置可写回且条目不丢失");

        // 内存绑定不可解析时拒绝写盘，不产出损坏文件。
        var badRoot = Path.Combine(root, "bad-write");
        var badStore = new ShortcutBindingsStore(badRoot);
        var badRejected = false;
        try { badStore.Save(new Dictionary<string, string[]> { ["commands"] = ["NotAGesture"] }); }
        catch (InvalidOperationException) { badRejected = true; }
        check(badRejected && !File.Exists(Path.Combine(badRoot, ShortcutBindingsStore.FileName)),
            "不可解析的内存绑定被拒绝写盘");
    }

    private static void TestManagerEditing(string root, Action<bool, string> check)
    {
        var storeRoot = Path.Combine(root, "manager");
        var store = new ShortcutBindingsStore(storeRoot);

        var vm = new ShortcutManagerViewModel(() => store);
        vm.SetActions(Catalog(), id => id == "unavailable.action" ? "会话尚未就绪" : null);

        check(vm.Rows.Count == Catalog().Count, "动作目录全部进入列表");
        var commands = vm.Rows.Single(row => row.Id == "commands");
        check(commands.EffectiveText == "Ctrl+K / Ctrl+Shift+P", "默认绑定从默认表读入");
        check(commands.IsDefault && commands.OriginText == "默认", "初始来源为默认");
        check(!vm.IsModified, "初始无未保存改动");

        vm.SetBinding("files", "Ctrl+Alt+F");
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+Alt+F", "改绑立即生效");
        check(vm.IsModified, "改绑标记为未保存");
        check(vm.Save(), "保存成功");
        check(!vm.IsModified, "保存后改动状态清零");
        check(new ShortcutBindingsStore(storeRoot).Current.Bindings["files"].Single() == "Ctrl+Alt+F", "改绑已落盘");

        // 冲突仍然允许保存，但界面必须列出双方。
        vm.SetBinding("commands", "Ctrl+Alt+F");
        check(vm.HasConflicts, "同范围同手势产生冲突");
        var conflict = vm.Conflicts.Single();
        check(conflict.ActionTitles.Contains("绑定测试命令面板") && conflict.ActionTitles.Contains("绑定测试文件"), "冲突双方标题都列出");
        check(vm.Conflicts.Single().ActionTitles.Split('、').Length == 2, "冲突同时列出双方");
        check(vm.Rows.Single(row => row.Id == "commands").HasConflict, "冲突标记到命令面板行");
        check(vm.Rows.Single(row => row.Id == "files").HasConflict, "冲突标记到双方行");

        // R-028 复现场景：Terminal 已用 Ctrl+K，Chat 改绑 Ctrl+K 必须不报冲突，
        // 且 Status、Conflicts、行标记三处结论一致。
        var crossRoot = Path.Combine(root, "cross-scope");
        var crossStore = new ShortcutBindingsStore(crossRoot);
        var cross = new ShortcutManagerViewModel(() => crossStore);
        cross.SetActions(Catalog());
        cross.SetBinding("terminal", "Ctrl+K");
        cross.SetBinding("edit.copy", "Ctrl+K");
        var terminalRow = cross.Rows.Single(row => row.Id == "terminal");
        var chatRow = cross.Rows.Single(row => row.Id == "edit.copy");
        check(!cross.HasConflicts && cross.Conflicts.Count == 0, "跨范围绑定不产生冲突列表");
        check(!terminalRow.HasConflict && !chatRow.HasConflict, "跨范围绑定不标记行冲突");
        check(!cross.Status.Contains("冲突"), "跨范围改绑时状态提示不误报冲突");

        // 同范围改绑：三处必须一致地报冲突并列出双方。
        cross.SetBinding("files", "Ctrl+K");
        check(cross.HasConflicts && cross.Conflicts.Count == 1, "同范围改绑产生冲突列表");
        check(cross.Status.Contains("冲突"), "同范围改绑状态提示报冲突");
        check(cross.Rows.Single(row => row.Id == "files").HasConflict &&
              cross.Rows.Single(row => row.Id == "terminal") is { } t2 && !t2.HasConflict,
            "同范围冲突只标记实际双方范围");
        check(cross.Conflicts.Single().ActionCount == 2, "冲突恰好列出双方");

        // 保留键被拒绝并给出原因。
        var before = vm.Rows.Single(row => row.Id == "files").EffectiveText;
        vm.SetBinding("files", "Ctrl+A");
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText == before, "保留键绑定被拒绝，绑定不变");
        check(vm.Status.Contains("文本编辑"), "保留键拒绝给出可见原因");

        vm.SetBinding("files", "NotAKey");
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText == before, "非法组合被拒绝");

        // 禁用与恢复。
        vm.SetBinding("files", "");
        check(vm.Rows.Single(row => row.Id == "files").IsDisabled, "空绑定即禁用");
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText.Length == 0, "禁用后无生效绑定");
        vm.ResetAction("files");
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+P", "逐项恢复回到默认");
        check(vm.Rows.Single(row => row.Id == "files").IsDefault, "恢复后来源为默认");

        // 不可用原因来自注入的注册表投影。
        check(vm.Rows.Single(row => row.Id == "unavailable.action").UnavailableReason == "会话尚未就绪", "不可用原因来自动作可用性");

        // 全部恢复：清空自定义（含已保存的），保存前处于未保存状态。
        vm.SetBinding("commands", "Ctrl+Alt+C");
        vm.SetBinding("files", "Ctrl+Alt+D");
        vm.Save();
        vm.ResetAll();
        check(vm.Rows.All(row => !row.IsDisabled), "全部恢复后无禁用项");
        check(vm.Rows.Single(row => row.Id == "commands").EffectiveText == "Ctrl+K / Ctrl+Shift+P", "全部恢复回到默认绑定");
        check(!vm.Conflicts.Any(), "全部恢复后无冲突");
        check(vm.IsModified, "全部恢复需要保存才落盘");
        check(vm.Save() && new ShortcutBindingsStore(storeRoot).Current.Bindings.Count == 0, "全部恢复保存后清空磁盘自定义");

        // 放弃改动：回到磁盘内容。
        vm.SetBinding("files", "Ctrl+Alt+Z");
        vm.Save();
        vm.SetBinding("files", "Ctrl+Alt+Y");
        vm.Revert();
        check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+Alt+Z", "放弃改动回到磁盘内容");
    }
    private static void TestSearchAndScopeFilter(string root, Action<bool, string> check)
    {
        var store = new ShortcutBindingsStore(Path.Combine(root, "filter"));
        var vm = new ShortcutManagerViewModel(() => store);
        vm.SetActions(Catalog());

        var total = vm.Rows.Count;
        vm.SearchText = "终端";
        check(vm.Rows.Count == 1 && vm.Rows[0].Id == "terminal", "搜索按标题过滤只留匹配项");
        vm.SearchText = "Ctrl+K";
        check(vm.Rows.Any(row => row.Id == "commands"), "搜索命中当前绑定文本");
        vm.SearchText = "";
        check(vm.Rows.Count == total, "清空搜索恢复全部");

        vm.ScopeFilter = ShortcutScope.Terminal;
        check(vm.Rows.All(row => row.Action.Scope == ShortcutScope.Terminal), "范围过滤只留终端动作");
        vm.ScopeFilter = null;
        check(vm.Rows.Count == total, "清空范围过滤恢复全部");

        vm.SearchText = "绝不可能匹配的关键词";
        check(vm.Rows.Count == 0, "无匹配时列表为空");
    }

    private static void TestRecorder(Action<bool, string> check)
    {
        var recorder = new ShortcutRecorder();
        recorder.Start();
        check(recorder.IsRecording, "录制已启动");

        var modifierOnly = recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        check(modifierOnly.Outcome == ShortcutCaptureOutcome.ModifiersOnly, "单按修饰键等待主键");
        check(recorder.PendingModifiers == "Ctrl", "记录当前按住的修饰键");

        var captured = recorder.KeyDown(Key.K, KeyModifiers.Control, false);
        check(captured.Outcome == ShortcutCaptureOutcome.Captured, "修饰键+主键完成捕获");
        check(captured.Gesture?.Text == "Ctrl+K", "捕获手势文本正确");
        check(!recorder.IsRecording, "捕获后停止录制");

        // R-027：修饰键状态以当前实际按键为准，释放后不能形成幽灵组合。
        recorder.Start();
        recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        recorder.KeyUp(Key.LeftCtrl);
        check(recorder.PendingModifiers.Length == 0, "Ctrl 释放后当前修饰键为空");
        var bareK = recorder.KeyDown(Key.K, KeyModifiers.None, false);
        check(bareK.Gesture?.Text == "K", "Ctrl 释放后裸 K 不得录成 Ctrl+K");

        recorder.Start();
        recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        recorder.KeyDown(Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift, false);
        check(recorder.PendingModifiers == "Ctrl+Shift", "多修饰键按固定顺序记录");
        recorder.KeyUp(Key.LeftShift);
        check(recorder.PendingModifiers == "Ctrl", "释放其中一个修饰键后只留按住的");
        var ctrlK = recorder.KeyDown(Key.K, KeyModifiers.Control, false);
        check(ctrlK.Gesture?.Text == "Ctrl+K", "多修饰释放后按当前状态捕获");

        recorder.Start();
        recorder.KeyDown(Key.LeftAlt, KeyModifiers.Alt, false);
        recorder.KeyUp(Key.LeftAlt);
        var loneK = recorder.KeyDown(Key.K, KeyModifiers.None, false);
        check(loneK.Gesture?.Text == "K", "Alt 释放后裸 K 不得录成 Alt+K");

        // 重复按下同一修饰键不重复计数。
        recorder.Start();
        recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        check(recorder.PendingModifiers == "Ctrl", "重复按下同一修饰键不重复");
        recorder.Stop();

        // 窗口失活/页面离开：清掉过期按键状态但保持录制。
        recorder.Start();
        recorder.KeyDown(Key.LeftCtrl, KeyModifiers.Control, false);
        recorder.ReleaseAllHeld();
        check(recorder.IsRecording && recorder.PendingModifiers.Length == 0, "失活清理按键状态但保持录制");
        recorder.Stop();

        // 未开始录制时 KeyUp 不产生副作用。
        recorder.KeyUp(Key.LeftCtrl);
        check(!recorder.IsRecording && recorder.PendingModifiers.Length == 0, "未录制时 KeyUp 无副作用");

        recorder.Start();
        var composing = recorder.KeyDown(Key.Enter, KeyModifiers.None, imeComposing: true);
        check(composing.Outcome == ShortcutCaptureOutcome.IgnoredDuringComposition, "输入法组合期间按键被忽略");
        check(recorder.IsRecording, "组合期间的按键不会结束录制");

        var escape = recorder.KeyDown(Key.Escape, KeyModifiers.None, false);
        check(escape.Outcome == ShortcutCaptureOutcome.Cancelled && !recorder.IsRecording, "Esc 取消录制");
        check(recorder.PendingModifiers.Length == 0, "取消后不残留修饰键状态");

        recorder.Start();
        var altC = recorder.KeyDown(Key.LeftAlt, KeyModifiers.None, false);
        check(altC.Outcome == ShortcutCaptureOutcome.ModifiersOnly, "Alt 单按等待主键");
        var altLeft = recorder.KeyDown(Key.Left, KeyModifiers.Alt, false);
        check(altLeft.Gesture?.Text == "Alt+Left", "Alt+方向键规范化为 Alt+Left");
    }

    private static async Task TestPageRendersAsync(string root, Action<bool, string> check)
    {
        var store = new ShortcutBindingsStore(Path.Combine(root, "page"));
        var vm = new ShortcutManagerViewModel(() => store);
        vm.SetActions(Catalog());
        var page = new ShortcutsPage(vm);
        var window = new Window { Width = 900, Height = 700, Content = page };
        window.Show();
        try
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            check(page.Bounds.Width > 0, "快捷键管理页可无头渲染");
            check(page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "键盘快捷键"), "页面标题渲染");
            check(page.GetVisualDescendants().OfType<Button>().Any(b => (b.Content as string) == "全部恢复默认"), "恢复默认入口存在");

            vm.SearchText = "Ctrl+J";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            check(vm.Rows.Count == 1 && vm.Rows[0].Id == "toggle.bottom", "页面内搜索命中 Ctrl+J 绑定");

            var statusBefore = vm.Status;
            check(statusBefore.Length > 0, "状态文本可读");

            // 真实页面事件路由：通过控件树上的隧道处理器走一遍录制，验证释放/IME/取消行为。
            vm.SearchText = "";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();

            page.BeginRecordingForTesting("files");
            check(page.RecordingActionForTesting == "files", "页面进入录制状态");
            var beforeBinding = vm.Rows.Single(row => row.Id == "files").EffectiveText;

            // Ctrl 按下后释放，再按裸 K：必须捕获失败并保持录制（裸键被规则拒绝）。
            page.SimulateRecordingKeyForTesting(Key.LeftCtrl, KeyModifiers.Control, composing: false);
            page.SimulateRecordingKeyForTesting(Key.LeftCtrl, KeyModifiers.None, composing: false, isKeyUp: true);
            check(page.PendingModifiersForTesting.Length == 0, "页面路由：Ctrl 释放后当前修饰键为空");
            page.SimulateRecordingKeyForTesting(Key.K, KeyModifiers.None, composing: false);
            check(vm.Rows.Single(row => row.Id == "files").EffectiveText == beforeBinding,
                "页面路由：Ctrl 释放后裸 K 不改变绑定");

            // 组合期间的按键被忽略且不结束录制。
            page.BeginRecordingForTesting("files");
            page.SimulateRecordingKeyForTesting(Key.Enter, KeyModifiers.None, composing: true);
            check(page.RecordingActionForTesting == "files", "页面路由：IME 组合期间保持录制");
            check(vm.Rows.Single(row => row.Id == "files").EffectiveText == beforeBinding,
                "页面路由：IME 组合期间不落定绑定");

            // 正常组合仍可捕获并落定。
            page.SimulateRecordingKeyForTesting(Key.LeftCtrl, KeyModifiers.Control, composing: false);
            page.SimulateRecordingKeyForTesting(Key.K, KeyModifiers.Control, composing: false);
            check(page.RecordingActionForTesting is null, "页面路由：捕获后结束录制");
            check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+K",
                "页面路由：正常 Ctrl+K 落定到动作");

            // Esc 取消不改变绑定。
            page.BeginRecordingForTesting("files");
            page.SimulateRecordingKeyForTesting(Key.Escape, KeyModifiers.None, composing: false);
            check(page.RecordingActionForTesting is null, "页面路由：Esc 结束录制");
            check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+K", "页面路由：Esc 不改变绑定");

            // 失活清理过期修饰键状态。
            page.BeginRecordingForTesting("files");
            page.SimulateRecordingKeyForTesting(Key.LeftAlt, KeyModifiers.Alt, composing: false);
            page.SimulateDeactivationForTesting();
            check(page.PendingModifiersForTesting.Length == 0, "页面路由：失活清理修饰键状态");
            check(page.RecordingActionForTesting == "files", "页面路由：失活不结束录制");
            page.SimulateRecordingKeyForTesting(Key.K, KeyModifiers.None, composing: false);
            check(vm.Rows.Single(row => row.Id == "files").EffectiveText == "Ctrl+K",
                "页面路由：失活后裸 K 不落定绑定");
            page.CancelRecordingForTesting();
        }
        finally
        {
            window.Close();
            await Task.Delay(60);
        }
    }

    private static IReadOnlyList<ShortcutAction> Catalog() =>
    [
        new("commands", "绑定测试命令面板", "全局", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+K / Ctrl+Shift+P"), "搜索应用动作", "全局"),
        new("files", "绑定测试文件", "工作区", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+P"), "浏览项目文件", "工作区"),
        new("toggle.bottom", "绑定测试底部面板", "布局", ShortcutScope.Global, ShortcutGesture.ParseAll("Ctrl+J"), null, "布局"),
        new("navigate.back", "绑定测试返回", "导航", ShortcutScope.Global, ShortcutGesture.ParseAll("Alt+Left"), null, "导航"),
        new("terminal", "绑定测试终端", "工作区", ShortcutScope.Terminal, ShortcutGesture.ParseAll("Ctrl+Shift+T"), null, "工作区"),
        new("edit.copy", "绑定测试复制", "编辑", ShortcutScope.Editor, ShortcutGesture.ParseAll("Ctrl+C"), null, "编辑"),
        new("unavailable.action", "绑定测试不可用项", "会话", ShortcutScope.Chat, [], null, "会话"),
        new("nobinding", "绑定测试无默认绑定", "窗口", ShortcutScope.Global, [], null, "窗口"),
    ];
}
