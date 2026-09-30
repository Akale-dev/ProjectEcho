# M0 复审与修复（2026-09-29）

结论：**本轮修复了可确定的 M0 缺陷，自动验证通过；M0 仍为 IN_PROGRESS / CLOCK_QUALITY_LIMITED，尚未进入 M1。** 用户本轮授权在 M0 无问题后继续 M1；设备位置恒零尚未由修复版实采闭环，不能用 M1 开发替代这一闸门。M0 不要求所有真实会议/浏览器隔离先通过；那些场景继续诚实保留 NOT_RUN，浏览器后续测试仍按既有要求暂缓。

## 已实现

1. **重复 QPC 不再误判正常。** 正帧包的 QPC 必须严格递增；重复/回退使时钟失败锁存。离线审计也独立拒绝重复值，即使清单声称 Complete=true。设备位置与 QPC 质量继续分开，设备位置恒零仍失败，原值不改成累计帧。[T22]
2. **按 WASAPI 合同初始化。** shared + event-driven 的 buffer duration 与 periodicity 都改为 0，由引擎选择缓冲区；原代码请求 100ms。[微软 Initialize 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)明确规定这一组合。构建验证不等于新参数的实际采集验证，更不证明恒零已解决。[T02]
3. **写盘就绪后才采集。** writer 成功建立全部 WAV、完成头部/初始 journal 刷新后，采集线程才可 Start；写文件失败或就绪期间取消不会启动音源。[T21]
4. **先停全部来源，再处理尾包和释放。** mic 优先收到 Stop，每路仍分别记录实际 cutoff。某一路尾包处理不再推迟另一轨的 Stop；错误不跳过其他来源的停止/释放。已有截止前队列继续排空，Saved 条件未放宽。这是 M0 Stop 修复，不代表已实现 M1 Pause/MicOff。[T51,T52]
5. **Host 输出阻塞不能拖住失联保护。** 状态写入有 2 秒超时；独立 5 秒父进程心跳 watchdog 可请求停采并断开 pipe，不等待输出。启动与 watchdog 的短锁避免丢 Stop 信号；退出先断 pipe 再 dispose writer，防止同步 flush 再次挂住。原轨保存完成后停用心跳 watchdog，防止慢速会后混音误报；最终消息仍有写入截止时间。[T19]
6. **纠正文档的否定词缺失。** `10_PRIVACY_SECURITY.md` 中原有“可以把勾选当法律证明/可以注入”等文字与 AGENTS 硬约束相反，已明确改为禁止；未实施那些行为。

没有新增包版本、运行依赖、驱动或工具链，没有改全局配置，没有提交或推送。测试工程增加对现有 CaptureHost 的项目引用，锁文件只增加该项目依赖关系。

## 时钟调查与未解决问题

