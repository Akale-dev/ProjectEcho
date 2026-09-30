# M0 旧试录审计与落盘修复

日期：2026-09-28。工作目录 `H:\AI\ProjectEcho`。本轮未新增录音、未播放声音、未开启 mic；只离线读取项目已有试录并修复生产代码。M0 音频可行性验收仍未完成。

## 结果

1. 项目已有约 4.821 秒试录包含非零信号，最初三个整秒窗有明显 660Hz 分量。不能由此证明浏览器隔离、来源归属、实际试听或 Zoom/Webex 兼容。
2. 旧产物审计 **FAIL**：480 次设备位置重叠、5 次 checkpoint 数据起点不一致，共 485 项。没有修改旧 WAV、清单或索引，也没有降低审计条件让旧产物通过。
3. 已修复 WAV 头缓冲导致的数据起点错误；补上设备帧位置及 QPC 的独立健康检查、落盘质量标记与会话降级。修复后的真实采集尚未运行。
4. 整个解决方案构建成功，0 错误、6 个已有 NU1900；28 项合成测试通过。修复版 WPF 实际启动和界面状态读取通过，mic 关闭、未选来源、停止/回放按钮禁用。

## 已有文件证据

输入仅为 `.local/M0Recordings/20260928-120808-768006fd73364e7da7b70c989c18a0ae/`，不是本轮新采集。原清单写 ProcessTree、mic=false、Complete=true；这些字段不替代现场来源核对。

| 指标 | 实际结果 |
|---|---|
| 原轨 | 48kHz、双声道 float32；230880 帧、481 包；PCM 1847040 字节 |
| 原轨 data 起点 | 独立 RIFF 解析为 58 字节；五个 checkpoint 均误写 38 |
| 混合预览 | 231421 帧、4.8212708 秒；帧长符合已有 QPC 时间线 |
| 原轨 device position | 481 包均为 0；首包零合法，后续与前包范围重叠 |
| QPC | 包间增量 100000（100ns 单位，即 10ms）；旧数据未报 timestamp-error |
| 信号 | 混合文件峰值约 0.024994、RMS 约 0.013851；前 3 个整秒窗 660Hz 约 -32.05 dBFS |
| mic 落盘 | 未发现约定的 mic 轨、文件或索引；不据此推断运行时从未读取 mic 内存 |

`--tone-report` 先取声道均值，再做三个指定频率的测量，反相声道可能抵消；它不是来源分类器，也不能证明未包含其他声音。索引 SHA-256 校验的是原始 data JSON 字节，不是录制时的 WAV PCM 完整性签名。

证据：

- `.local/diagnostics/recording-audit-20260928-133931-213-d40679e4.json`：首次旧产物审计。
- `.local/diagnostics/tone-report-20260928-133953-934-7ef8feb276324e84854271c7c09a6bed.json`：离线信号测量。
- `.local/diagnostics/recording-audit-20260928-134702-921-434868cf.json`：新字段兼容接入后，旧产物仍为 FAIL。

## 修复说明

### WAV checkpoint 起点

锁定 NAudio.Core 3.1.0 的 `WaveFileWriter` 会缓冲部分 WAV 头。构造后直接读底层 `FileStream.Position` 得到的是尚未刷新完毕的位置，不是最终 PCM 起点。

新增内部 `CaptureWaveFile`，真实 TrackWriter 使用它创建文件；先 `Writer.Flush()` 完成文件头，再记录 `DataOffset`。不硬编码 44/58，不触及源录音或改变采集生命周期。

同一生产类的回归测试覆盖 float32、PCM16、奇数数据长度 PCM24、extensible float、extensible PCM24-in-32；独立解析 RIFF，检查空文件、两次 checkpoint 和收尾后 payload。旧逻辑真实重现 21 PASS / 5 FAIL；修复后 26 PASS / 0 FAIL。证据为 `.local/diagnostics/checkpoint-regression-before.txt`、`checkpoint-regression-after.txt` 及对应 TestResults。

### 时钟质量

