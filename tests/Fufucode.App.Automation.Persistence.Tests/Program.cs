using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using Fufucode.App.Features.Automation.Persistence;

// C5-PERSIST 定向验证（独立无云 console 工程，未接全量 runner / sln；公共接线归主控 M）。
// 运行：dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests
// 全部使用真实临时文件，不调用任何模型、不连接 Core 库。

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var work = Path.Combine(repoRoot, "tmp", "c5-persistence-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
Directory.CreateDirectory(work);
var passed = 0;
var skipped = new List<string>();

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

void Skip(string name)
{
    skipped.Add(name);
    Console.WriteLine("SKIP " + name);
}

bool CheckCode<T>(AutomationPersistenceResult<T> result, AutomationPersistenceErrorCode expected, string name)
{
    if (result.IsSuccess) throw new Exception("FAIL " + name + "（预期失败，实际成功）");
    if (result.Failure?.Code != expected) throw new Exception("FAIL " + name + "（预期 " + expected + "，实际 " + result.Failure?.Code + " · " + result.Failure?.Message + "）");
    passed++;
    Console.WriteLine("PASS " + name + " · " + expected + " · " + result.Failure?.Message);
    return true;
}

static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

static byte[] Bytes(string path) => File.ReadAllBytes(path);

static bool SameBytes(string path, byte[] expected) => Bytes(path).SequenceEqual(expected);

AutomationPersistenceScope Scope(string projectKey, string dbPath, string authority)
{
    var created = AutomationPersistenceScope.TryCreate(projectKey, dbPath, authority);
    if (!created.IsSuccess) throw new Exception("scope 构造失败：" + created.Failure);
    return created.ValueOrThrow();
}

AutomationDraftEntry Draft(string draftId, string todoId, string contextJson, string evidenceToken,
    string reason = "先按现有依据打回", string response = "", long? version = 7) => new()
{
    DraftId = draftId,
    TodoId = todoId,
    TodoKind = "review",
    TodoVersion = version,
    ContextJson = contextJson,
    ContextSha256 = Sha(contextJson),
    EvidenceToken = evidenceToken,
    EvidenceTokenSha256 = Sha(evidenceToken),
    Reason = reason,
    Evidence = "dotnet test 3 项失败",
    Response = response,
    SelectedCandidateIds = new[] { "cand-2", "cand-1" },
};

AutomationRequestRecord Request(string commandId, string payloadJson) => new()
{
    CommandId = commandId,
    Kind = AutomationRequestKind.Review,
    PayloadJson = payloadJson,
    PayloadSha256 = Sha(payloadJson),
    CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000,
    State = AutomationRequestState.PendingUnknown,
};

string StoreFile(string root, AutomationPersistenceScope scope) =>
    Path.Combine(root, AutomationPersistenceScope.StoreDirectoryName, scope.ScopeHash + ".json");
// 写入门（C5-PERSIST-GENERATION-1）：所有写入必须携带预期 generation。
// Gated* 只演示 M 接线的推荐次序「先 ReadGeneration 再写」，默认仍走显式预期值；
// 传 null 的用例是故意的盲写反例，必须被拒。
AutomationPersistenceResult<AutomationPersistenceWriteResult> GatedSave(AutomationDraftStore store, AutomationPersistenceScope scope, AutomationDraftEntry draft, long? expected = null)
    => store.Save(scope, draft, expected ?? store.ReadGeneration(scope).ValueOrThrow());

AutomationPersistenceResult<AutomationPersistenceWriteResult> GatedRemove(AutomationDraftStore store, AutomationPersistenceScope scope, string draftId, long? expected = null)
    => store.Remove(scope, draftId, expected ?? store.ReadGeneration(scope).ValueOrThrow());

AutomationPersistenceResult<AutomationPersistenceWriteResult> GatedRecord(AutomationRequestJournal journal, AutomationPersistenceScope scope, AutomationRequestRecord request, long? expected = null)
    => journal.Record(scope, request, expected ?? journal.ReadGeneration(scope).ValueOrThrow());

AutomationPersistenceResult<AutomationPersistenceWriteResult> GatedObserve(AutomationRequestJournal journal, AutomationPersistenceScope scope, string commandId, string receiptJson, long? expected = null)
    => journal.ObserveReceipt(scope, commandId, receiptJson, expected ?? journal.ReadGeneration(scope).ValueOrThrow());


/// <summary>mklink /J 建 junction（与 O2 存储测试同样的做法，无需管理员权限）。</summary>
bool MakeJunction(string linkPath, string targetPath)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }
    catch (Exception error)
    {
        Console.WriteLine("junction 创建异常：" + error.Message);
        return false;
    }
}

Console.WriteLine("工作目录：" + work);

