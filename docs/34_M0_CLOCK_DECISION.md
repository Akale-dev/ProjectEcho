# M0 设备计数能力与 QPC 时间证据

2026-09-29。此文把“原件写盘”“包时间可用”“设备帧计数可用”“双轨同步已验收”分开记录。**没有修复或补造底层 device position，也没有把旧 FAIL 改成 PASS。**

## 判定

本轮已完成构建、55组自动测试及真实文件分类，采纳下述限用可行性结论并进入M1。实现与后续真实录制证据见 [35_M1_IMPLEMENTATION_REPORT.md](35_M1_IMPLEMENTATION_REPORT.md)。设备计数并未变为可用，旧审计不变。

建议在本轮实现构建及测试确认后，把 M0 可行性记为 **FEASIBLE_WITH_LIMITATIONS / QPC_ONLY**，随后进入 M1 实施。理由是 M0 要求实际 Windows 捕获、回放、锁定 API 和时间戳/帧数验证；其退出合同没有把硬件计数器可用或两小时双轨同步提前作为 M0 必须通过项。已经保存的 API QPC 是包首帧时间，不是回调到达时间；实采帧数与逐包 QPC 连续性可以被离线验证。M1 的可靠性门槛仍完整保留。

这个判断只解决“此 API 在此电脑是否足以继续可靠录音器研发”，不等于用户可直接把当前样机当可靠会议录音器。进程捕获底层设备位置恒零的原因仍未知；正式录音完整性仍按旧标准降级。若后续出现 QPC 错误、乱序、断续或不能满足同步目标，应拒绝相关完整性结论或明确限用，不能用本次分类放行。

## 官方依据与推断边界

微软说明 `IAudioCaptureClient::GetBuffer` 独立返回实际可读帧数，以及首帧的 device position 和 QPC。QPC 已转换为 100ns 单位，不能再次当 raw `QueryPerformanceCounter` ticks 转换；`TIMESTAMP_ERROR` 与 `DATA_DISCONTINUITY` 必须保留处理。[GetBuffer 官方契约](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)

微软的 application-loopback 示例限定目标进程及其子进程，捕获不绑定某个物理输出端点。该说明**没有**保证所有实现的 device position 恒零，也没有承诺 QPC-only 与另一独立 mic 时钟必然长时间同步。[Application loopback 官方示例](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)

因此：本机“ProcessTree + 全零 device + 连续有效 API QPC”是**本地观测到的能力限制**。允许单独识别这一模式是工程分类决策，不是从微软文档推出“恒零正常”“设备计数已经修复”或“任意来源都可忽略 device”。

## 已有真实证据与本轮离线核对

真实 Windows 探针在上一轮已经执行，本轮此项调查仅读取原件，没有重新录音、启用 mic 或改写旧录音。

| 内容 | 证据/结果 |
|---|---|
| 系统 | Windows 26200.9457 / 25H2 / X64 |
| SDK/依赖 | 10.0.401 / NAudio 3.1.0；本轮没有新增依赖 |
| 来源 | 测试独立子进程，ProcessTree，mic=false，无端点回退 |
| 已有真实探针 | `.local/AudioIntegration/20260929-024316-5cbac342a5a84e629bd65820cea90dea/probe-report.json` |
| 原包 | 649 包，每包 480 原始帧，48kHz；311520 帧 / 6.49 秒 |
| 本轮读取 capture-result 后的数字核对 | 相邻 QPC 差 × 48000 / 10000000 − 前包原始帧数：最小/最大都为 0；device 全零 |
| 既有信号检查 | 660Hz 主信号及 880Hz 尾标记通过；详见 33；不能据此宣称逐帧截止、浏览器隔离或双轨同步 |
| 原件状态 | 原清单 `Complete=false`；旧报告 FAIL，1297 项错误，保留不变 |

此前报告的原件哈希与 journal/字节检查仍保留于原报告。新的时钟分类本身不读取 WAV 字节、校验 journal 或证明最终刷新成功。

## 独立能力分类器

新增 `src/ProjectEcho.Core/ClockCapability.cs`，入口：

```csharp
ClockCapabilityEvaluator.Evaluate(ClockSourceKind.ProcessTree, track, epochQpc100ns)
```

调用者须从已保存的来源选择确定 `ClockSourceKind`，不能根据文件名猜测。每次只评估同一连续 segment 内尚未跨 chunk 重复拆分的原始 API 包；暂停/重置/格式变化应分段后评估。

| 结果 | 条件/含义 |
|---|---|
| NoPackets | 无包且无帧；不等于采到静音 |
| InsufficientEvidence | 只有一个有效原包，无法证明下一包时钟推进 |
| DeviceReferenced | 原始 device 连续、API QPC 严格递增且与帧时长一致；仅证明本段内部记录一致 |
| QpcOnlyLimited | 仅明确 ProcessTree；至少两个包；device 全零；帧区间和 QPC 严格有效，显式保留 device 不可用提示 |
| Invalid | 时间标志、QPC 重复/倒退/越界、明显 gap/overlap、device 部分回退/停滞、帧数/索引不一致等 |

比较使用 `Int128` 有理数运算，防止 uint64 大 QPC 的浮点精度损失及 device end 溢出。只容许至多 **1 个 100ns 单位**的整数时间戳量化差；这是精度取整界限，不是容忍毫秒级 gap/overlap 的窗口，也不是漂移修正。尾部按真实截止保留的前缀允许存在，但中途截包后又继续同段则报错。

报告的 `OriginalStorageValidated` 与 `CrossSourceSynchronizationValidated` 固定为 false。旧 `CaptureClockHealth`、旧离线审计、旧 `CaptureResult.Complete` 均不变。新分类器只增加可解释的能力描述，不覆盖旧失败事实。

## 测试和后续责任

新纯元数据夹具在 `tests/ProjectEcho.Tests/ClockCapabilityTests.cs`，入口 `Run()`；覆盖正常推进、明确 QPC-only、拒绝 mic/endpoint/未知来源全零、无包与静音区分、单包、重复/倒退 QPC、flags、gap/overlap、device 部分回退、非零停滞、索引/帧数错误、尾部截断、大 uint64 和溢出。编译、自动测试以及新实采的执行结果应以主任务本轮报告为准；此文不把未执行的检查预标为通过。

M1 须独立增加存储状态（写盘处理中、原件已完成收尾、可恢复/不完整等）与时钟质量，避免把“缺 device”误写成“没有文件”，也不能反过来因 WAV 存在就表示完整。保存成功必须由 writer 最终刷新、journal 水位、文件收尾与 manifest 成功证明；未经这些证据，时钟分类不得提供替代结论。

M1 的随机 Stop/末帧、mic-off 边界、30 秒切块、离线重建、异常恢复、两小时同步、睡眠/锁屏仍须各自验证。尤其目前未启用真实 mic，不存在双轨实测证据；QPC-only 也不证明 `amix` 或重采样能解决独立时钟漂移。
