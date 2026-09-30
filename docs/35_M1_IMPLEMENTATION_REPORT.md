# M1 实现与验证（2026-09-29）

**M0 已按明确时钟限制下的可行性结论进入 M1；M1 主要实现已落地，完整退出验收尚未完成。** Debug/Release 构建0错误/0警告，55组默认自动测试通过；真实Windows的普通录制、父连接失联停采、Host崩溃恢复通过各自断言。真实mic、两小时双轨同步、锁屏/睡眠及平台会议没有因此通过。

## M0 的处理

没有修复或补造 Windows 返回的全零 device position。新的独立分类器依据实际 API QPC、帧数、flags 和连续性，确认已测 ProcessTree 路径为 **FEASIBLE_WITH_LIMITATIONS / QPC_ONLY**，可以继续录音器研发。[34](34_M0_CLOCK_DECISION.md)记录依据与推断边界。

本轮能力证据：`.local/diagnostics/clock-capability-20260929-031542-b0e4af1560ed49db93f05c0a9ecff46b.json`。旧原件、`Complete=false` 和1297项旧审计FAIL不变。M1 `StorageComplete` / `IsCleanEnd` 与时钟完整性分开；恢复返回的旧 `CaptureResult.Complete` 在设备计数无效时仍为false。mic/endpoint计数异常、非零回退等不能冒充已知ProcessTree全零限制。

## 已实现

- 普通录音与10秒试录分开；一个CaptureHost、一个采集线程及一个有界writer。默认未分类/时间标题、mic关闭；暂停/恢复、固定设备mic开关、单一书签、重复Stop及请求ID幂等。
- 控制在采集线程生效。先停相应来源，再处理截止前包/释放，确认后回执；跨界只保留完整前缀、后缀清零。尚未到达时间边界的包暂留有界内存，不提前写入。Stop排空writer、完成WAV/索引/manifest后才显示保存。Stop/Dispose失败不确认“已暂停/mic已关”。
- mic关闭期间不创建或读取mic流；重新开启只接受本场明确选择的固定设备，不替换失效设备。此隐私路径已有合成源测试，未启用真实mic。
- 连续30秒WAV分块，切块不重启capture；启停新建segment，持久化原始device/QPC/raw frame count/flags、保留偏移、chunk位置及实际格式。原轨头部成功准备后才Start。
- 约1秒checkpoint，保存PCM水位/hash、实际data offset、连续序号及data精确UTF-8字节hash；完整块校验并rename，最终原子写manifest。保留系统/磁盘缓存限制，不承诺断电零损失。
- 仅从磁盘重建及恢复；拒绝坏hash、重复属性、错序、越界、真实junction、格式不符、确认水位损坏、非法边界及模糊final/partial。只采用checkpoint覆盖的完整帧；恢复写新副本，原件不改。
- 保留暂停/无callback的经过时间及显式缺口；书签/控制事件独立验证，仅恢复合法且在确认时间范围内的前缀。
- UI等待对应回执才更新暂停/mic状态，录音/Paused/Stopping都禁回放；显示来源、连接、包/声音状态、时钟提示、checkpoint及磁盘。提供选单场检查和恢复副本入口，不自动扫描私人历史。
- 写盘错误/低磁盘明确停采，来源故障不扩大范围；父连接失联和IPC回压有界停采。长调度缺口后暂停提示恢复，不改电源策略。普通录音保存不等待后处理，之后可开始下一场。

普通录音完整混音、压缩、分段导出和便携包仍属M2，本轮只保留原有短试录混音。没有数据库、云API、模型、驱动或新增依赖；没有进入会议或继续暂缓的浏览器隔离测试。

## 已测试

环境：Windows **26200.9457 / 25H2 / X64**，PowerShell **7.6.5**，SDK **10.0.401**，NAudio **3.1.0**。实际采集均为 **ProcessTree/本次新建合成进程/mic=false**。WPF枚举1个输出、0个mic。环境文件：`.local/diagnostics/m1-20260929/environment.json`。

| 检查 | 实际结果 | 本地证据 |
|---|---|---|
| Debug及Release全解决方案 | 0错误/0警告 | `.local/diagnostics/m1-20260929/build-debug.txt`、`build-release.txt` |
| 默认55组自动测试 | PASS；含15组存储检查及生产录音线程的多种合成控制/故障子情形 | `.local/TestResults/M1/final/results.json`；公开Dev Test另见 `.local/TestResults/Current/20260929-034012-712/results.json` |
| 最终约40秒实录 | `PASS_WITH_CLOCK_LIMITATIONS`；暂停/恢复、书签、重复Stop，2段/3块、跨30秒、末端880Hz通过 | `.local/M1AudioIntegration/20260929-033743-9195b0e868254407acc6f64881bfbe1a/probe-report.json` |
| 真实15分钟数字静音 | `PASS_WITH_CLOCK_LIMITATIONS`；经过900.658302秒，31块/43227840帧；345822720字节PCM全部为零，原件hash不变，未提前结束，Host/子进程均正常退出 | `.local/M1SilenceIntegration/20260929-035237-0bf8e362b47643abaeb62117510a8730/probe-report.json` |
| 父IPC连接失联 | 未发Stop，Host自行停采/收尾；100800帧，原件hash不变 | `.local/M1FailureIntegration/disconnect-20260929-032618-b58cd18d677f401a8596dab069017d3e/probe-report.json` |
| 强制结束本轮测试Host | 完整30秒块保留，1个partial；恢复1536960个确认帧，排除24576字节未确认物理尾部，副本可读且原件不变 | `.local/M1FailureIntegration/crash-20260929-032633-89e026b5e4ff43748f44b332691d0efb/probe-report.json` |
| WPF启动/状态smoke | 实际启动/枚举，无采集；回执匹配、暂停/停止回放互斥和恢复路径检查通过 | `.local/diagnostics/m1-20260929/wpf-smoke.json` |
| schema样例/脚本语法 | 规划结构PASS，不替代产品验收 | `.local/diagnostics/m1-20260929/planning-check.txt` |

