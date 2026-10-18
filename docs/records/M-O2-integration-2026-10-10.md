# M：O2第三轮复核与设置接线

日期：2026-10-10。状态：已整合待验证，不是原生M2整项验收。

来源：C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227，分支wt/20261010-072036，起点8cf3921，HEAD e8f1277，原执行会话停止写入。其代码基线与当前main并非完全一致：O2起点早于M接线后的main，按实际Git整合，不能把交付卡措辞当同步证据。

代码五提交收编：b94e0fb→f5febaf、1a49e47→ad91ed9、be708ac→581d082、fc95202→1aa282d、12ec793→0b91625；最终O2任务记录从e8f1277单文件采纳。中间记录补录不重复收编。主区用户demo结果和六项未跟踪内容未覆盖/暂存/提交，不push、不改历史、不清理worktree。

## 独立验证

- 源实际`dotnet run --project tests/Fufucode.App.Storage.Tests`：212通过、exit0，源tmp/m-o2-third-tests.log。文件符号链接/ACL拒读SKIP不升格为通过。
- 主区`dotnet run --project tmp/m-o2-review/Review.csproj`直接编译真实清理器源文件，临时根唯一。三个原反例：已有junction protectedExists=True/deletedFiles=0；更新日志exists=True/deletedFiles=0；预览后nested父目录换junction linkCreated=True/protectedExists=True/deletedFiles=0。主区tmp/m-o2-third-independent.log。
- 主区`dotnet build src/Fufucode.App -v quiet -o tmp/m-o2-integrated-app`：0警告0错误，tmp/m-o2-wiring-build.log。
- 主区`dotnet build Fufucode.sln`：0警告0错误，日志tmp/m-o2-wiring-solution.log。最终`dotnet tmp/m-o2-integrated-app/Fufucode.App.dll --selftest`：784项通过、0项失败、exit0，日志tmp/m-o2-wiring-full.log。全量新增实际Settings存储页承载、浏览路径/资料保护同源断言；212独立模块断言仍独立执行，没有虚构为App默认注册。

## 公共接线与限制

只选设置入口：新增“存储”分类承载StoragePage，不另外注册工作台storage页。清单使用应用数据根/webview/library，与既有落点一致；打开目录复用系统外壳，失败转页面状态。窗口销毁取消VM在途计量/清理，不把临时视觉脱离当销毁。首版日志保留默认14天，本轮页面调整不宣称跨启动持久化。

R-030与R-031已按三个稳定反例独立验证修复；不宣称路径逐级检查能抵御恶意并发替换的所有竞态（执行有祖先缓存，未做句柄级竞态保护）。所有清理测试仅临时夹具，未执行真实用户数据清理。

原生指针、IME、DPI、主题、真实外壳操作/目录打开、实际EBWebView布局和WebView2锁仍未验；真实网站登录态/离线内容前后未验。当前工具无computer-use所需node_repl，不以无头代替原生。没有模型云调用或打包安装验证。

## 下一步操作卡

原始O2 L2→修复L3（+1）：保留原主模型，不建议升级子代理。原O2可退出，保留工作树，原生验收由M组织；O1/O3也待原生，D1继续G1。不要要求终端长期等待。活跃执行状态未知，不自动开会话。O7仍依赖O3整项验收，G1/G2门未绕过。

直接转发原O2：

```text
任务：O2收尾｜原L2→修复L3（+1）｜保留原主模型。
第三轮修复已由M独立复核并收编，设置“存储”入口已接。读取主仓库docs/records/M-O2-integration-2026-10-10.md。
本执行会话可以退出，保留工作树和已有内容即可；无需持续开着等待。不同步/重置、不自行整合main、不push、不清理、不自动开始下一任务。只有M提供明确缺陷修复指令才继续本任务；原生验收未完成，不标整项通过。
```
