# M 独立审核：E2-B 浏览器

任务E2-B，L4，原模型group/auto-deepseek-v4-1-flash。真实chat01a13085-8c79-7602-a35e-9119100e6a9f/local，交付轮01a13085-d477-7852-b5d8-3ba5e94ae5f7，源C:/Users/mio/.codex/worktrees/1dd7/Fufucode，起点f7d2bba、冻结HEAD9f2f19dd533e730cf06594a4d6369e43cf2abdd9。游标:10 idle/completed、树干净、无树专属App/dotnet/pwsh写入者后才读卡/源。7提交/14文件允许范围；无继承未提交内容。未同步main或夹带用户文件。

结构化结论见E2-browser-review-2026-10-12.json：decision=rework，reviewedHead=9f2f19d，integrationStatus=not_integrated。这是直接Codex Desktop受管chat的审核，以真实thread/交付turn/HEAD绑定；该任务没有旧CLI队列manifest/JobDirectory，不伪造runId或调用旧队列Review入口，不直接写其review.json。待公共接线及原生工具缺失不是打回原因，下述实际产品失败才是。

M独立重跑原模块43项，全部通过（源tmp/m-e2-browser-directed.log）。用户要求扩大测试后，新增独立审核工装scripts/browser-review.ps1及browser-review/Program.cs，按显式App工程生成隔离测试项目，输出只能在目标树tmp内，核对评审前后真实HEAD；不更改产品源码/旧断言、不运行模型。33场景：14无头真实控件、19真实WebView2 SDK，最新结果25 PASS/8 FAIL。重复ID/null恢复各1FAIL，初始化顺序1、提前缩放2、查找取消1/重开1、提前停止1；同一缩放根因两个用户路径分开测，不当两个独立根因。

最终命令（从主仓库显式workdir运行）：

```powershell
pwsh -NoProfile -File scripts/browser-review.ps1 -ApplicationProject C:/Users/mio/.codex/worktrees/1dd7/Fufucode/src/Fufucode.App/Fufucode.App.csproj -Mode Headless
pwsh -NoProfile -File scripts/browser-review.ps1 -ApplicationProject C:/Users/mio/.codex/worktrees/1dd7/Fufucode/src/Fufucode.App/Fufucode.App.csproj -Mode Native
```

Headless14/2失败、exit1，tmp/m-e2-browser-restore-final-red.log，源证据tmp/browser-review-5c12cbf9beee4dbebf2ebcc6cb6821b4。有效条目的原ID/草稿必须保留，不能用全部丢弃配置实现“没崩溃”。Native19/6失败、exit1，tmp/m-e2-browser-native-final-red.log，源证据tmp/browser-review-e411efca5d584c8ead179340d911d6c1；SDK根fufucode-m-browser-review-9a35bf4a87434dd8932662fb2d529ba7，仅临时文件/本机环境。工装构建默认0警告0错误，未抑制NuGet审计。Headless最终之后仅新增Native Stop场景，原14行为没有变；两个部分合计33，不把不同版本测试结果混为最新全套通过。

实际失败：

1. RestoreState循环savedTabs.Count，ResetTabs去重后restored更短，重复ID抛ArgumentOutOfRangeException；null记录取Id抛NullReferenceException。需要过滤/成对归一化并保留合法原条目，不静默丢全部数据。
2. 控制器创建前先Navigate旧HTML、再NavigateToDocument新HTML，真实document.title仍M_PENDING_OLD，最后请求被优先_pendingAddress覆盖。该缺陷继承旧实现，但本任务的连续初始化/预览路径仍须修。
3. 创建前用户缩放175%、新tab140%，真实controller.ZoomFactor两次都是1；ready后再调缩放、两个ready tab独立缩放均通过，定位为待创建状态丢失，而非整个Zoom API失效。
4. 已就绪页再次发FindNext立即Close，跨过全部轮询后FindStatus被迟到回调写回2/2；关闭条确实隐藏，不等于业务提示取消。
5. 关闭查找后换成只有1个marker的新真实DOM，再开同query，原Find缓存未失效，SDK仍(2,2)，旧计数不能证明新查找。早轮仅看MatchCount=2会误判，已改成新文档不同匹配数，保留早日志并说明，未放宽断言。
6. 控制器未就绪时CanStop=true且真实Stop已受理，初始化结束仍加载M_PENDING_OLD。取消必须阻止待发导航并收敛IsLoading。

