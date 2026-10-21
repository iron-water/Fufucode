# 设置台—前端分级—实际功能：接线规范化记录（2026-10-20）

## 结论

本次把三层关系收敛为一条可验证链路：

```
设置目录（SettingsNavCatalog / SettingsPageViewModel）
        ↓ 选择分类或功能状态页
显示分级（WorkbenchActionDefaults / WorkbenchCustomizationViewModel.Pages / ui.json）
        ↓ 真实动作、停靠页、插件页
运行时（AppActionRegistry / WorkbenchPageRegistry / PluginPackageManager / 工具注册表）
```

设置页不再拥有第二套页面清单、第二套更新实现或“未接线占位页”；插件状态跨宿主实例通过 `PluginStateNotifier` 收敛。

## 已修复的确定断链

| 问题 | 修复 |
|---|---|
| 设置内层搜索只过滤分类，不过滤页面行 | `SettingsPage` 绑定 `SearchText`；`SettingsLayout` 行构件写入 `SearchTerms`；`WorkbenchSectionsView` 递归过滤并保持组头显隐 |
| 二级设置页（外观排布、通知提示等）整体不可见 | `ShowWorkbenchSection` 按分类判断，不再只认分类首页 |
| 窄窗设置页布局越界崩溃 | 响应式重排改为移动 `SettingsContentHost`（`SettingsBody` 的直接子级），不再改内层 Grid |
| 显示空的停靠区开关无效 | `SetZoneVisible` 在空区补默认页，避免“空区不收起”再次隐藏 |
| 自动布局同步污染撤销栈 | `SynchronizeLayout` 直接落盘，不进入用户撤销历史 |
| 插件页动态动作不进快捷键目录 | `SyncShortcutCatalog` 在动作注册表变化时重建快捷键目录 |
| 功能状态页“前往插件市场/工作台模块”只改子页 id、不切分类 | `SettingsPageViewModel.NavigateTo` 统一处理分类与子页 |
| 默认插件安装失败被写成“用户已卸载”，后续不再播种 | `Deploy` 只在存在显式状态时回滚；开发构建从 `out/plugins-dist` 或项目输出找载荷 |
| 空停靠区回填默认页时不检查其他区域，导致同一控件两个视觉父级 | `EnsureDockHasPages`/`SetZoneVisible` 只补未占用页；全部占用时移动首页 |
| 未激活的裸 Subagent 调度器仍改写底层权限链 | `INativeSubagentDispatcher.IsActive` 门禁，仅插件运行时为 true |
| 默认插件基线测试仍按旧 `Surface` 轴断言 | 改为真实 Metro/LiquidGlass 皮肤断言 |
| 会话岛固定 8 个磁贴，忽略用户位置/顺序/隐藏 | `BuildIslandPanel` 按 `settings.Actions` 的 `SessionIsland` 位置与 `Order` 渲染；任何动作都可入岛 |
| 辅助栏设置按默认位置筛选，用户移入/移出后无法管理 | `IsAuxiliaryCandidate` 读取当前生效位置 |
| 状态栏默认缺少用时/项目/分支 | `status.duration/project/branch` 默认放入 `StatusRight` |
| `help.update` 与设置“关于”是两套更新实现 | `help.update` 改为进入“关于”并调用 `CheckAndUpgradeAsync` |
| `perf` 在动作快照之后注册，设置里不可配置 | 插件页变化后重新 `SetActions`；动作注册表支持动态注销 |
| 插件页/内建页壳 ID 冲突会删除内建 `automation` 页 | 页面注册区分 core/optional/plugin-owned；插件失败只记录原因 |
| 插件页 ID 在 `ui.json` 解析和布局规范化时被静态清单丢弃 | 布局结构规范化保留所有现有 ID；组合根用真实 `KnownPageIds` 裁剪 |
| 设置页安装 Seep 嵌入插件必然失败 | 嵌入插件走状态安装/卸载，不要求物理载荷 |
| 设置市场安装后工作区宿主仍持旧插件快照 | `PluginPackageManager` 订阅 `PluginStateNotifier` 并重载；设置市场行同步刷新 |
| 市场列表“安装”按不可用载荷失败 | 测试夹具补真实 manifest；嵌入插件有明确分支 |
| 通知页称“系统通知尚未启用” | 文案改为指向“会话与消息 → 通知提示”，并增加直达渠道行 |
| 动作图标静默回退 `AppsList` | 新增 `ExplicitIcon`，R05 自测对 `null` 失败 |
| 死代码 `SettingsNavLayout.Build`、`ShowLookColumn`、`ShowArrangementColumn`、`ShowThemeRow` | 删除，测试改用真实生产路径 |
| 重复动作 `session.share`、`copy.session`、`help.status` | 删除，保留有真实实现的导出/复制/诊断动作 |

