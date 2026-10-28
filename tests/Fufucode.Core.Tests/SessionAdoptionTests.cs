using Fufucode.Core;
using Fufucode.Core.Backend;

/// <summary>
/// 会话目录收养：项目 id 是随机 GUID，索引丢行会让完好的历史永久打不开。
/// 这些用例覆盖"能救"与"不能猜"两种边界。
/// </summary>
internal static class SessionAdoptionTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static string Scratch(string root, string tag) =>
        Path.Combine(root, "tmp", "adopt-" + tag + "-" + Guid.NewGuid().ToString("N"));

    private static void WriteSession(AppDataLayout layout, string projectId, string sessionId,
        params (int Role, string Content)[] messages)
    {
        var directory = Path.Combine(layout.ProjectSessionsDirectory(projectId), "fumo");
        Directory.CreateDirectory(directory);
        var body = string.Join(",", messages.Select(m =>
            "{\"Role\":" + m.Role + ",\"Content\":" + System.Text.Json.JsonSerializer.Serialize(m.Content) + "}"));
        File.WriteAllText(Path.Combine(directory, sessionId + ".json"),
            "{\"version\":2,\"messages\":[" + body + "],\"modelId\":\"m\",\"thinkingLevel\":\"off\",\"executionMode\":\"manual\"}");
    }

    public static void Run(string root)
    {
        // ── 1) 目录有标记、索引没有行：项目与会话都该被收养回来 ──
        var one = Scratch(root, "basic");
        var layoutOne = new AppDataLayout(one);
        var projectPathOne = Path.Combine(one, "project-a");
        Directory.CreateDirectory(projectPathOne);
        const string projectIdOne = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        WriteSession(layoutOne, projectIdOne, "s1", (1, "请在 src/graph 下实现图引擎，禁止第三方库"));
        SessionAdoption.WriteMarker(layoutOne, projectIdOne, projectPathOne);

        var metadataOne = new DesktopMetadataStore(layoutOne);
        var resultOne = SessionAdoption.Reconcile(layoutOne, metadataOne);
        Check(resultOne.ProjectsAdopted == 1 && resultOne.SessionsAdopted == 1,
            "缺失的项目与会话被收养回索引");
        var projectOne = metadataOne.GetProject(projectIdOne);
        Check(string.Equals(projectOne.Path, Path.GetFullPath(projectPathOne), StringComparison.OrdinalIgnoreCase),
            "收养后的项目路径与目录标记一致");
        var listedOne = metadataOne.ListSessions(projectOne.Id).ToList();
        Check(listedOne.Count == 1 && listedOne[0].Name == "请在 src/graph 下实现图引擎，禁止第三方库",
            "会话名取自首条用户消息，不用手写 SQL 也能在侧栏认出它");

        // 幂等：再跑一次不产生新行、也不搬文件
        var again = SessionAdoption.Reconcile(layoutOne, metadataOne);
        Check(!again.Changed, "重复收养是幂等的，不会重复建行或搬移文件");

        // ── 2) 同一路径已登记在另一个 id 下：文件必须搬过去，否则读取路径推不出来 ──
        var two = Scratch(root, "relocate");
        var layoutTwo = new AppDataLayout(two);
        var projectPathTwo = Path.Combine(two, "project-b");
        Directory.CreateDirectory(projectPathTwo);
        var metadataTwo = new DesktopMetadataStore(layoutTwo);
        var registered = metadataTwo.GetOrCreateProject(projectPathTwo);
        const string orphanId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        WriteSession(layoutTwo, orphanId, "orphan-session", (1, "把孤儿会话救回来"));
        SessionAdoption.WriteMarker(layoutTwo, orphanId, projectPathTwo);

        var resultTwo = SessionAdoption.Reconcile(layoutTwo, metadataTwo);
        Check(resultTwo.ProjectsAdopted == 0 && resultTwo.Relocated == 1 && resultTwo.SessionsAdopted == 1,
            "路径已有另一 id 时：不新建项目，搬文件并登记到正确 id");
        Check(File.Exists(Path.Combine(layoutTwo.ProjectSessionsDirectory(registered.Id), "fumo",
                "orphan-session.json")),
            "会话文件被搬到索引 id 目录下，读取路径可推导");
        Check(!Directory.Exists(layoutTwo.ProjectSessionsDirectory(orphanId)),
            "搬空后的孤儿目录被收掉，不再留第二次歧义");
        Check(metadataTwo.ListSessions(registered.Id).Any(s => s.Id == "orphan-session"),
            "收养的会话出现在正确项目下");

        // ── 3) 没有标记的目录：不猜路径 ──
        var three = Scratch(root, "nomarker");
        var layoutThree = new AppDataLayout(three);
        WriteSession(layoutThree, "cccccccccccccccccccccccccccccccc", "s3", (1, "无标记会话"));
        var metadataThree = new DesktopMetadataStore(layoutThree);
        var resultThree = SessionAdoption.Reconcile(layoutThree, metadataThree);
        Check(resultThree.ProjectsAdopted == 0 && resultThree.SessionsAdopted == 0 && resultThree.Unreadable == 0,
            "没有目录标记时不做猜测式收养");

        // ── 4) 标记指向已不存在的路径：拒绝收养，也不该抛异常 ──
        var four = Scratch(root, "gone");
        var layoutFour = new AppDataLayout(four);
        var missingProject = Path.Combine(four, "deleted-project");
        Directory.CreateDirectory(missingProject);
        const string projectIdFour = "dddddddddddddddddddddddddddddddd";
        WriteSession(layoutFour, projectIdFour, "s4", (1, "项目已删"));
        SessionAdoption.WriteMarker(layoutFour, projectIdFour, missingProject);
        Directory.Delete(missingProject);
        var metadataFour = new DesktopMetadataStore(layoutFour);
        var resultFour = SessionAdoption.Reconcile(layoutFour, metadataFour);
        Check(resultFour.Unreadable == 1 && resultFour.ProjectsAdopted == 0,
            "项目路径已不存在时标记为不可收养，而不是建一条打不开的索引");

        // ── 5) 损坏的会话文件不能被收养流程挪走 ──
        var five = Scratch(root, "corrupt");
        var layoutFive = new AppDataLayout(five);
        var projectPathFive = Path.Combine(five, "project-e");
        Directory.CreateDirectory(projectPathFive);
        const string projectIdFive = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var fumoFive = Path.Combine(layoutFive.ProjectSessionsDirectory(projectIdFive), "fumo");
        Directory.CreateDirectory(fumoFive);
        File.WriteAllText(Path.Combine(fumoFive, "broken.json"), "{ this is not json");
        SessionAdoption.WriteMarker(layoutFive, projectIdFive, projectPathFive);
        var metadataFive = new DesktopMetadataStore(layoutFive);
        SessionAdoption.Reconcile(layoutFive, metadataFive);
        Check(File.Exists(Path.Combine(fumoFive, "broken.json")),
            "损坏的会话文件留在原处（收养不得把用户唯一副本改名挪走）");

        // ── 6) 管理器构造时自动收养：应用重启即可在侧栏看到历史 ──
        var six = Scratch(root, "manager");
        var layoutSix = new AppDataLayout(six);
        var projectPathSix = Path.Combine(six, "project-f");
        Directory.CreateDirectory(projectPathSix);
        const string projectIdSix = "ffffffffffffffffffffffffffffffff";
        WriteSession(layoutSix, projectIdSix, "s6", (1, "重启后应当看得见"));
        SessionAdoption.WriteMarker(layoutSix, projectIdSix, projectPathSix);
        var managerSix = new PersistentSessionManager(string.Empty, string.Empty, layoutSix,
            requireApprovalBridge: false);
        Check(managerSix.Adopted.ProjectsAdopted == 1 && managerSix.Adopted.SessionsAdopted == 1,
            "构造期自动收养：不需要用户手动修索引");
        Check(managerSix.Metadata.ListSessions(projectIdSix)
                  .Any(s => s.Id == "s6"),
            "收养的会话可从正常的项目/会话查询路径列出");

        // ── 7) 已登记项目也要有标记：否则将来丢行时无从推断路径 ──
        var seven = Scratch(root, "backfill");
        var layoutSeven = new AppDataLayout(seven);
        var projectPathSeven = Path.Combine(seven, "project-g");
        Directory.CreateDirectory(projectPathSeven);
        var metadataSeven = new DesktopMetadataStore(layoutSeven);
        var registeredSeven = metadataSeven.GetOrCreateProject(projectPathSeven);
        Check(!File.Exists(SessionAdoption.MarkerPath(layoutSeven, registeredSeven.Id)),
            "新建项目时还没有目录标记（本用例的前提）");
        SessionAdoption.Reconcile(layoutSeven, metadataSeven);
        Check(SessionAdoption.MarkerPath(layoutSeven, registeredSeven.Id) is var marker &&
              File.Exists(marker) &&
              File.ReadAllText(marker).Contains("project-g", StringComparison.OrdinalIgnoreCase),
            "收养时把目录标记回填给已登记项目，丢行后才救得回来");
    }
}