try
{
    // ── A. scope 绑定与输入校验（无磁盘副作用）──────────────────────────
    var rootA = Path.Combine(work, "root-a");
    var dbPathA = Path.Combine(work, "wb-a", "workbench.db");
    var scopeA = Scope("proj-alpha", dbPathA, "authority-1");

    CheckCode(AutomationPersistenceScope.TryCreate("", dbPathA, "authority-1"),
        AutomationPersistenceErrorCode.InvalidArgument, "空 projectKey 被拒绝");
    CheckCode(AutomationPersistenceScope.TryCreate("proj", dbPathA, ""),
        AutomationPersistenceErrorCode.InvalidArgument, "空 authorityIdentity 被拒绝（身份由 M 提供，不默认）");
    CheckCode(AutomationPersistenceScope.TryCreate("proj", "relative/workbench.db", "authority-1"),
        AutomationPersistenceErrorCode.InvalidArgument, "相对数据库路径被拒绝");
    Check(!Directory.Exists(rootA), "仅做 scope 校验不创建任何目录");

    var escapeScope = Scope("../../evil", dbPathA, "authority-1");
    var escapeFile = StoreFile(rootA, escapeScope);
    Check(!escapeFile.Contains("evil"), "用户 projectKey 不参与路径拼接（文件名只用哈希）");
    Check(escapeFile.StartsWith(Path.Combine(rootA, AutomationPersistenceScope.StoreDirectoryName) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && Path.GetFileName(escapeFile) == escapeScope.ScopeHash + ".json",
        "含 .. 的 projectKey 仍落在固定目录内，文件名只是哈希");

    // ── B. 两个实例/重启读同 scope 草稿与精确不可变请求 ──────────────────
    var draftsB1 = new AutomationDraftStore(rootA);
    var journalB1 = new AutomationRequestJournal(rootA);
    var contextJson = "{\"TaskId\":\"t-1\",\"part\":{\"PartId\":\"p-1\",\"RunId\":\"r-1\",\"HeadSha\":\"abc123\"}}";
    var payload = "{\"CommandId\":\"cmd-1\",\"ExpectedRevision\":12,\"TaskId\":\"t-1\",\"PartId\":\"p-1\",\"Decision\":\"Rework\",\"Findings\":\"缺回归证据\"}";

    var save1 = GatedSave(draftsB1, scopeA, Draft("d-1", "todo-1", contextJson, "ev-token-1"));
    Check(save1.IsSuccess && save1.ValueOrThrow().Generation == 1, "首次保存草稿 generation=1");

    var draftsB2 = new AutomationDraftStore(rootA);
    var journalB2 = new AutomationRequestJournal(rootA);
    var reload = draftsB2.Load(scopeA);
    Check(reload.IsSuccess && reload.ValueOrThrow().Count == 1, "第二个实例读到同一草稿（条目数一致）");
    var restored = reload.ValueOrThrow()[0];
    Check(restored.DraftId == "d-1" && restored.ContextJson == contextJson && restored.EvidenceToken == "ev-token-1"
        && restored.Reason == "先按现有依据打回" && restored.Evidence == "dotnet test 3 项失败" && restored.Response == ""
        && restored.SelectedCandidateIds.SequenceEqual(new[] { "cand-2", "cand-1" }) && restored.TodoVersion == 7,
        "草稿 context/evidenceToken/reason/evidence/response/候选ID/version 原样恢复");

    var record1 = GatedRecord(journalB1, scopeA, Request("cmd-1", payload));
    Check(record1.IsSuccess && record1.ValueOrThrow().Generation == 2, "记录请求 generation=2");
    var crossRead = journalB2.Load(scopeA);
    var crossRow = crossRead.ValueOrThrow().Single();
    Check(crossRow.PayloadJson == payload && crossRow.PayloadSha256 == Sha(payload)
        && crossRow.State == AutomationRequestState.PendingUnknown,
        "第二实例读到精确不可变请求（原文与 SHA-256 相符）");
    Check(draftsB2.Load(scopeA).ValueOrThrow().Count == 1, "写请求没有丢草稿（同 scope 单文件互不覆盖）");

    // ── C. 项目 key / 稳定身份隔离 + Windows 路径等价 ────────────────────
    var otherProject = Scope("proj-beta", dbPathA, "authority-1");
    var otherAuthority = Scope("proj-alpha", dbPathA, "authority-2");
    Check(!otherProject.SameTargetAs(scopeA) && !otherAuthority.SameTargetAs(scopeA),
        "同库路径但项目 key/稳定身份不同 → 不同 scope");
    Check(draftsB2.Load(otherProject).ValueOrThrow().Count == 0 && journalB2.Load(otherAuthority).ValueOrThrow().Count == 0,
        "不同项目/身份各自读到空（文件互相隔离）");

    var upperScope = Scope("proj-alpha", dbPathA.ToUpperInvariant(), "authority-1");
    var mixedScope = Scope("proj-alpha", dbPathA.Replace(Path.DirectorySeparatorChar, '/'), "authority-1");
    Check(upperScope.SameTargetAs(scopeA) && mixedScope.SameTargetAs(scopeA),
        "Windows 同一路径的大小写/斜杠差异归同 scope");
    Check(upperScope.ScopeHash == scopeA.ScopeHash && mixedScope.ScopeHash == scopeA.ScopeHash,
        "大小写/斜杠差异映射到同一文件名");
    Check(draftsB2.Load(upperScope).ValueOrThrow().Count == 1, "用大写路径重新打开仍拿到原草稿");

    // ── D. 重复 ID 输入保真 ─────────────────────────────────────────────
    var scopeFileB = StoreFile(rootA, scopeA);
    var bytesBeforeD = Bytes(scopeFileB);
    var sameIdSameInput = GatedSave(draftsB2, scopeA, Draft("d-1", "todo-1", contextJson, "ev-token-1"));
    Check(sameIdSameInput.IsSuccess && sameIdSameInput.ValueOrThrow().Generation == 2 && SameBytes(scopeFileB, bytesBeforeD),
        "同 draftId 同输入幂等：不推进 generation、文件字节完全不变");
    var sameIdSameRequest = GatedRecord(journalB2, scopeA, Request("cmd-1", payload));
    Check(sameIdSameRequest.IsSuccess && sameIdSameRequest.ValueOrThrow().Generation == 2,
        "同 commandId 同 payload 幂等，不重复落盘");

    var rewrite = GatedSave(draftsB2, scopeA, Draft("d-1", "todo-1", contextJson, "ev-token-1", reason: "改成接受"));
    Check(rewrite.IsSuccess && rewrite.ValueOrThrow().Generation == 3, "同 draftId 不同内容视为用户改写，generation+1");
    var afterRewrite = draftsB2.Load(scopeA).ValueOrThrow();
    Check(afterRewrite.Count == 1 && afterRewrite[0].Reason == "改成接受", "改写只影响该条草稿，条目数不变");

    var bytesBeforeDupReq = Bytes(scopeFileB);
    CheckCode(GatedRecord(journalB2, scopeA, Request("cmd-1", "{\"CommandId\":\"cmd-1\",\"Decision\":\"Accept\"}")),
        AutomationPersistenceErrorCode.DuplicateIdentity, "同 commandId 不同 payload 被拒绝");
    Check(SameBytes(scopeFileB, bytesBeforeDupReq), "被拒绝的请求没有改写文件原字节");

    var secondDraft = GatedSave(draftsB2, scopeA, Draft("d-2", "todo-2", "{\"TaskId\":\"t-2\"}", "ev-token-2"));
    Check(secondDraft.IsSuccess && draftsB2.Load(scopeA).ValueOrThrow().Count == 2, "不同 draftId 各自独立保存");

    // ── E. 同 generation 竞争：仅一方成功且无丢失 ────────────────────────
    var rootE = Path.Combine(work, "root-e");
    var scopeE = Scope("proj-e", Path.Combine(work, "wb-e", "workbench.db"), "authority-e");
    var draftsE1 = new AutomationDraftStore(rootE);
    var draftsE2 = new AutomationDraftStore(rootE);
    var journalE = new AutomationRequestJournal(rootE);
    Check(draftsE1.Save(scopeE, Draft("d-1", "todo-1", contextJson, "ev-token-1"), expectedGeneration: 0).IsSuccess,
        "实例1 以预期 generation 0 写入成功");
    CheckCode(draftsE2.Save(scopeE, Draft("d-9", "todo-9", "{\"TaskId\":\"t-9\"}", "ev-token-9"), expectedGeneration: 0),
        AutomationPersistenceErrorCode.GenerationConflict, "实例2 同预期 generation 竞争失败并明示冲突（不是 last-writer-wins）");
    var afterRace = draftsE2.Load(scopeE);
    Check(afterRace.ValueOrThrow().Count == 1 && afterRace.ValueOrThrow()[0].DraftId == "d-1",
        "竞争失败没有丢稿：实例1 的草稿仍在，实例2 的未写入");
    Check(draftsE2.Save(scopeE, Draft("d-9", "todo-9", "{\"TaskId\":\"t-9\"}", "ev-token-9"),
        expectedGeneration: afterRace.ValueOrThrow().Count == 1 ? 1 : 0).IsSuccess, "实例2 用新 generation 重试成功");
    Check(draftsE1.Load(scopeE).ValueOrThrow().Count == 2, "两条草稿共存，无覆盖");
    Check(journalE.ReadGeneration(scopeE).ValueOrThrow() == 2, "请求日志与草稿共用同一 generation（跨 store 实例可见）");

    // ── F. 原子失败保原字节 ─────────────────────────────────────────────
    var rootF = Path.Combine(work, "root-f");
    var scopeF = Scope("proj-f", Path.Combine(work, "wb-f", "workbench.db"), "authority-f");
    var draftsF = new AutomationDraftStore(rootF);
    Check(GatedSave(draftsF, scopeF, Draft("d-1", "todo-1", contextJson, "ev-token-1")).IsSuccess, "F 前置：基线草稿已落盘");
    var targetPath = StoreFile(rootF, scopeF);
    var baselineF = Bytes(targetPath);
    var storeDirF = Path.Combine(rootF, AutomationPersistenceScope.StoreDirectoryName);

    // F1. 真实文件锁占用目标文件（不是 mock）。预期 generation 在加锁前取好：锁定期内连读取都会失败，
    // 那样测的是读路径而不是原子发布。失败码在锁内断言、字节比对在释放锁后做。
    var genBeforeBlocker = draftsF.ReadGeneration(scopeF).ValueOrThrow();
    AutomationPersistenceResult<AutomationPersistenceWriteResult>? blocked;
    using (var blocker = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        blocked = draftsF.Save(scopeF, Draft("d-2", "todo-2", "{\"TaskId\":\"t-2\"}", "ev-2"), expectedGeneration: genBeforeBlocker);
        Check(!blocked.IsSuccess && blocked.Failure?.Code is AutomationPersistenceErrorCode.PublishFailed
            or AutomationPersistenceErrorCode.ReadFailed, "目标被真实文件锁占用时写入明确失败（" + blocked.Failure?.Code + "），不是静默成功");
        Check(!Directory.EnumerateFiles(storeDirF, "*.tmp-*").Any(), "失败路径清理临时文件，目录不残留半成品");
    }
    Check(SameBytes(targetPath, baselineF), "释放锁后原文件字节与锁定前逐字节一致（锁占用失败未改写任何字节）");
    // F2. 可注入 I/O 边界：发布阶段真实抛 IOException。
    var faultIo = new FaultFileIo(new AutomationPersistenceFileIo()) { FailPublish = true };
    var draftsF2 = new AutomationDraftStore(rootF, fileIo: faultIo);
    CheckCode(GatedSave(draftsF2, scopeF, Draft("d-3", "todo-3", "{\"TaskId\":\"t-3\"}", "ev-3")),
        AutomationPersistenceErrorCode.PublishFailed, "注入发布失败时返回可诊断失败");
    Check(SameBytes(targetPath, baselineF) && File.Exists(targetPath), "注入发布失败保原字节，原文件未被删除或替换");
    Check(!Directory.EnumerateFiles(storeDirF, "*.tmp-*").Any(), "注入失败同样不残留临时文件");

    // F3. 注入临时文件写入失败。
    var tempFaultStore = new AutomationDraftStore(rootF, fileIo: new FaultFileIo(new AutomationPersistenceFileIo()) { FailTempWrite = true });
    CheckCode(GatedSave(tempFaultStore, scopeF, Draft("d-6", "todo-6", "{\"TaskId\":\"t-6\"}", "ev-6")),
        AutomationPersistenceErrorCode.TempWriteFailed, "注入临时文件写入失败被明示");
    Check(SameBytes(targetPath, baselineF), "临时文件失败不影响目标原字节");

    // F4. 写入锁被另一持有者占用。
    var lockPathF = Path.Combine(storeDirF, scopeF.ScopeHash + ".lock");
    using (var held = new FileStream(lockPathF, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
    {
        CheckCode(GatedSave(draftsF, scopeF, Draft("d-4", "todo-4", "{\"TaskId\":\"t-4\"}", "ev-4")),
            AutomationPersistenceErrorCode.LockContended, "写入锁被占用时明示竞争");
        Check(SameBytes(targetPath, baselineF), "锁竞争期间没有任何写入");
    }

    // ── G. 读者持句柄按声明行为 ─────────────────────────────────────────
    var bytesBeforeG = Bytes(targetPath);
    using (var reader = new FileStream(targetPath, FileMode.Open, FileAccess.Read, AutomationPersistenceReadPolicy.ReaderShareMode))
    {
        var whileReading = GatedSave(draftsF, scopeF, Draft("d-5", "todo-5", "{\"TaskId\":\"t-5\"}", "ev-5"));
        Check(whileReading.IsSuccess, "读者按声明共享模式持句柄时，写方仍能原子发布");
        var stale = new byte[bytesBeforeG.Length];
        var got = 0;
        while (got < stale.Length) { var n = reader.Read(stale, got, stale.Length - got); if (n <= 0) break; got += n; }
        Check(got == bytesBeforeG.Length && stale.AsSpan(0, got).SequenceEqual(bytesBeforeG),
            "已打开的读者看到的仍是它打开时的旧字节（不会被半写状态污染）");
    }
    Check(draftsF.Load(scopeF).ValueOrThrow().Any(row => row.DraftId == "d-5"), "读者释放后新内容对后续读者可见");

    // ── H. 非法/未来格式/重复键/摘要不符/非法状态/超限：拒绝且保原字节 ────
    var rootH = Path.Combine(work, "root-h");
    var scopeH = Scope("proj-h", Path.Combine(work, "wb-h", "workbench.db"), "authority-h");
    var draftsH = new AutomationDraftStore(rootH);
    var journalH = new AutomationRequestJournal(rootH);
    var legalContext = "{\"TaskId\":\"t-1\"}";
    Check(GatedSave(draftsH, scopeH, Draft("d-1", "todo-1", legalContext, "ev-1")).IsSuccess, "H 前置：合法草稿已落盘");
    var hPayload = "{\"CommandId\":\"cmd-h\",\"ExpectedRevision\":3,\"Decision\":\"Rework\"}";
    Check(GatedRecord(journalH, scopeH, Request("cmd-h", hPayload)).IsSuccess, "H 前置：合法 pending_unknown 请求已落盘（非法状态反例需要真实 state 字段）");
    var scopeFileH = StoreFile(rootH, scopeH);
    var goodBytes = Bytes(scopeFileH);
    var goodText = Encoding.UTF8.GetString(goodBytes);
    var recordedDbPath = JsonText(goodText, "databasePath");

    static string JsonEscaped(string value) => value.Replace(@"\", @"\\");

    void Reject(string name, string content, AutomationPersistenceErrorCode expected)
    {
        File.WriteAllText(scopeFileH, content);
        var result = draftsH.Load(scopeH);
        CheckCode(result, expected, name);
        Check(SameBytes(scopeFileH, Encoding.UTF8.GetBytes(content)), name + " 之后磁盘保持被拒绝的原文，没有被重置、覆盖或删除");
        Check(journalH.Load(scopeH).IsSuccess == false, name + "：请求日志同样拒绝读取这份坏文件");
    }

    Reject("非法 JSON 被拒绝", "{not json", AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("重复顶层键被拒绝", goodText.Replace("\"generation\":", "\"generation\":1,\"generation\":"), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("未来格式版本被拒绝", goodText.Replace("\"formatVersion\":1", "\"formatVersion\":999"), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("未知顶层字段被拒绝", goodText.Replace("\"generation\":", "\"mystery\":1,\"generation\":"), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("scope 错配（项目改名/换库）被拒绝",
        goodText.Replace("\"projectKey\":\"proj-h\"", "\"projectKey\":\"proj-other\""), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("文件内数据库路径错配被拒绝",
        goodText.Replace(recordedDbPath, JsonEscaped(Path.Combine(work, "wb-h", "other.db"))), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("草稿摘要不符（hash 损坏）被拒绝", goodText.Replace(JsonText(goodText, "contextSha256"), Sha("另一个内容")), AutomationPersistenceErrorCode.CorruptRecord);
    Reject("非法状态被拒绝", goodText.Replace("\"state\":\"pending_unknown\"", "\"state\":\"applied_successfully\""), AutomationPersistenceErrorCode.InvalidFileContent);
    Reject("字段被截断被拒绝", goodText.Substring(0, goodText.Length - 8), AutomationPersistenceErrorCode.InvalidFileContent);

    var bulkDrafts = string.Join(",", Enumerable.Range(0, 900).Select(index =>
        "{\"draftId\":\"bulk-" + index + "\",\"todoId\":\"t\",\"todoKind\":\"review\",\"todoVersion\":null," +
        "\"contextJson\":\"{}\",\"contextSha256\":\"" + Sha("{}") + "\",\"evidenceToken\":\"\",\"evidenceTokenSha256\":\"" + Sha("") + "\"," +
        "\"reason\":\"\",\"evidence\":\"\",\"response\":\"\",\"selectedCandidateIds\":[],\"createdAtMs\":1,\"updatedAtMs\":1}"));
    Reject("条目数超限被拒绝",
        C5TestSupport.SpliceArray(goodText, "drafts", bulkDrafts, AutomationPersistenceLimits.Default.MaxDraftsPerScope + 1),
        AutomationPersistenceErrorCode.Oversize);

    File.WriteAllBytes(scopeFileH, goodBytes);
    Check(draftsH.Load(scopeH).IsSuccess, "人工恢复合法原文后重新可读（本模块不擅自修复）");

    var tinyLimits = new AutomationPersistenceLimits { MaxDraftsPerScope = 1 };
    var overLimitStore = new AutomationDraftStore(rootH, limits: tinyLimits);
    CheckCode(GatedSave(overLimitStore, scopeH, Draft("d-new", "todo-new", "{\"x\":1}", "ev-new")),
        AutomationPersistenceErrorCode.Oversize, "写入时条目数超上限被拒（已有 1 条 + 新 1 条 > 上限 1）");
    Check(SameBytes(scopeFileH, goodBytes), "超限拒绝不改写原文件");

    CheckCode(new AutomationDraftStore(rootH, limits: new AutomationPersistenceLimits { MaxContextUtf8Bytes = 64 })
        .Save(scopeH, Draft("d-big", "todo-big", "{\"Blob\":\"" + new string('x', 400) + "\"}", "ev")),
        AutomationPersistenceErrorCode.Oversize, "context 超 UTF-8 上限被拒");
    CheckCode(GatedSave(draftsH, scopeH, Draft("d-badhash", "todo-1", legalContext, "ev-1") with { ContextSha256 = Sha(legalContext + "篡改") }),
        AutomationPersistenceErrorCode.CorruptRecord, "写入时草稿摘要与原文不符被拒（不信任调用方哈希）");
    CheckCode(GatedRecord(journalH, scopeH, Request("cmd-bad", payload) with { PayloadSha256 = Sha(payload + "篡改") }),
        AutomationPersistenceErrorCode.CorruptRecord, "请求 payload 摘要不符被拒");
    CheckCode(GatedRecord(journalH, scopeH, Request("cmd-broken-json", "{oops")),
        AutomationPersistenceErrorCode.InvalidFileContent, "非法 payload JSON 被拒");
    Check(SameBytes(scopeFileH, goodBytes), "以上写入侧拒绝全部未改动原文件");

    // ── I. pending_unknown 只供对账：不执行、不重发、不追加请求 ──────────
    var rootI = Path.Combine(work, "root-i");
    var scopeI = Scope("proj-i", Path.Combine(work, "wb-i", "workbench.db"), "authority-i");
    var journalI = new AutomationRequestJournal(rootI);
    var draftsI = new AutomationDraftStore(rootI);
    var payloadP1 = "{\"CommandId\":\"cmd-p1\",\"ExpectedRevision\":12,\"Decision\":\"Rework\"}";
    var payloadP2 = "{\"CommandId\":\"cmd-p2\",\"ExpectedRevision\":12,\"Decision\":\"Accept\"}";
    Check(GatedRecord(journalI, scopeI, Request("cmd-p1", payloadP1)).IsSuccess, "I 前置：第一条未知请求已记录");
    Check(GatedRecord(journalI, scopeI, Request("cmd-p2", payloadP2)).IsSuccess, "I 前置：第二条未知请求已记录");
    var scopeFileI = StoreFile(rootI, scopeI);

    var pendingFirst = journalI.ListPendingUnknown(scopeI);
    var genBeforeReads = journalI.ReadGeneration(scopeI).ValueOrThrow();
    var bytesBeforeReads = Bytes(scopeFileI);
    Check(pendingFirst.IsSuccess && pendingFirst.ValueOrThrow().Count == 2, "pending_unknown 列出 2 条供人工核对");
    for (var round = 0; round < 3; round++) _ = journalI.ListPendingUnknown(scopeI);
    Check(journalI.ReadGeneration(scopeI).ValueOrThrow() == genBeforeReads
        && journalI.Load(scopeI).ValueOrThrow().Count == 2
        && SameBytes(scopeFileI, bytesBeforeReads),
        "反复对账读取不追加请求、不推进 generation、字节不变（本模块不重发）");
    Check(pendingFirst.ValueOrThrow().All(row => row.State == AutomationRequestState.PendingUnknown),
        "对账读取没有把未知状态改成决定或成功");

    var receipt = "{\"CommandId\":\"cmd-p1\",\"Revision\":13,\"TaskId\":\"t-1\",\"Decision\":\"Rework\"}";
    var observed = GatedObserve(journalI, scopeI, "cmd-p1", receipt);
    Check(observed.IsSuccess && observed.ValueOrThrow().Generation == genBeforeReads + 1, "显式记录回执才推进 generation");
    var seen = journalI.Find(scopeI, "cmd-p1");
    Check(seen.ValueOrThrow()!.State == AutomationRequestState.ReceiptObserved && seen.ValueOrThrow()!.ReceiptJson == receipt
        && seen.ValueOrThrow()!.PayloadJson == payloadP1, "回执与不可变 payload 同时精确可读");
    var replaySame = GatedObserve(journalI, scopeI, "cmd-p1", receipt);
    Check(replaySame.IsSuccess && replaySame.ValueOrThrow().Generation == genBeforeReads + 1, "同 commandId 同回执重放幂等，不重复写");
    Check(journalI.Load(scopeI).ValueOrThrow().Count == 2, "回执记录/重放不追加新请求");
    CheckCode(GatedObserve(journalI, scopeI, "cmd-p1", "{\"CommandId\":\"cmd-p1\",\"Revision\":14}"),
        AutomationPersistenceErrorCode.DuplicateIdentity, "已记录回执不可变：不同回执被拒绝");
    CheckCode(GatedObserve(journalI, scopeI, "cmd-never", receipt),
        AutomationPersistenceErrorCode.UnknownIdentifier, "未知 commandId 的回执被拒绝，不新增不猜测");
    CheckCode(GatedObserve(journalI, scopeI, "cmd-p2", "not json"),
        AutomationPersistenceErrorCode.InvalidFileContent, "非法回执 JSON 被拒绝");
    Check(journalI.Find(scopeI, "cmd-p2").ValueOrThrow()!.State == AutomationRequestState.PendingUnknown,
        "被拒绝的回执没有改变原请求状态");
    Check(draftsI.Load(scopeI).ValueOrThrow().Count == 0, "空草稿 scope 与请求日志共存不报错（本 scope 确实没有草稿）");

    // ── J. 旧草稿不自动套用到新版本 ─────────────────────────────────────
    var rootJ = Path.Combine(work, "root-j");
    var scopeJ = Scope("proj-j", Path.Combine(work, "wb-j", "workbench.db"), "authority-j");
    var draftsJ = new AutomationDraftStore(rootJ);
    var contextJ = "{\"TaskId\":\"t-1\",\"part\":{\"Revision\":7,\"ReviewId\":\"rv-7\"}}";
    Check(GatedSave(draftsJ, scopeJ, Draft("d-j", "todo-1", contextJ, "ev-7")).IsSuccess, "J 前置：草稿绑定版本 7 / 证据 ev-7");
    Check(draftsJ.OpenFor(scopeJ, "d-j", "todo-1", "review", 7, "ev-7", contextJ).IsSuccess, "同版本同证据可重新打开");
    CheckCode(draftsJ.OpenFor(scopeJ, "d-j", "todo-1", "review", 8, "ev-7", contextJ),
        AutomationPersistenceErrorCode.DraftSuperseded, "待办版本前进后不自动套用旧草稿");
    CheckCode(draftsJ.OpenFor(scopeJ, "d-j", "todo-1", "review", 7, "ev-8", contextJ),
        AutomationPersistenceErrorCode.DraftSuperseded, "证据 token 变化后不自动套用旧草稿");
    CheckCode(draftsJ.OpenFor(scopeJ, "d-j", "todo-2", "review", 7, "ev-7", contextJ),
        AutomationPersistenceErrorCode.DraftSuperseded, "待办 ID 不符不会串草稿");
    Check(draftsJ.OpenFor(scopeJ, "d-missing", "todo-1", "review", 7, "ev-7", contextJ).IsSuccess
        && draftsJ.OpenFor(scopeJ, "d-missing", "todo-1", "review", 7, "ev-7", contextJ).ValueOrThrow() is null,
        "不存在的草稿 ID 返回空值而不是失败");
    Check(draftsJ.Load(scopeJ).ValueOrThrow().Any(row => row.DraftId == "d-j"), "被判过期的草稿仍在盘上，不删除用户内容");

    // ── K. 本模块不执行、不碰 Core：结构性证据 ──────────────────────────
    var persistenceNamespace = "Fufucode.App.Features.Automation.Persistence";
    var publicTypes = typeof(AutomationDraftStore).Assembly.GetTypes()
        .Where(type => type.Namespace == persistenceNamespace && type.IsPublic).ToArray();
    Check(publicTypes.Length >= 8, "公共类型清单可读（" + publicTypes.Length + " 个）");
    var publicMembers = publicTypes
        .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
        .Select(method => method.Name).ToArray();
    string[] forbidden = ["Apply", "Dispatch", "Send", "Execute", "Start", "Run", "Retry", "Reconcile", "Migrate", "Approve", "Integrate", "Consumer"];
    var offending = publicMembers.Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
    Check(offending.Length == 0, "公共 API 不含执行/发送/派发/迁移类动作（未知请求无法被本模块自动执行）· 命中：" + string.Join(",", offending));
    var moduleSources = Directory.GetFiles(Path.Combine(repoRoot, "src", "Fufucode.Plugins.AutoDev", "Ui", "Automation", "Persistence"), "*.cs", SearchOption.AllDirectories);
    Check(moduleSources.Length >= 8, "模块源文件清单（" + moduleSources.Length + " 个 .cs）");
    var coreHits = moduleSources.Where(path => File.ReadAllText(path).Contains("Fufucode.Core", StringComparison.Ordinal)).ToArray();
    Check(coreHits.Length == 0, "模块源码不引用 Fufucode.Core（不调用 Core、不启动 harness）· 命中：" + string.Join(",", coreHits));
    var sqliteHits = moduleSources.Where(path => File.ReadAllText(path).Contains("Sqlite", StringComparison.OrdinalIgnoreCase)).ToArray();
    Check(sqliteHits.Length == 0, "模块源码不打开任何数据库文件 · 命中：" + string.Join(",", sqliteHits));

    // ── L. 纯读取无磁盘副作用；同路径不同稳定身份互相隔离 ────────────────
    var rootL = Path.Combine(work, "root-l");
    var oldDb = Path.Combine(work, "wb-l", "workbench.db");
    var draftsL = new AutomationDraftStore(rootL);
    Check(draftsL.Load(Scope("proj-l", oldDb, "authority-l")).IsSuccess, "未连接过的 scope 读取返回空而不是错误");
    Check(!Directory.Exists(Path.Combine(rootL, AutomationPersistenceScope.StoreDirectoryName)),
        "纯读取不创建固定目录（无磁盘副作用）");
    var authorityOld = Scope("proj-l", oldDb, "authority-old");
    var authorityNew = Scope("proj-l", oldDb, "authority-new");
    Check(GatedSave(draftsL, authorityOld, Draft("d-old", "todo-1", contextJson, "ev-1")).IsSuccess, "旧身份草稿落盘");
    Check(draftsL.Load(authorityNew).ValueOrThrow().Count == 0, "同路径同项目但新稳定身份读不到旧身份草稿（模块不推断新旧等价）");
    Check(draftsL.Load(authorityOld).ValueOrThrow().Count == 1, "旧身份仍可完整读回");

    // ── M. 文件被改名/复制到另一 scope：拒绝恢复且保留原字节 ──────────────
    var rootM = Path.Combine(work, "root-m");
    var draftsM = new AutomationDraftStore(rootM);
    var scopeM1 = Scope("proj-m1", Path.Combine(work, "wb-m", "workbench.db"), "authority-m");
    var scopeM2 = Scope("proj-m2", Path.Combine(work, "wb-m", "workbench.db"), "authority-m");
    Check(GatedSave(draftsM, scopeM1, Draft("d-m1", "todo-1", contextJson, "ev-m1")).IsSuccess, "M 前置：项目 m1 草稿落盘");
    var m1File = StoreFile(rootM, scopeM1);
    var m1Bytes = Bytes(m1File);
    File.Copy(m1File, StoreFile(rootM, scopeM2), overwrite: true);
    CheckCode(draftsM.Load(scopeM2), AutomationPersistenceErrorCode.InvalidFileContent, "复制过去的文件内 scope 与请求 scope 不符 → 拒绝恢复");
    Check(SameBytes(StoreFile(rootM, scopeM2), m1Bytes), "错配文件保持原字节：不重置、不覆盖、不删除");
    Check(draftsM.Load(scopeM1).ValueOrThrow().Count == 1, "原项目草稿仍可正常读取");
    var intact = Bytes(m1File);
    File.AppendAllBytes(m1File, new byte[] { (byte)'\\', (byte)'{' });
    CheckCode(draftsM.Load(scopeM1), AutomationPersistenceErrorCode.InvalidFileContent, "尾部损坏的文件整体拒绝，不返回部分草稿");
    File.WriteAllBytes(m1File, intact);
    Check(draftsM.Load(scopeM1).ValueOrThrow().Count == 1, "人工恢复原文后可读（模块不擅自修复）");

    // ── N. 固定目录被换成 junction：拒绝读写，不跟随链接 ──────────────────
    var rootN = Path.Combine(work, "root-n");
    var scopeN = Scope("proj-n", Path.Combine(work, "wb-n", "workbench.db"), "authority-n");
    var draftsN = new AutomationDraftStore(rootN);
    Check(GatedSave(draftsN, scopeN, Draft("d-n0", "todo-0", contextJson, "ev-n0")).IsSuccess, "N 前置：基线写入");
    var linkStoreDir = Path.Combine(work, "junction-store");
    var linkMade = MakeJunction(linkStoreDir, Path.Combine(rootN, AutomationPersistenceScope.StoreDirectoryName));
    if (!linkMade)
    {
        Skip("junction 拒绝用例（mklink /J 在本机不可用）");
    }
    else
    {
        var linkRootMarker = Path.Combine(linkStoreDir, "outside-marker.json");
        Check(File.Exists(StoreFile(rootN, scopeN)), "junction 指向真实目录");
        var throughLink = new AutomationDraftStore(linkStoreDir + "-parent");
        // 通过 junction 的父目录写入：目录本身是重解析点，必须拒绝而不是跟随。
        var junctionIoRoot = Path.GetDirectoryName(linkStoreDir)!;
        var viaJunction = new AutomationDraftStore(Path.Combine(junctionIoRoot, "unused"));
        Check(viaJunction.StoreDirectory != linkStoreDir, "普通实例的固定目录独立于 junction（对照）");
        Check(!File.Exists(linkRootMarker), "未通过链接写入任何文件");
        var storeThroughLink = new AutomationDraftStore(linkStoreDir);
        // 显式预期值：证明拒绝来自写入路径自身（不跟随链接），而不是读侧先行失败。
        CheckCode(storeThroughLink.Save(Scope("proj-link", Path.Combine(work, "wb-n", "workbench.db"), "authority-n"),
            Draft("d-link", "todo-1", contextJson, "ev-link"), expectedGeneration: 0), AutomationPersistenceErrorCode.ReparsePoint,
            "固定目录是重解析点时拒绝写入（不跟随链接，路径逃逸无机会）");
        CheckCode(storeThroughLink.Load(Scope("proj-link", Path.Combine(work, "wb-n", "workbench.db"), "authority-n")),
            AutomationPersistenceErrorCode.ReparsePoint, "同一链接目录的读取也被拒绝（不只写侧防护）");
    }

    // N2. 目录正常，但该 scope 的目标文件位置本身是 junction：写入必须拒绝，绝不穿过链接落到外部目录。
    var outsideN2 = Path.Combine(work, "outside-n2");
    Directory.CreateDirectory(outsideN2);
    var outsidePlanted = Path.Combine(outsideN2, "planted.json");
    File.WriteAllText(outsidePlanted, "OUTSIDE-MUST-NOT-CHANGE");
    var scopeN2 = Scope("proj-n2", Path.Combine(work, "wb-n", "workbench.db"), "authority-n");
    var n2Target = StoreFile(rootN, scopeN2);
    var n2LinkMade = MakeJunction(n2Target, outsideN2);
    if (!n2LinkMade)
    {
        Skip("N2 目标文件位为 junction 的拒绝用例（mklink /J 在本机不可用）");
    }
    else
    {
        Check(Directory.Exists(n2Target), "N2 前置：目标文件位置确实是目录链接");
        CheckCode(draftsN.Save(scopeN2, Draft("d-n2", "todo-1", contextJson, "ev-n2"), expectedGeneration: 0),
            AutomationPersistenceErrorCode.ReparsePoint, "目标文件位是重解析点时拒绝写入，不穿过链接");
        Check(File.ReadAllText(outsidePlanted) == "OUTSIDE-MUST-NOT-CHANGE", "外部目录内容没有被本模块写入或改写");
        Check(!Directory.EnumerateFiles(outsideN2, "*.tmp-*").Any(), "临时文件没有落进链接指向的外部目录");
        CheckCode(draftsN.Load(scopeN2), AutomationPersistenceErrorCode.ReparsePoint, "同一链接位置的读取也被拒绝（不只写侧防护）");
    }
    // ── O. 多线程同 scope 竞争：允许明示冲突，但绝不静默丢内容 ────────────
    var rootO = Path.Combine(work, "root-o");
    var scopeO = Scope("proj-o", Path.Combine(work, "wb-o", "workbench.db"), "authority-o");
    var stores = Enumerable.Range(0, 6).Select(index => new AutomationDraftStore(rootO)).ToArray();
    var results = new AutomationPersistenceResult<AutomationPersistenceWriteResult>?[stores.Length];
    var threads = stores.Select((store, index) => new Thread(() =>
    {
        results[index] = GatedSave(store, scopeO, Draft("d-par-" + index, "todo-" + index, "{\"TaskId\":\"p" + index + "\"}", "ev-" + index));
    })).ToArray();
    foreach (var thread in threads) thread.Start();
    foreach (var thread in threads) thread.Join();
    var succeeded = results.Count(result => result?.IsSuccess == true);
    var refused = results.Count(result => result?.Failure?.Code is AutomationPersistenceErrorCode.GenerationConflict
        or AutomationPersistenceErrorCode.PublishFailed or AutomationPersistenceErrorCode.LockContended
        or AutomationPersistenceErrorCode.TempWriteFailed);
    Check(succeeded + refused == stores.Length, "并行写入只有成功或明示冲突两种结局，没有静默失败（成功 " + succeeded + " / 冲突 " + refused + "）");
    var parallel = stores[0].Load(scopeO).ValueOrThrow();
    Check(parallel.Count == succeeded, "盘上条数等于成功数（无丢失、无凭空多出）");
    Check(parallel.Select(row => row.DraftId).Distinct().Count() == parallel.Count, "并行写入没有产生重复 draftId");
    var expectedIds = Enumerable.Range(0, stores.Length).Where(index => results[index]?.IsSuccess == true).Select(index => "d-par-" + index).ToArray();
    Check(parallel.Select(row => row.DraftId).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(expectedIds.OrderBy(id => id, StringComparer.Ordinal)),
        "成功写入的草稿全部可枚举，失败方没有留下半成品");

    // ── P. M 首轮审核四个 finding 的逐条反例（对齐 tmp/c5-persist-review/Program.cs 的真实形态）──
    // P1. C5-PERSIST-SCOPE-1：合法 Unicode 分隔符不得让两个不同 scope 元组撞同一个文件名。
    var tupleA = Scope("a∤C:\\X", "C:\\Y", "z");
    var tupleB = Scope("a", "C:\\X", "C:\\Y∤z");
    Check(!tupleA.SameTargetAs(tupleB), "P1 两个不同 scope 元组不是同一目标");
    Check(tupleA.ScopeHash != tupleB.ScopeHash, "P1 不同元组的 ScopeHash/文件名不同（长度前缀编码，不按分隔符切分）");
    var rootP = Path.Combine(work, "root-p");
    var draftsP = new AutomationDraftStore(rootP);
    var journalP = new AutomationRequestJournal(rootP);
    var scopeP = Scope("a∤C:\\X", "C:\\Y", "z");
    Check(GatedSave(draftsP, scopeP, Draft("d-p1", "todo-1", contextJson, "ev-p1")).IsSuccess, "P1 前置：含 U+2224 的合法 projectKey 可写");
    Check(draftsP.Load(tupleB).ValueOrThrow().Count == 0, "P1 另一元组读到空：两者独立存储，文件名不再碰撞");

    // P2. C5-PERSIST-PATH-1：盘相对与根相对必须在展开前拒绝。
    CheckCode(AutomationPersistenceScope.TryCreate("p", @"C:relative.db", "owner"),
        AutomationPersistenceErrorCode.InvalidArgument, "P2 盘相对 C:relative.db 被拒绝");
    CheckCode(AutomationPersistenceScope.TryCreate("p", @"\relative.db", "owner"),
        AutomationPersistenceErrorCode.InvalidArgument, "P2 根相对 \relative.db 被拒绝");
    CheckCode(AutomationPersistenceScope.TryCreate("p", "/relative.db", "owner"),
        AutomationPersistenceErrorCode.InvalidArgument, "P2 根相对 /relative.db 被拒绝");
    var fullyQualified = Path.GetFullPath(Path.Combine(work, "wb-p2", "workbench.db"));
    Check(AutomationPersistenceScope.TryCreate("p", fullyQualified, "owner").IsSuccess, "P2 完整绝对路径仍被接受（没有误伤合法路径）");
    Check(Scope("p", fullyQualified, "owner").SameTargetAs(Scope("p", fullyQualified.Replace(Path.DirectorySeparatorChar, '/'), "owner")),
        "P2 完整合法路径的斜杠等价保持不变");

    // P3. C5-PERSIST-FINGERPRINT-1：合法文本里的 U+241F 与候选边界不得伪造幂等。
    var scopeP3 = Scope("proj-p3", Path.Combine(work, "wb-p3", "workbench.db"), "owner-p3");
    var separatorReason = new AutomationDraftEntry
    {
        DraftId = "text", TodoId = "todo", TodoKind = "review", TodoVersion = 1,
        ContextJson = "{}", ContextSha256 = Sha("{}"), EvidenceToken = "", EvidenceTokenSha256 = Sha(""),
        Reason = "one␟two", Evidence = "three",
    };
    Check(GatedSave(draftsP, scopeP3, separatorReason).IsSuccess, "P3 前置：含 U+241F 的合法文本可保存");
    var separatorUpdated = separatorReason with { Reason = "one", Evidence = "two␟three" };
    var saveUpdated = GatedSave(draftsP, scopeP3, separatorUpdated);
    Check(saveUpdated.IsSuccess && saveUpdated.ValueOrThrow().Generation == 2, "P3 字段边界移动识别为改写（generation 推进，不误判幂等）");
    var savedText = draftsP.Find(scopeP3, "text").ValueOrThrow()!;
    Check(savedText.Reason == separatorUpdated.Reason && savedText.Evidence == separatorUpdated.Evidence,
        "P3 重新打开拿到新文本，旧文本没被幂等判定吞掉（丢稿缺陷已修）");
    var commaCandidates = Draft("candidates", "todo-c", "{}", "") with { SelectedCandidateIds = new[] { "a,b" } };
    Check(GatedSave(draftsP, scopeP3, commaCandidates).IsSuccess, "P3 前置：候选含逗号可保存");
    var splitCandidates = commaCandidates with { SelectedCandidateIds = new[] { "a", "b" } };
    var splitSave = GatedSave(draftsP, scopeP3, splitCandidates);
    Check(splitSave.IsSuccess && splitSave.ValueOrThrow().Generation == 4, "P3 [\"a,b\"] → [\"a\",\"b\"] 识别为改写（元素数与边界都进编码）");
    Check(draftsP.Find(scopeP3, "candidates").ValueOrThrow()!.SelectedCandidateIds.SequenceEqual(new[] { "a", "b" }),
        "P3 候选列表元素边界保留，修改实际落盘");
    var sameAgain = GatedSave(draftsP, scopeP3, splitCandidates);
    Check(sameAgain.IsSuccess && sameAgain.ValueOrThrow().Generation == 4, "P3 完全相同内容仍然幂等（没把幂等一起改掉）");

    // P4. C5-PERSIST-GENERATION-1：缺预期 generation 的盲写一律拒绝，覆盖每个写入口。
    var scopeP4 = Scope("proj-p4", Path.Combine(work, "wb-p4", "workbench.db"), "owner-p4");
    Check(GatedSave(draftsP, scopeP4, Draft("d-anchor", "todo-1", contextJson, "ev-anchor")).IsSuccess, "P4 前置：锚点草稿已落盘");
    var anchorBytes = Bytes(StoreFile(rootP, scopeP4));
    CheckCode(draftsP.Save(scopeP4, Draft("d-blind", "todo-1", contextJson, "ev-blind")),
        AutomationPersistenceErrorCode.MissingExpectedGeneration, "P4 草稿 Save 省略 expectedGeneration 被明示拒绝");
    CheckCode(draftsP.Remove(scopeP4, "d-anchor"),
        AutomationPersistenceErrorCode.MissingExpectedGeneration, "P4 草稿 Remove 省略 expectedGeneration 同样被拒");
    CheckCode(journalP.Record(scopeP4, Request("cmd-blind", "{\"CommandId\":\"cmd-blind\"}")),
        AutomationPersistenceErrorCode.MissingExpectedGeneration, "P4 请求 Record 省略 expectedGeneration 被拒");
    CheckCode(journalP.ObserveReceipt(scopeP4, "cmd-blind", "{\"CommandId\":\"cmd-blind\"}"),
        AutomationPersistenceErrorCode.MissingExpectedGeneration, "P4 回执 ObserveReceipt 省略 expectedGeneration 被拒");
    Check(SameBytes(StoreFile(rootP, scopeP4), anchorBytes), "P4 四次盲写都没有改动文件一个字节");
    Check(GatedSave(draftsP, scopeP4, Draft("d-blind", "todo-1", contextJson, "ev-blind")).IsSuccess,
        "P4 同一写入补上预期 generation 后成功（门不是永久失败）");

    // P5. 必填 generation 之后，两实例竞争仍只有一方成功且不丢稿。
    var scopeP5 = Scope("proj-p5", Path.Combine(work, "wb-p5", "workbench.db"), "owner-p5");
    var p5Store = new AutomationDraftStore(rootP);
    var p5Other = new AutomationDraftStore(rootP);
    Check(p5Store.Save(scopeP5, Draft("d-p5", "todo-1", contextJson, "ev-p5"), expectedGeneration: 0).IsSuccess, "P5 实例1 以 0 写入成功");
    CheckCode(p5Other.Save(scopeP5, Draft("d-p5b", "todo-2", "{\"TaskId\":\"b\"}", "ev-p5b"), expectedGeneration: 0),
        AutomationPersistenceErrorCode.GenerationConflict, "P5 实例2 用陈旧 0 仍被拒（新门没有削弱竞争检测）");
    var p5Read = p5Other.Load(scopeP5).ValueOrThrow();
    Check(p5Read.Count == 1 && p5Read[0].DraftId == "d-p5", "P5 竞争失败方没有丢稿也没有写入");
    Check(GatedRemove(p5Store, scopeP5, "d-p5").IsSuccess && p5Store.Load(scopeP5).ValueOrThrow().Count == 0,
        "P5 带预期 generation 的 Remove 正常生效（该写入口可用）");

    // P6. 首轮实现留下的旧文件名（scope 哈希输入编码变更产生的孤儿文件）必须原地保留，不读不删不改。
    var storeDirP = Path.Combine(rootP, AutomationPersistenceScope.StoreDirectoryName);
    var staleName = Path.Combine(storeDirP, "stale-first-round.json");
    if (!File.Exists(staleName)) File.WriteAllText(staleName, "STALE-FIRST-ROUND-FILE");
    var staleBytes = Bytes(staleName);
    Check(GatedSave(new AutomationDraftStore(rootP), scopeP3, Draft("d-p6", "todo-p6", "{}", "ev-p6")).IsSuccess, "P6 前置：新一轮写入成功");
    Check(SameBytes(staleName, staleBytes), "P6 旧命名文件原样保留（模块不删除、不覆盖、不迁移他人文件）");
    Check(draftsP.Load(scopeP3).ValueOrThrow().Any(row => row.DraftId == "d-p6"), "P6 新写入对自己 scope 可见");

    // ── Q. M 接线前新增两项 finding 的逐条反例 ────────────────────────────
    // Q1. C5-PERSIST-OPAQUE-ID-1：projectKey / authorityIdentity 是逐字保真的不透明原身份。
    var rootQ = Path.Combine(work, "root-q");
    var dbQ = Path.Combine(rootQ, "authority.db");
    var qExact = Scope("project", dbQ, "authority");
    var qProject = Scope(" project ", dbQ, "authority");
    var qAuthority = Scope("project", dbQ, " authority ");
    Check(qProject.ProjectKey == " project " && qAuthority.AuthorityIdentity == " authority ",
        "Q1 首尾空格原样保留（不 Trim、不由规范化文字反推原身份）");
    Check(!qExact.SameTargetAs(qProject) && qExact.ScopeHash != qProject.ScopeHash,
        "Q1 " + "\"project\"" + " 与 \"" + " project " + "\" 是不同 scope、不同文件名");
    Check(!qExact.SameTargetAs(qAuthority) && qExact.ScopeHash != qAuthority.ScopeHash,
        "Q1 " + "\"authority\"" + " 与 \"" + " authority " + "\" 是不同 scope、不同文件名");
    var qStore = new AutomationDraftStore(rootQ);
    Check(GatedSave(qStore, qExact, Draft("private", "todo", "{}", "")).IsSuccess, "Q1 前置：exact scope 草稿落盘");
    Check(qStore.Load(qProject).ValueOrThrow().Count == 0 && qStore.Load(qAuthority).ValueOrThrow().Count == 0,
        "Q1 另一原身份读不到 exact 的草稿（真实存储隔离，不只是哈希层面）");
    Check(qStore.Find(qProject, "private").ValueOrThrow() is null, "Q1 用另一身份按同一 DraftId 也找不到别人的草稿");
    Check(qStore.Load(qExact).ValueOrThrow().Count == 1, "Q1 exact 自己仍完整可读");
    Check(AutomationPersistenceScope.TryCreate("   ", dbQ, "authority").Failure?.Code == AutomationPersistenceErrorCode.InvalidArgument
        && AutomationPersistenceScope.TryCreate("project", dbQ, "  ").Failure?.Code == AutomationPersistenceErrorCode.InvalidArgument,
        "Q1 纯空白身份仍然拒绝（不是「带空格的身份」）");
    var longLimits = new AutomationPersistenceLimits { MaxIdentifierLength = 10 };
    // 这组用例只用一个 7 字符绝对路径（本模块不做磁盘访问），否则库路径本身就会先撞同一个上限。
    var shortDb = @"C:\x.db";
    CheckCode(AutomationPersistenceScope.TryCreate("0123456789X", shortDb, "authority", longLimits),
        AutomationPersistenceErrorCode.InvalidArgument, "Q1 projectKey 原始长度 11 > 10 明示拒绝（不因 Trim 侥幸通过）");
    Check(AutomationPersistenceScope.TryCreate("0123456789", shortDb, "authority", longLimits).IsSuccess
        && AutomationPersistenceScope.TryCreate(" 0123456789 ", shortDb, "authority", longLimits).Failure?.Code == AutomationPersistenceErrorCode.InvalidArgument,
        "Q1 长度按原始值算：10 字符身份接受，12 字符（Trim 后才等于 10）超限拒绝——旧 Trim 规则会假装合规");
    Check(GatedSave(qStore, qExact, Draft(" id-with-spaces ", "todo", "{}", "")).IsSuccess,
        "Q1 DraftId 首尾空格被逐字保留并可写入");
    Check(qStore.Find(qExact, " id-with-spaces ").ValueOrThrow() is not null
        && qStore.Find(qExact, "id-with-spaces").ValueOrThrow() is null,
        "Q1 查找同样按逐字原值：去掉空格的 key 找不到那条草稿");

    // Q2. C5-PERSIST-NULL-VERSION-1：无版本号的合法草稿（TodoVersion=null）读写保真，且不毒化整个 scope。
    var qNullDraft = Draft("nullable", "proposal", "{}", "") with { TodoKind = "knowledge", TodoVersion = null, Response = "" };
    var qNullSave = GatedSave(qStore, qExact, qNullDraft);
    Check(qNullSave.IsSuccess, "Q2 null 版本草稿 Save 成功");
    var qNullLoad = qStore.Load(qExact);
    Check(qNullLoad.IsSuccess, "Q2 含 null 版本的 scope 仍可整体读取（不再报 todoVersion 必须整数或 null）");
    Check(qNullLoad.ValueOrThrow().Single(row => row.DraftId == "nullable").TodoVersion is null,
        "Q2 重新读回仍是 null（没有被造成 0，也没有丢字段）");
    Check(qStore.Find(qExact, "nullable").ValueOrThrow()!.TodoVersion is null, "Q2 Find 路径同样保真");
    Check(qStore.OpenFor(qExact, "nullable", "proposal", "knowledge", null, null, "{}").IsSuccess
        && qStore.OpenFor(qExact, "nullable", "proposal", "knowledge", 5, null, "{}").IsSuccess,
        "Q2 OpenFor 可用：无版本草稿不被判为过期（知识提案本就无版本）");
    var qNullRewrite = GatedSave(qStore, qExact, qNullDraft with { Reason = "改成接受" });
    Check(qNullRewrite.IsSuccess && qStore.Find(qExact, "nullable").ValueOrThrow()!.Reason == "改成接受",
        "Q2 null 版本草稿可再次写入并读到新内容");
    var qNullIdempotent = GatedSave(qStore, qExact, qNullDraft with { Reason = "改成接受" });
    Check(qNullIdempotent.IsSuccess && qNullIdempotent.ValueOrThrow().Generation == qNullRewrite.ValueOrThrow().Generation,
        "Q2 null 版本草稿的幂等判定仍成立（没有因为改动把幂等弄坏）");
    var qVersionedFile = StoreFile(rootQ, qExact);
    var qVersionedText = Encoding.UTF8.GetString(Bytes(qVersionedFile));
    Check(qVersionedText.Contains("\"todoVersion\":null", StringComparison.Ordinal),
        "Q2 写侧对 null 输出显式 JSON null（与省略同义，读侧两种都还原成 null）");
    // 非法类型与未来格式严拒保持不变（不放松成「就当没有」）。
    var qBadKindText = qVersionedText.Replace("\"todoVersion\":null", "\"todoVersion\":\"7\"");
    File.WriteAllText(qVersionedFile, qBadKindText);
    CheckCode(qStore.Load(qExact), AutomationPersistenceErrorCode.InvalidFileContent, "Q2 todoVersion 是字符串时严拒（不静默当 null）");
    var qBadFutureText = qVersionedText.Replace("\"todoVersion\":null", "\"todoVersion\":true");
    File.WriteAllText(qVersionedFile, qBadFutureText);
    CheckCode(qStore.Load(qExact), AutomationPersistenceErrorCode.InvalidFileContent, "Q2 todoVersion 是布尔时同样严拒");
    File.WriteAllText(qVersionedFile, qVersionedText);
    Check(qStore.Load(qExact).IsSuccess, "Q2 恢复合法原文后可读（拒绝路径未改写文件）");
    // 字段省略与显式 null 同义：手写一份「省略 todoVersion」的文件也必须可读回 null。
    var qOmitted = qVersionedText.Replace("\"todoVersion\":null,", "");
    File.WriteAllText(qVersionedFile, qOmitted);
    var qOmittedLoad = qStore.Load(qExact);
    Check(qOmittedLoad.IsSuccess && qOmittedLoad.ValueOrThrow().Single(row => row.DraftId == "nullable").TodoVersion is null,
        "Q2 省略字段与显式 null 同义：两种写法都还原成 null");
    Check(GatedSave(qStore, qExact, qNullDraft).IsSuccess, "Q2 省略形态下再次写入仍可用");
    File.WriteAllText(qVersionedFile, qVersionedText);
    var qBadLimits = new AutomationDraftStore(rootQ, limits: new AutomationPersistenceLimits { MaxDraftsPerScope = 64 });
    Check(qBadLimits.Load(qExact).IsSuccess, "Q2 收尾：整 scope 在各类反例后仍可正常读取");

    static string JsonText(string json, string name)
    {
        var key = "\"" + name + "\":\"";
        var start = json.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) throw new Exception("JSON 里没有字段 " + name);
        start += key.Length;
        var end = json.IndexOf("\"", start, StringComparison.Ordinal);
        return json[start..end];
    }

    Console.WriteLine();
    Console.WriteLine("C5-PERSIST 定向验证通过：" + passed + " 项断言"
        + (skipped.Count > 0 ? "，跳过 " + skipped.Count + " 项：" + string.Join("、", skipped) : string.Empty));
    Console.WriteLine("工作目录（供复核）：" + work);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("工作目录（失败现场）：" + work);
    return 1;
}

/// <summary>可注入的真实 I/O 故障边界：只包装真实实现，不伪造成功。</summary>
internal sealed class FaultFileIo(AutomationPersistenceFileIo inner) : IAutomationPersistenceFileIo
{
    public bool FailPublish { get; init; }
    public bool FailTempWrite { get; init; }

    public void EnsureDirectory(string directoryPath) => inner.EnsureDirectory(directoryPath);
    public bool FileExists(string filePath) => inner.FileExists(filePath);
    public byte[] ReadAllBytes(string filePath) => inner.ReadAllBytes(filePath);

    public bool TryAcquireWriteLock(string lockFilePath, int timeoutMs, out IAutomationPersistenceLock? handle)
        => inner.TryAcquireWriteLock(lockFilePath, timeoutMs, out handle);

    public string WriteTempFile(string directoryPath, string fileNameHint, byte[] content)
    {
        if (FailTempWrite) throw new IOException("注入：临时文件写入失败");
        return inner.WriteTempFile(directoryPath, fileNameHint, content);
    }

    public void Publish(string tempFilePath, string targetFilePath)
    {
        if (FailPublish) throw new IOException("注入：原子发布失败");
        inner.Publish(tempFilePath, targetFilePath);
    }

    public void DeleteQuietly(string filePath) => inner.DeleteQuietly(filePath);
}
