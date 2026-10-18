# E3 未结束插件请求的精确只读核对

第52轮，E3-M-HOST 的L3增量，M直接负责公共模块。起点main `16bf9b0a6a1af64d24275d0da238fa52b7a6e90b`，实际产品提交 `13b331a766b65e9e72d82329da09e9bd585bc5fa`，七源/自测+记录/缺陷登记，共九允许文件。本轮不派新执行会话；原R1/E3停写、HEAD和游标不变，无新交付。未知Sandbox PID15712仍原出生，安装门继续defer。

## 范围与前置验收

异常退出前尚未收到回执的请求只有持久InFlight的token/version/time。当前页面只显示版本ID，回执区隐藏，用户不能按原请求核对。这是推进安全处理残留在途之前的实际可用性缺口。先在真实未返回宿主回调上确认没有回执，再检查真实编译页面能否显示原请求。

本增量只投影原InFlight登记，并提供绑定原plugin/token的只读加载观察。Loaded/NotLoaded/Unknown/Stale不冒充原请求结果，不能据NotLoaded解除保护。保留目录精确字节、原token/时间/选定/pending/回执及副本；不调用Abandon/Apply/Restart、不合成回执、不自动重发。实际安全释放仍须证明原回调不会再产生副作用，当前持久格式未记录持有者寿命，不能靠新宿主未加载或人工说明伪造证据。

允许范围：Plugins投影/只读服务/VM/原Section、App定向自测与注册、本记录/缺陷登记/进度/决策/台账。保持设置、辅助栏、已有回执区布局与R1冻结来源。验收：原产品可复现页面缺口；精确原身份和同版本不同token可分别核对；无token/已结束不回退别的请求；期间目录改变/迟到选择拒收；真实编译绑定与两尺寸结果可访问；solution零警告错误、App全量零失败及原真实Pi/模块UI回归。只临时数据，不请求模型。

## 实际行为与反例

原产品 `tmp/m-cycle52-entry-red.log` 一FAIL：真实未返回回调的InFlight有原token但没有回执，编译页面没有对应列表。保留该红证据，不把等待中的正文或任务卡当交付。

同一原插件页增加“未结束请求（受保护）”，只读核对按钮及结果放在该区历史/详情之前。原token、版本、登记时间可复制；同版本多请求在列表中显示各自原token，不能用可变pending或最新回执推断归属。新 `InspectInFlightAsync` 独立读取原登记，不合成ApplyReceipt；无token、token被改动、已结束登记不回退别的请求。读取期间目录/登记变化返回Stale；宿主异常/身份未知/不兼容/固定副本篡改保持Unknown。同包其他文件不能证明原入口加载。

VM沿用原忙碌/生命周期，异步结果必须匹配所选插件与精确token；同版本换请求、关闭后的迟到观察拒收。销毁后入口禁用。完成后刷新移除在途投影，展示服务真实生成的原回执，而不是把只读观察写成成功。持久schema没有改变。

## 本轮验证

最终App全量1305通过、零失败exit0。原页缺口一红后修复，新未结束请求32检查/原共享52+回执18，定向总102零失败exit0；最终solution零警告错误exit0。插件原服务166、proof规则21、模块无头UI46通过exit0；最终14真实Pi外壳检查通过exit0。Core/Pi协议未变，不机械重复其第50轮已过的全量，旧证据不作本轮新证据。

| 本轮命令/产物 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终0警告0错误、exit0 | `tmp/m-cycle52-dispose-build.log` |
| App Release DLL `--selftest --local-plugins-only` | 最终102通过（新32），0失败、exit0 | `tmp/m-cycle52-dispose.log` |
| App Release DLL `--selftest` | 最终1305通过、0失败、exit0；最终销毁守卫之前的1304零失败日志独立保留，不作最终源证据 | `tmp/m-cycle52-app-final-full.log`、旧`tmp/m-cycle52-app-full.log` |
| App Release DLL `--selftest --local-plugins-real-only` | 最终14通过、exit0；先前构建14通过的日志另保留 | `tmp/m-cycle52-shell-final-real.log`、旧`tmp/m-cycle52-shell-real.log` |
| 插件独立Release DLL默认入口 / `--ui` | 原服务166+规则21 / UI46通过、exit0 | `tmp/m-cycle52-services.log`、`tmp/m-cycle52-ui.log`；销毁守卫后UI再次核对见`tmp/m-cycle52-ui-final.log` |

DLL使用对应工程 `bin/Release/net10.0/`。同App的各轮验证按构建/定向/全量串行；独立模块UI可并行，但未并行重建任何运行DLL。32新检查涵盖真实未返回回调的持久原身份、Loaded/NotLoaded与操作结论分离、同版本双token、篡改/不兼容/未知/宿主异常、损坏目录原字节保持、预取消、原页编译绑定/复制/两尺寸稳定视口、忙碌/换token/关闭/销毁守卫，以及完成期间Stale和真实回执刷新。

M已查看最终销毁守卫构建的两图：`C:/Users/mio/AppData/Local/Temp/fufucode-plugin-inflight-c5c5dd2b73764d11885e9d7078eb2339/inflight-1100x800.png` 和同目录 `inflight-800x640.png`，核对按钮/完整结果均可见。页面宿主是明确离线Unknown夹具，不能当真实Pi加载或原生键鼠证据；真实Pi回归另算。此前31条检查构建的两图亦已查看并保留，未拿旧像素替代最终构建证据。

工装错误如实保留：`tmp/m-cycle52-build.log`两编译错误是缺Avalonia扩展命名空间；`tmp/m-cycle52-targeted.log`一异常是误把实际TransitioningContentControl读作Border。分别按真实using/Control修正，不放宽几何预期，原日志不覆盖、不抑制警告。先前全量运行时只编辑源码，等该进程退出再重建最终DLL，不在测试中途覆盖二进制。

## 未验证与下一步

本轮只读观察覆盖运行中回调及持久原身份投影，不是进程崩溃恢复验收。当前登记没有原宿主/持有者生命周期证据；单个新快照NotLoaded、时间过去、没有当前UI忙碌或人类输入reason都不足以证明旧回调无未来副作用。未接Abandon按钮，未释放保护或删除副本。下一M必须先建立并验证可绑定原token的真实宿主终止/持有者证据，再考虑显式安全释放；旧登记无证据继续保留。

系统原生指针/键盘/选择器/IME/DPI、压力/迁移及包含当前源的安装仍未验证，插件整项与生产六门pending。R1原冻结cc44eee不含后续插件；未知Sandbox保持、不杀或接管，原会话仅在验证槽证实空闲后有限续验原setup，不追main重建。

M持有主区可续期独占PID5808，编辑/提交前活检续期。原demo结果/全部未跟踪文件不暂存；无新派工或未知会话接管、不push、不改写历史、不清理树、不登录采购部署。全部模型机制测试未发云请求，没有昂贵测试lead。
