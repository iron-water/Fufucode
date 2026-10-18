# E3 安全应用的页面连续性

第57轮，用户本地2026-10-13继续指令；UTC记录日期2026-10-14。M直接实施L3公共外壳，起点main `449ea340531e8d0356f2aae282809cadbc4da69e`，实际产品SHA `6b87b735e8c7bb1bb70ec9e6f337e0be0b3a01f4`，三源/测试和任务/记录/失败登记，共六允许文件。任务范围 `docs/tasks/hourly-E3-plugin-page-continuity.md`。

## 产品缺口及改动

上轮像素工装误在Chat测插件按钮可见性，虽通过显式重新导航修好了工装，但同时暴露真实产品缺口：用户在扩展页执行安全应用/更新/失败恢复/回退时，AttachRealSession重建MainViewModel默认Chat，页面被移出树，用户看不到原动作的结果。本轮不把上一工装修正当产品已修，独立加入四项真实Pi用户路径断言。`tmp/m-cycle57-page-red.log`四FAIL，原34项实际行为仍通过。

最小改动：AttachSession接收初始AppPage，并在绑定外壳/PageHost前设定；AttachRealSession可明确保当前页面，只有ApplyExtensionsAsync成功、坏候选已恢复、取消但旧宿主仍活的三个重接分支启用。使用重接时实际当前页，不把动作开始时页签存成将来强制恢复目标。维护中用户选择Chat，迟到完成仍保Chat。正常启动/切项目/切会话和普通独立Restart仍保持原默认行为，设置/辅助栏布局不改。原Pi新连接/会话/草稿接线照常重建，不为了保页面沿用已释放旧Chat。

真实插件自测取消了上轮在明确应用旧版后由工装重新打开扩展页的补救，直接验证产品保持的原页面及控件挂树，再检查两尺寸动作可达。应用/更新/失败恢复/回退四项、明确恢复旧固定版后的页面额外一项共五新增。最终两帧M已看：真实扩展页挂载，原请求观察/明确结束动作可滚动到达；不是原生键鼠验收。

取消自测从真实扩展页开始，保原六种实际生命周期用例，再加维护Resume后受控延迟。此期间用现有公共导航切Chat，再释放真实宿主继续提交，确认原会话仍live、草稿保持、当前Chat不被拉回、用户能明确返回扩展页；预取消和信任拒绝仍保真实扩展页。候选尚未接受的取消/关闭仍不发布或复活连接。

## 验证

| 实际命令/入口 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0警告0错误，exit0 | `tmp/m-cycle57-build.log` |
| App Release DLL `--selftest --local-plugins-real-only` | 39 PASS/0 FAIL，exit0；原34+新5 | `tmp/m-cycle57-page-green.log` |
| App Release DLL `--selftest --local-plugins-cancel-real-only` | 33 PASS/0 FAIL，exit0；原27+新6 | `tmp/m-cycle57-cancel-page.log` |
| App Release DLL `--selftest` | 1388 PASS/0 FAIL，exit0 | `tmp/m-cycle57-app-full.log` |
| 插件独立Release DLL默认/`--ui` | 166服务+21proof / 46无头UI全部PASS，exit0 | `tmp/m-cycle57-services.log`、`tmp/m-cycle57-ui.log` |

M看过最终 `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-shell-54eb48e25261471ab9afdb14d4793888/reconciliation-1100x800.png`、同目录 `reconciliation-800x640.png`。本轮App外壳改动不影响Core/Pi协议/生命周期源码，未重复跑其全量；上轮366+1云SKIP/48属于历史证据，不冒充本轮新跑。生产六门完成时须再以最终固定候选运行全部门。

所有命令在真实主区显式workdir；只用临时数据、自有插件、实际Pi及proof/devin/swe-2明确配置，无云prompt/昂贵测试lead。同App实际插件/取消/默认全量串行，模块DLL独立并行。四红原日志不覆盖、不放宽断言。新五实际插件+六取消/切页检查共十一；默认全量计数不增加，因为这十一在真实入口，未伪称默认自测已覆盖真实Pi。本轮红/绿/取消三个专属根只读核对Node残留0。统计工装曾将foreach直接接管道导致PowerShell解析失败；改收集数组后统计，不冒充产品失败或改计数。

## 限制及监督

仅页面连续性与真实Pi绑定的有限增量；未做系统指针/键盘/IME/DPI、当前源码安装、长期压力或完整E3/生产验收。旧unknown插件原请求仍不自动解除保护。待验范围继续如实保留，不能把几十项额外green当整项目完成。

R1 idle及E3 notLoaded/latestTurn仍原completed，wait游标7f0ba842:13/5c7b503d:2 unchanged，真实HEAD4097328/8c67289保持，无新交付；没有重复审核/派发或续接未知会话。未知WindowsSandboxServer PID15712及出生保持，R1冻结cc44eee安装门defer，不停止或接管。main续期独占PID32552，编辑/提交前活检，继承demo脏改和全部未跟踪不暂存，不push/改写历史/清理工作树/登录采购部署。

下一步继续按产品交付推进实际可见结果/恢复入口与原生和固定候选门，正常已返回请求恢复现已收口；无返回/无退出证据的未知旧请求保持保护，不为制造可恢复结论扩大承诺。