本地锁定 NAudio.Wasapi 3.1.0 的 GetBuffer 参数顺序正确；[对应提交源码](https://raw.githubusercontent.com/naudio/NAudio/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/CoreAudioApi/AudioCaptureClient.cs)原样转发 device 与 QPC。当前未确认 Windows 返回全零设备位置的底层原因，不能宣布这种行为是规范保证。[微软 GetBuffer 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)分别定义设备帧位置和 QPC 时间。

新增每路 `CaptureClockHealth`：检测设备位置停滞/回退、QPC 回退、错误标志、负值及早于 epoch，故障锁存至本会话。第一包 device=0 不误报；使用 Int128 比较避免位置加帧数溢出。QPC 有效性不由设备位置失效连带否定，原始值不替换为累计帧数。

`CapturedPacket.DevicePositionValid` 与 `TimestampValid` 分开；索引新增可选 `devicePositionValid`，质量 `qpc_referenced` 表示该包 QPC 可用而设备位置不可依赖。任一路时钟异常使会话 `Complete=false` 并保存警告。旧 JSON 缺少新增字段时保持读取兼容，审计仍独立验证其设备序列；不会把旧全零位置当作通过。schema 和审计同时更新，矛盾的质量标签也会失败。

新增合成用例覆盖上述时钟异常，以及新旧 packet JSON 兼容性；完整测试集最终 **28 PASS / 0 FAIL**。这是 M0 缺陷修复，没有实现 M1 恢复、长录音或漂移校正。

## 实际命令与环境

Windows build **26200.9457 / 25H2 / X64**，项目 SDK **10.0.401**、运行时 **10.0.12**，NAudio.Core/Wasapi **3.1.0**。只复用现有工具，无新下载、无全局设置改动。

```powershell
# 旧产物离线审计；预期 FAIL / exit 1，wrapper 会抛出非零退出异常
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\20260928-120808-768006fd73364e7da7b70c989c18a0ae')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--tone-report','H:\AI\ProjectEcho\.local\M0Recordings\20260928-120808-768006fd73364e7da7b70c989c18a0ae\mixed-preview.wav')
.\scripts\Dev.ps1 -Action Build
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-restore','-c','Debug','--','--output','.local/TestResults/M0/m0-repair-review-20260928')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','src/ProjectEcho.App/ProjectEcho.App.csproj','--no-build','--no-restore','-c','Debug')
```

构建日志 `.local/diagnostics/m0-repair-build-20260928.txt`；最终测试日志 `.local/diagnostics/m0-repair-review-tests-20260928.txt`，结构化结果 `.local/TestResults/M0/m0-repair-review-20260928/results.json`。NU1900 表示漏洞信息服务不可达，不是依赖安全检查已通过。

旧版 WPF 确认空闲后关闭；修复版已重新打开。实际观察默认日期时间标题、未分类、进程树未选来源、mic 关闭；启动后 1 个音频会话/树候选、2 个输出端点、0 个 mic。仅枚举不证明来源/采集通过。本轮没有触发 CaptureHost。

## 浏览器工具状态与剩余阻塞

电脑重启后原生 Computer Use 仍在 Chrome 状态读取时返回 URL 无法可靠识别，并要求结束当轮。WPF 操作正常，两者分开记录。

用户随后明确授权枚举全部标签页；正常审批下 Playwright `browser_tabs(list)` 成功，返回一个 `about:blank`，并非已经连接用户原先的测试标签页。接着导航项目合成页被接口明确拒绝：`Access to "file:" protocol is blocked`。未关闭审批、修改权限、改协议绕行或用另一个输入渠道打开被拒绝页面；未对该浏览器开始播放或采集。列表可用不等于本地文件可用，也不代表原生 Chrome URL 故障已修复。

T03–T08 继续 NOT_RUN。旧产物审计 FAIL 与修复后合成 PASS 都不能替代新的进程树试录、实际回放、同时双浏览器频率对照或平台会议测试；mic 没有设备。后续可在正常获准的页面访问条件下继续，或由用户手动操作合成页、代理只操作 WPF 样机；每场重新核对当前音频来源，不复用旧 PID。