55是顶层测试组数。内存采集源注入的产物标为isSynthetic；真实WASAPI探针单独存放，不能混为真实mic证据。首轮46通过/2失败的日志保留；修复后才记录上述通过结果。实录检查的是信号包络、尾标记、hash和磁盘时间轴，不是人工听感或每个硬件末帧的独立测量。断连测试通过真实pipe失联触发生产保护，没有杀用户WPF；crash只终止本轮自建测试Host。

此前由代理启动且无CaptureHost的旧开发UI阻止Debug覆盖DLL；正常关闭未退出，核验身份和无采集后仅结束该旧样机，新构建随后通过。没有终止录音writer制造成功。新WPF无采集smoke正常退出，但实际录音后手工关闭仍未全测。

无警告构建沿用已有本地资产，**NuGet漏洞审计仍未完成**。没有新下载、改长期审计设置、提交或推送。

## 可重复命令

```powershell
.\scripts\Dev.ps1 -Action Build
.\scripts\Dev.ps1 -Action Test
.\scripts\Dotnet.ps1 -Arguments @('build','ProjectEcho.slnx','--no-restore','-c','Release')
.\scripts\Test-M1Integration.ps1 -Scenario Recording
.\scripts\Test-M1Integration.ps1 -Scenario Disconnect
.\scripts\Test-M1Integration.ps1 -Scenario Crash
.\scripts\Test-M1Integration.ps1 -Scenario Silence
```

M1探针默认Release，前三项分别约40秒、2秒、32秒后注入故障，Silence实际运行15分钟；均仅自建进程。前三项有低电平测试音，Silence只有数字零；全部实机入口共享项目内互斥锁，不能互相重叠启动。失败返回非零并保存报告。旧 `Test-AudioIntegration.ps1` 仍执行严格M0探针，其设备计数限制仍导致总体FAIL，不修改旧标准。

## 磁盘合同补充

M1使用 `meeting.json/schemaVersion=m1-recording-1` 和本次不可复用来源 `selection.json`，不是对旧原件的原地迁移。capture-index包络仍用连续序号/精确字节hash：

- `validBitsPerSample`保留有效位宽；retainedFrameOffset使一包跨块仍能恢复而不重复计算QPC。
- `gapBefore100ns/gapReason`记录大于一采样帧的正向无包区间及可复算长度，原flags不改；同包后续片为0/null。负重叠超过一采样帧在写PCM前拒绝，此容差不是漂移修正。
- checkpoint包含segmentId、PCM水位hash；`observedSessionUs`来自采集线程经过时间并向下取整，保留暂停/无包时长，不用于代替包QPC对齐。
- 恢复时长以确认包/边界精确100ns及checkpoint为权威。manifest仅容许0–9个100ns的取整余量，并在所有验证成功后采用；坏manifest不能扩大未来书签恢复范围。
- events独立验证序号、hash、重复属性及时间单调，只复制已确认范围内有效前缀。恢复副本明确标为recovered，不拼入未确认尾部。

## 未测试／剩余验收

- **真实mic/双轨：**当前0个mic设备；平台mute差异、拔插、回声、硬件隐私边界未验。合成mic不能替代。
- **长录音：**15分钟真实数字静音已通过；两小时独立双设备同步≤100ms仍未执行，不能用单路静音或合成时钟代替。无callback策略有合成注入证据，未冒充所有驱动的实际无callback行为。
- **系统状态：**真实锁屏、睡眠/恢复、完整WPF关闭/重开尚未全验，没有自动干预系统电源/锁屏。
- **边界覆盖：**合成随机/跨块尾帧和实录尾标记已通过，未把所有驱动失效、真实磁盘满、UI强杀、每种硬件末帧场景全部标PASS。
- **平台范围：**Zoom/Webex真实会议、Chrome实例隔离及端点模式仍按原记录未测/暂缓。

因此M1为 **主要实现已落地／验收进行中**，没有进入M2或发布生产可用承诺。验收矩阵区分完整自动存储用例、局部覆盖和未执行硬件情形。

独立复审修复了：写边界失败跳过另一来源释放、mic启动失败未清理、失败仍确认停采、Control/Stop竞态悬挂、WAV未准备便Start、损坏manifest扩大时间轴、保存成功误提升设备时钟完整性。最终文件hash/文本审查见 `.local/diagnostics/m1-20260929/review.json`；仓库无已跟踪基线，未用空git diff冒充无改动。
