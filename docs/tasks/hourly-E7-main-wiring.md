# E7-M-WIRING：主控皮肤公共接线

负责人M，难度L4；当前主控自做共享改动，不新增执行会话、不占第三实现槽。2026-10-14 UTC核查main `31a8402b0acb4a8d345f734164a9342dbae56e0a`。本次仅准备接线任务，不表示已实现；实际实施起点在模块收编时另记。

## 开工条件与当前事实

E7-ENGINE停止、交付卡绑定真实HEAD、允许范围及必要机制/真实控件证据独立审核后，M才导入模块并按其真实API接线。模块待接线是有效交付。E7-DESIGN可并行继续；不为等待教师冻结Metro/M3接线。教师HTML和AXAML样板分别核查，不当作生产Aero完成。

当前源有三处需要协调，尚不是产品缺陷：

- `Infrastructure/WorkbenchAppearance.cs`已有独立Theme、Accent与Surface。Surface仅接受fluent/glass/flat/classic，影响现有透明与局部控件外观；不能直接拿它存Metro/M3而被Normalize退回，也不能破坏旧Surface配置。
- `Views/MainWindow.Customization.cs`已有唯一ApplyCustomization入口、系统配色事件、窗口RequestedThemeVariant及局部资源/样式。新增皮肤要明确与旧外观覆盖的顺序，不能通过清空Styles/Resources强行赢过原配置。
- `Views/MainWindow.DockFloating.cs`的RenderFloatingWindows当前复制主窗口直接Resources，并应用Theme/FontSize/Dock外观。合并资源字典和scoped Styles是否传播须真实验证；仅复制这层键不证明新皮肤已同步。

## 实施范围与顺序

1. 收编模块后记录其真实SkinId/应用租约/释放API及语义键，独立重建/运行模块测试，查看实际Avalonia帧。范围或行为不合格则原会话原模型定向返工；M自己的共享缺项不打回受限worker。
2. 在既有WorkbenchAppearance与Store加入独立稳定皮肤字段，默认Fluent；Theme仍是system/light/dark，Surface仍是旧材质选项。缺字段的旧文件保持Fluent；未知ID采用可解释的有效Fluent回退，原未知输入保留在原JSON，普通其他设置编辑不静默覆写它。沿用全局/项目稀疏覆盖、未知键保留和损坏文件禁止自动覆盖机制，不另写第二份皮肤配置。
3. 沿既有WorkbenchCustomizationViewModel及设置页外观区加选择项/说明，不复制设置页面。保存失败保原生效值并显示现有失败状态；只列已真实接入的Fluent/Metro/M3，Aero教师样板不混入生产目录。保留Windows内置设置式排布与所有既有开关。
4. 由ApplyCustomization统一协调旧材质、accent、密度/圆角与皮肤租约。按交付模块实测确定覆盖优先级，记录选择；Fluent回退须恢复旧材质效果。跟随系统明暗使用实际ThemeVariant变化，不能把Default一律当Light。
5. 新建和已存在的浮动页签/整栏窗口同步皮肤、明暗与accent。每窗口独立租约，更新前移除自身旧租约；关闭/停靠回主窗释放其引用，临时挂载不销毁页面VM。核查直接资源复制是否遮蔽字典新键并增量修正，不改通知三渠道/终端架构。主窗口关闭后不执行迟到皮肤回调。
6. 合并已接受教师规范，只修改明确视觉差异；Vista/Aero正式实现另设范围与证据。既有默认Fluent、辅助栏结构、极扁主底部状态栏默认关闭保持；皮肤选择不得打开状态栏或重置浮动布局。

## 必须验证的用户路径

| 路径 | 最低证据 |
| --- | --- |
| 旧配置、未知skin、损坏文件、项目覆盖/清除覆盖 | 临时数据根下忠实输入/输出及重启加载；无原用户数据写入 |
| Fluent→Metro→M3→Fluent重复切换 | 真实已挂树控件属性与帧变化、资源/Styles不累积；其他模块资源保留 |
| 明暗、system主题改变、系统/自选accent | 实际ThemeVariant与动态资源更新；不以仅保存字符串证明界面变化 |
| 先浮动再切皮肤、先选皮肤再浮动、关闭重开/回停靠 | 双窗口显示一致、独立租约/VM连续性、布局保持、关闭无迟到写入 |
| 设置选择与JSON编辑、保存失败 | 原绑定入口真实触发、有效值/错误状态正确、失败不污染已生效窗口 |
| 默认基线与800×640小窗口 | 设置/聊天/通知/辅助栏代表帧；状态栏仍默认隐藏，控件状态可读 |

适用代码改动后solution构建0警告0错误、App全量selftest0失败，模块独立套件与既有Customization/Floating回归通过。跨Core/Pi协议无改不机械重跑；候选打包冻结时再执行生产任务书要求的全量门。

无头控件/截图与原生键鼠/IME/系统DPI、真实WebView2/ConPTY是不同证据。原生不可用写notRun并继续其他独立项，不宣称皮肤整项验收。生产包必须包含此后实际公共源码，旧R1冻结候选不追溯纳入。

每个可验证增量小步提交，实际SHA/命令/结果/未验项记入M-E7接线记录及progress/decisions；新真实缺陷登记failure-registry。不push、改写历史、清理worktree、登录采购部署；用户意见原文件及继承改动不暂存。