## 新增守卫

- `WorkbenchActionDefaults.ExplicitIcon` + R05 自测：所有注册动作必须有显式图标。
- `WiringIntegrationTests`：设置页安装/启停 → 独立运行时管理器 → 页面贡献和工具注册同时收敛。
- 设置测试新增：动态插件页布局恢复、`ui.json` 插件页保留、状态栏默认指标、动作图标映射。

## 验证

| 命令 | 结果 |
|---|---|
| `dotnet build Fufucode.sln -c Debug --no-restore` | 41 项目，0 错误，0 警告 |
| `dotnet run --project tests/Fufucode.App.Settings.Tests -c Debug --no-build` | 115 项断言全部通过 |
| `dotnet run --project tests/Fufucode.PerformanceMonitor.Tests -c Debug --no-build` | ALL PASSED |
| `--selftest --settings-only` | 0 项失败（含 420/1280 DIP 响应式、草稿、保存回退） |
| `--selftest --shortcuts-only` | 0 项失败 |
| `--selftest --r3-only` | 0 项失败 |
| `--selftest --r05-only` | 0 项失败 |
| `--selftest --tabs-only` | 0 项失败 |
| `--selftest --workbench-only` | 0 项失败 |
| `--selftest --steer-keyboard-only` | 0 项失败 |
| `--selftest --background-only` / `--archive-only` / `--notification-page-only` / `--browser-integration-only` / `--floating-workbench-only` | 0 项失败 |
| `dotnet run --project tests/Fufucode.App.Backend.Tests -c Debug --no-build` | 843 项断言全部通过 |
| `FUFUCODE_SKIP_UI_GROUP=1 dotnet run --project tests/Fufucode.Core.Tests` | 通过；UI 组受本机 `hostpolicy.dll` 路径限制，单独跳过 |
| `tests/Fufucode.App.RemoteControl.Tests` | 139 项断言全部通过 |
| `tests/Fufucode.Harness.Tests` | 51 项检查通过 |
| `tests/Fufucode.App.Plugins.Tests` | 21 项检查通过 |
| `tests/Fufucode.PerformanceMonitor.Tests` | ALL PASSED |

## 仍然存在（未在本次强行改）

1. `SettingsPageViewModel` 仍约 1,900 行，分类可见性仍是字符串 switch；后续应按页面注册表拆分。
2. `WorkbenchLayoutState.KnownPages` 仍保留内建静态清单；插件页已不被丢弃，但“页面是否存在”的裁剪仍依赖组合根。
3. 后端选择现在显示插件安装/启用状态与回退说明，但选择本身仍允许写入不可用后端（产品既定回退语义）。
4. 通知策略与通知渠道仍是两份持久化真源（`notification-preferences.json` / `notification-delivery.json`），页面已互相直达，未合并存储。
5. `WorkbenchAppearance` 保留旧浏览器字段，只用于一次性迁移。
6. `--sessions-only` 仍有 2 项失败：默认管理页四类顶级分类与 Toolbar 单 Grid 断言，来自并行会话对 Overview 的重构，本次未覆盖其文件。
7. `Fufucode.Hosting.Tests` 的 `survivor` 断言仍失败（活跃 writer 阻止 resolution），属于既有宿主语义问题，未在本次扩大修复范围。
8. 完整 `--selftest` 在 R05 中段以 `EXIT=127` 中止；分组运行全部通过，怀疑与本机无头进程资源或并行构建替换 DLL 有关，尚未定位到代码根因。
9. `--local-plugins-only` 在 70 项通过后以 `EXIT=127` 中止，未产生异常堆栈；需要单独的环境级排查。
10. `TestApp` 移除了失效的 DeveloperTools 引用；它仍是旧模板工程，不属于产品入口。
