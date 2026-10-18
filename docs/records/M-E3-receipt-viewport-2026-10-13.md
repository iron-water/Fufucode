# E3 回执核对结果的稳定可视区域修复

第51轮，E3-M-HOST 布局增量，L3，M直接实现共享页面并独立核验。起点 main `a6a0a73ef09c092fabbfb24bdf4c438c3ff95d95`；实际产品提交 `82a240649bd390140a1b5650735340ea7ba001d4`，只含两源/自测文件及本记录/缺陷登记，共四允许文件。

## 复现与判断

第50轮只读核对的绑定与立即几何检查通过，但M实际查看PNG没有结果面板。本轮在同一真实编译页面增加两次渲染tick及有界等待，再检查稳定后的视口，未修改产品前明确一条FAIL，保存在 `tmp/m-cycle51-scroll-red.log`。

进一步记录显示：结果顶部从立即测量的Y504移动到Y581，页面可视底部Y555；ScrollViewer的Offset从505变成428，Extent538×1347、Viewport538×467均未变化。`tmp/m-cycle51-offset2.log`仍为同一失败。可确认延迟布局/滚动结束后结果被移出视口，不能解释为仅截图旧帧；尚未确认滚动变化的内部触发源。红日志当时断言名含caret，是诊断假设，不是已证明原因；最终断言名称已改为布局与滚动稳定。

## 最小产品改动

只调整原插件回执区的排列：标题→只读核对按钮→当前观察结果→回执列表→原回执详情。操作与结果不再排在可变长度的历史/详情之后。原历史选择、精确token、只读可复制详情及目录/回执不改写契约保持。没有改辅助栏、设置页或全局滚动策略，也未增加自动重发、应用或解除在途保护。

新增三个有意义的强断言：延迟滚动结束后完整结果仍在视口；原详情仍可滚动访问并保原token/只读复制；800×640最小窗口中完整结果可见。原立即几何、绑定、目录字节不变、调用次数及迟到/并发变化断言保持。

## 本轮实际验证

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0警告、0错误、exit0 | `tmp/m-cycle51-final-build.log` |
| App Release DLL `--selftest --local-plugins-only` | 原52+回执18=70通过，0失败、exit0 | `tmp/m-cycle51-final.log` |
| App Release DLL `--selftest --local-plugins-real-only` | 14真实Pi外壳检查通过、exit0 | `tmp/m-cycle51-shell-real.log` |
| App Release DLL `--selftest` | 1273通过，0失败、exit0 | `tmp/m-cycle51-app-full.log` |
| 插件独立Release DLL `--ui` | 46通过、exit0 | `tmp/m-cycle51-ui.log` |

同App DLL的定向、真实Pi和全量顺序执行；没有在测试运行时重建该DLL。仅临时目录和明确离线proof/devin/swe-2 provider，没有云prompt或昂贵验证模型。Core/Pi协议与服务未变，本轮未重复其第50轮已经通过的全量，不把旧结果计作本轮新证据。

M实际查看最终两张渲染图，均能看见核对按钮和完整观察结果：

- `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-receipts-b165c8caa3d3484c823a61461e96fb58/receipt-page.png`，1100×800。
- 同目录 `receipt-page-minimum.png`，800×640。

这是Avalonia无头像素证据。页面夹具的宿主unknown结果不能冒称真实插件加载；真实Pi查询另由14条外壳检查证明。像素、编译绑定、真实Pi和系统原生输入四种证据分开。新断言在原产品先红、最终修复后绿，不放宽原预期。

诊断工具两次编译失败原日志保留：`tmp/m-cycle51-diagnostic-build.log`中误将StyledElement当Control；`tmp/m-cycle51-offset-build.log`中误用不存在的AnchorCandidate。分别修实际类型与使用安装包公开的CurrentAnchor；没有抑制警告。最终构建结果如上。

## 监督、边界与下一步

原R1/E3均notLoaded、最新turn completed、cursor/HEAD未变，无新交付、不派重复任务或向停写会话发送轮询。R1继续冻结cc44eee，尚不含后续插件；未知Sandbox PID15712及出生一致，保其状态，不停止或接管，安装门仍defer。

M续期独占主写入租约PID37840，编辑/提交前活检。原demo结果与全部继承未跟踪文件不入暂存；不push、不改写历史、不清理工作树、不登录采购部署。

E3-RECEIPT-PIXEL-1仅本次稳定无头像素边界可标verified。系统原生指针/键盘/选择器/IME/DPI、残留在途的安全明确释放、长稳/迁移和包含当前源码的安装仍未验证；插件整项及生产六门仍pending。下一步M按宿主证据门推进残留在途核对/明确处理；当前NotLoaded本身不能证明可安全解除保护。R1只有证实原生槽空闲后才有限续验原固定setup，不重建追main。
