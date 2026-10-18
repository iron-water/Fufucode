# 轨道 F 任务书：M7 发布工程（安装器、完整性、升级、诊断）

> MVP 与 M5 已达成。本任务是 M7 的工程实现主体（终验由协调者与轨道 D 的收尾投放完成）。依据：计划 §10 M7、§12、`docs/decisions.md` [轨道F]（升级/回退设计已定）、你的打包 spike（`scripts/packaging/`）。开工前 `git merge main`。

## 范围（只允许写这些）

- `scripts/packaging/`、`labs/packaging/`、`docs/archive/progress/progress-f.md`、`docs/verification.md`（本轨验证段）
- Core/Pi 需要改动（如诊断导出的 Core 接口）→ 记 progress-f.md 交轨道 A

## 任务（结果定义）

1. **安装器**：Inno Setup 脚本（消费 `assets/installer/` 的素材——轨道 G 交付前用占位图并记录），`PrivilegesRequired=lowest`，安装布局对齐你的 spike；产物 + 干净环境安装/卸载验证。
2. **完整性树**：发布后全文件 SHA256 清单 + 运行时校验（参照研究 §5.4 的做法），崩溃报告附带校验结论。
3. **升级/回退流程落地**：按 [轨道F] 设计——`runtime/<version>` 并存、用户数据区隔离、协议兼容探针（`get_state` + `get_entries` since）、升级验证门。
4. **无凭据诊断导出**：一键导出诊断包（版本、运行时清单、日志脱敏摘录、校验结论）——Core 接口找 A，你做打包侧。
5. **验证**：`verify-clean-start.ps1` 扩展为完整发布验证（安装器产物 → 安装 → 启动 → selftest → 真实 Pi 无模型查询 → 升级演练 → 回退演练）；测量数据按 §12 口径复测记录。

## 验收

- 从安装器产物在干净目录完成：安装 → 首启（selftest 绿）→ 无模型 RPC → 关闭；升级与回退演练各一次有证据。
- 全部证据、测量、未验证项入 progress-f.md 与 verification.md。

## 本轮派单（2026-09-30，协调者，Maka 派发）

在 track-f-packaging worktree（track-f 分支）执行；先 `git merge main` 取最新。ISCC 在 `C:/Users/21894/AppData/Local/Programs/Inno Setup 6/ISCC.exe`。

**真实安装器升级/回退演练**：产出两个版本安装器（`/DAppVersion=0.9.0` 与 `/DAppVersion=1.0.0`，产物重命名区分），依次演练：装 0.9.0 → 校验已安装布局/启动（selftest）→ 装 1.0.0 覆盖升级 → 校验布局与 integrity.sha256.json → 卸载。全程记录证据（安装日志 /LOG、已安装目录清单、selftest 结果），写 docs/archive/progress/progress-f.md。若 .NET 运行时或 ISCC 缺失导致无法演练，如实记录阻塞并保留中间产物。不做 git commit/merge——改动留工作区由协调者代提交。