锁定 NAudio 3.1.0 的真实 GetBuffer 参数顺序与调用一致；AudioClient.Dispose 会释放其 capture service，未发现应替换封装或重复释放的证据。[对应 NAudio 源码](https://raw.githubusercontent.com/naudio/NAudio/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/CoreAudioApi/AudioCaptureClient.cs)

本轮离线检查七份既有试录清单：每份只有一个 device position 值，QPC 每包不同且严格递增。它们均为先前 ProcessTree / mic-off 产物；没有重新录制，也没有修改原清单或录音。第一份旧清单自身的 Complete=true 是历史值，不代表本轮认可；旧审计 FAIL 仍保留。聚合证据：`.local/diagnostics/m0-review-20260929/existing-clock-summary.json`。

[微软样本仓库 issue #319](https://github.com/microsoft/Windows-classic-samples/issues/319)有相似直接复现，但这是问题报告，不是微软对所有系统的行为保证。可据此提出“可能为 process-loopback 的实现限制”这一假设，不能据此把恒零标为正常或关闭验收。当前仍只允许带警告的 QPC 试听，不宣称双轨时钟、漂移或末帧通过。

## 已测试：真实命令与证据

本轮环境：Windows **26200.9457 / 25H2 / X64**；PowerShell **7.6.5**；项目 SDK **10.0.401**；NAudio.Core/Wasapi **3.1.0**。本轮未选择采集模式、未发送录音 Start、未启用 mic。精确环境位于 `.local/diagnostics/m0-review-20260929/environment.json`。

| 项目 | 结果 | 本地证据（均在 `.local`） |
|---|---|---|
| 修复前基线产品 Build | 0 错误、6 NU1900 | `diagnostics/m0-review-20260929/baseline-build.txt` |
| 仅用现有项目缓存 restore + locked restore | PASS；无下载、新包或版本变化 | `diagnostics/m0-review-20260929/offline-restore.txt`、`offline-locked-restore.txt` |
| 最终产品 Build | **0 错误、0 警告** | `diagnostics/m0-review-20260929/final-build.txt` |
| 自动测试 | **35 PASS / 0 FAIL**，均无音频采集 | `diagnostics/m0-review-20260929/final-tests.txt`；`TestResults/M0/m0-review-20260929-final2/results.json` |
| 实际 WPF 启动与设备枚举 | PASS；mic 关、来源未选、未启动 CaptureHost；1 输出端点、0 mic、0 音频树候选 | `diagnostics/m0-review-20260929/wpf-smoke.json` |
| 独立 Host 无采集 IPC | idle-stop、idle-heartbeat-timeout **2 PASS**，从未发送 Start | `diagnostics/m0-review-20260929/host-smoke.json` |
| 人工 Windows 音频/试听/新初始化参数实采 | **NOT_RUN** | 无本轮新录音证据 |

最终构建没有 NU1900 是因为本轮从现有本地缓存恢复时，**仅对该命令设置 `NuGetAudit=false`**；没有修改项目长期设置。漏洞信息审计仍未完成，不能据此宣称依赖安全通过。

新增回归不是硬件替身验收：QPC 测试生成有效对照与仅重复 QPC 的磁盘 WAV/索引/清单，校验审计拒绝且输入哈希不变；writer 测试用真实已存在文件触发 CreateNew 失败；停采顺序测试注入尾包/释放/Stop 异常；Host 测试使用真实本地 NamedPipe 的 1MiB 合成状态文本制造回压，验证独立 watchdog 和写超时，但没有创建 CaptureSession。

```powershell
.\scripts\Dev.ps1 -Action Build
.\scripts\Dotnet.ps1 -Arguments @('restore','ProjectEcho.slnx','-r','win-x64','--source','H:\AI\ProjectEcho\.local\nuget-packages','-p:NuGetAudit=false')
.\scripts\Dotnet.ps1 -Arguments @('restore','ProjectEcho.slnx','-r','win-x64','--locked-mode','--source','H:\AI\ProjectEcho\.local\nuget-packages','-p:NuGetAudit=false')
.\scripts\Dev.ps1 -Action Build
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--output','.local/TestResults/M0/m0-review-20260929-final2')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','src/ProjectEcho.App/ProjectEcho.App.csproj','--no-build','--no-restore','-c','Debug','--','--smoke-test')
.\scripts\Test-M0Host.ps1
```

现有脚本生成固定名 smoke JSON；本轮结果已复制到本报告的日期目录，旧固定名文件已从本轮事前副本恢复，避免旧报告的链接被新结果覆盖。

## 未测试、阻塞与后续

- **当前阻塞：**新初始化配置的短试录与逐包 device/QPC 核验尚未执行；本轮枚举没有可确认的音频树候选。既有代操作授权继续有效，这不是要求用户重复授权。不能宣布设备时钟已修复；M0 尚未完成。
- **未测试：**新停止顺序的真实音频末帧/硬件故障、实际采集中 UI 卡死、mic、端点兼容、真实会议及浏览器隔离；后续浏览器测试仍暂缓。WPF smoke 是自动 Windows 检查，不冒充人工音频验收。
- **M1 未开始：**Pause/MicOff、30 秒块、离线恢复、两小时同步、锁屏/睡眠等不由本轮测试替代。M2 压缩导出/便携发行也未执行。
- **下一步最小任务：**准备好可确认的受控测试音来源后，延续既有代操作授权，用修复版做一次 mic-off 短试录；采集前重新枚举当前来源，核验新的初始化、QPC 与设备帧序列，保留旧原件。若恒零仍出现，继续调查或明确评审 QPC-only 的能力与验收合同，不伪造设备计数/降低旧审计标准。M0 闸门解除后，按本轮授权进入 M1，不再另行索取开发许可。

## 复审与修改范围

两条独立审查分别检查时钟 API/既有证据和 Host/采集生命周期；集成后复核 writer 就绪、停止顺序、尾队列排空和 Host 超时竞争。仓库仍没有 Git 已跟踪基线，普通 git diff 为空不表示没有改动；本轮使用事前 SHA-256 清单和 CaptureSession 的事前副本对照，并记录新增文件。

生产修改：`CaptureSession.cs`、新增 `CaptureLifecycle.cs`、`CaptureClockHealth.cs`、CaptureHost `Program.cs` 与新增 `HostTransportSafety.cs`。测试修改：时钟/审计测试、新增 lifecycle/Host 回压/磁盘审计回归、测试注册/项目引用/锁文件。状态、验收矩阵及相关文档同步更新。最终哈希与文本审查证据：`.local/diagnostics/m0-review-20260929/review.json`。
