# M 收编与公共接线：E2-T 终端

任务E2-T/L4/group/auto-deepseek-v4-1-flash，原chat01a1301a-127c-7e81-a841-81a552c58864/local、ba55树冻结2b6a74ca4da8c1ae048ed36fd9e7cd05a2b51e5c。M已按M-E2-terminal-review逐轮独立复核，两次返工保留原会话/模型/历史，没有接管未知会话或读活写入者正文。

decision=accept，reviewedHead=2b6a74ca4da8c1ae048ed36fd9e7cd05a2b51e5c；源13提交/10文件依次cherry-pick到main，最后收编SHA690fbe539fab5fdfdfaae0040e068dd01583db63。F1宿主先行SHA4275373。原树及卡不修改、不清理、不push；accept模块与本M共享接线提交分别记录，不冒称整项原生验收。

主线行为：输出拖选/冻结快照复制、完整组合字符/宽格/ZWJ/旗帜边界、输出查找与前后定位/关闭返回、返回最新输出、每tab100条输入历史/原未发送草稿恢复、粘贴合并草稿且显式运行；tab切换/页面隐藏移挂/退出后输出仍能读取。剪贴板await绑定发起tab，关闭后丢弃，不改投或抢另一tab焦点。

M公共接线：七个terminal.*动作进入实际设置目录、命令面板、动态可用性与同一绑定表。默认复制Ctrl+Shift+C、粘贴Ctrl+V/Shift+Insert、查找Ctrl+F、输出复制或中断Ctrl+C；新建/纯中断/回底可自行配置。改绑后的旧手势不能复活，禁用无默认回退。未绑定的控制码仍是shell原生输入，不再偷走绑定表判断；命令框文字已选中时Ctrl+C保留编辑器复制。浮动窗口使用自身焦点和相同动作目录；copy/find提示同步用户绑定，按钮“中断”不写死Ctrl+C。独立控件无组合根时保留已验证的默认回退，避免破坏模块自测。

验证：

- M源六反例PASS/solution0警告错误/App893断言0失败/真实ConPTY0失败，日志和320DIP截图检查见review记录；没有信任交付卡口头结论。
- M主线公共接线定向16条全部通过，tmp/m-e2-integration-target.log；随后加无选区不可用原因断言及刷新，最终全量包含17条接线检查。
- 主线默认全量初次938项ok/0失败，tmp/m-e2-final-main-selftest.log；当时solution并行复制与自有Pi.Tests进程36036冲突，MSB3026共5条警告，不将此构建记为0警告。Pi退出后串行重构并复验最终增量，实际结果及SHA完成后补录。
- 同轮Core全量340 PASS、exit0，tmp/m-e2-final-core.log；Pi全量21 PASS、exit0，tmp/m-e2-final-pi.log。协议夹具与真实无害query，不请求付费模型对话；Core真实对话开关为0，Atria真实验收未启用并SKIP，不将可选云SKIP算模型对话已验。
- 最终solution/App全量/主线真实ConPTY：tmp/m-e2-acceptance-build.log、selftest.log、conpty.log，完成后追加实测结果。

尚未验证：Windows原生指针/系统剪贴板/真实IME/跨DPI、长交互shell在浮动时持续运行、特殊shell全屏应用；无头与真实ConPTY脚本均不能代替这些。不是已重打安装包、干净环境安装或OMP打包完成。源处于保留停写状态，只有明确新修复才恢复，不自动开始下一任务。

下一步M推进旧浮窗的通知/用量页签迁移与三渠道，独立浏览器完整基础路径可开受限新任务；浏览器原生父HWND风险必须处理，不以F1布局PASS代替它。C3唯一库客户端/完整返工续接仍待，产品扩展不等控制台全部完工。

最终门禁已完成：solution0警告0错误、默认App939项ok/0失败（含17项公共接线、29项F1及96项终端组）、真实ConPTY0失败，三份acceptance日志均exit0。Core340/Pi21本轮全量通过；不覆盖旧失败/警告日志。M即提交共享接线，实际SHA随后追加；这证明当前主线接线，不解锁尚缺的原生/安装/浏览器验收。

实际M共享接线mainSHA6244c9157e9c6d18d4e6d16eed59e7ff1a2cd65b，integrationStatus=integrated。原chat已收到包含真实SHA与本记录的只读保留停写要求；未让原树自行整合或开始新任务。整项原生未验保持。