通过场景包括空列表/未知活动ID/重复恢复、有效草稿、tab隔离/关最后一tab、复制实际地址、缩放上下限、缺文件/拒绝javascript保持真实DOM、ready后的独立Zoom、只释放关闭tab、真实旧父窗口销毁期间初始化能重挂、Dispose在创建中无残留controller。SDK调用不冒称真实指针/IME/DPI/多显示器；F1-BROWSER-PARENT-1仍等修复模块收编后的完整父HWND/DOM往返门。

额外源码发现：默认DownloadStarting仍将ResultFilePath设到真实系统Downloads且Handled=true、不设Cancel，继承旧行为，与任务书“默认不自动下载”及交付表述不符。未触发真实下载，避免向用户目录写文件；标静态open。修复限本包默认取消/清晰状态，不要求下载管理新功能；仅修复后在临时/loopback验证取消，不能把未跑下载记为PASS。

修复仍原chat/原L4/原模型/原1dd7，保持7提交及原状态，小步追加，不修改M测试、main公共表/设置/Core或全局记录。修完原43项+本33项、默认App全量/solution正常零警告错误及适用原生模块工装，交新HEAD/完整卡后停写；M再独立复核和公共接线。没有收编9f2f19d，不以本轮测试/SDK部分通过标整项E2验收。

M审核工装/记录真实mainSHA eb1ae8dd40714023e04770271e17d592ac1934aa（不是浏览器模块集成SHA）。一次完整返工消息已成功送达原chat，游标:11 active/inProgress，真实修复turn01a130ea-bfd6-72b3-bde2-f12ac77c7b30。随后只观察状态，待新交付停写才重新读/跑其源码，不重复派发。

## 第二十一轮：修复回卡复核与相邻状态测试（16:41 UTC）

原chat游标:12 idle/completed，修复交付turn01a130ea-bfd6-72b3-bde2-f12ac77c7b30。源0974ff52547c1c5b7bc90d972074bddf84c20ef8干净，4个追加提交/4文件允许范围；无树专属App/dotnet/pwsh写入者、旧门房原PID不在，才开始读源/卡。没有继承主线或修改M工装。

M独立运行原33项全部PASS（14/0、19/0），tmp/m-e2-browser-rework-review.log，源证据tmp/browser-review-4084c2af7c05479dbd6d51e8134c9acf。不是仅采信卡。M独立运行源App --native-module-selftest退出0，tmp/m-e2-browser-native-rework.log；真实parent/DOM/整栏浮动/隐藏重开/下载Cancel/无落盘/查找重开均通过。旧七项已复核修复，但未集成产品、未宣称原生指针/IME/DPI。

按用户“多构建几个测试”继续覆盖修复相邻路径：新增5场景后工装38项，其中15无头/23实际SDK，35 PASS/3 FAIL。源码冻结0974ff5。Headless15/1见主区tmp/m-e2-browser-adjacent-red.log；Native23/2见tmp/m-e2-browser-history-red.log及源tmp/browser-review-33849341e46342989e760956fcca61f2。HEAD前后一致，工装默认构建0警告0错误。额外后退查找两项通过（真实先3匹配，再Back后2匹配），不因源码猜测而登记假失败。

新实际失败：

- NormalizeTabs依出现顺序生成tab-restored-1，若后面的合法原ID就是tab-restored-1，则原条目被重命名，ActiveTabId命中前面坏ID条目；实际选中blank-draft而不是selected-real-draft。必须先保留全部合法原ID再分配新ID，选中页保持对应原条目。
- Stop未ready时只改StatusText，原tab.IsLoading仍true。真实Stop已经受理，等待初始化之前也必须收敛loading，不能仅等初始化终态通过。
- Stop未ready后立刻提交新的有效导航，_pendingStop未撤销；初始化把这个新请求也丢弃，最终真实document.title空。旧被取消请求仍应不加载，但新用户请求须可继续。

结构化决定为E2-browser-review-rework-2026-10-12.json：rework/0974ff5/not_integrated。修复仍原L4、group/auto-deepseek-v4-1-flash、chat和1dd7，新增问题未升两级，不换模型；M工装38项保持，修完再卡停写。源SDK绿色不是main与通知增量交互验收，不提前公共接线或重新打包。

工装/第二轮审核真实mainSHA b52fac539694507f84485a505a6a85a39a659534。完整三点返工/38项门一次送达原chat，游标:13 active/inProgress，真实修复turn01a13101-3a92-70b2-a094-cc344d72adc9；不读取继续活写源码或重跑其半成品。
