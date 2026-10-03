# M0｜NAudio 3.1.0 实际 API、实现与验证边界

2026-09-29 更新：下面保留初次 API 报告。历史有限实录见 29–31；本轮修复 shared/event 初始化为 0/0、QPC 严格递增及生命周期/IPC 问题，证据见 [32_M0_REVIEW_REPAIR.md](32_M0_REVIEW_REPAIR.md)。设备位置恒零未闭环，修复版实采 NOT_RUN。

核验日期：2026-09-28。工作目录 `H:\AI\ProjectEcho`。本报告只说明 M0 样机；真实进程回环激活、麦克风采集、平台兼容和停止末帧实测均 **NOT_RUN**。没有代理启动录音。

## 实际依赖与可构建证据

- 项目使用 `net10.0-windows`，项目本地 SDK `10.0.401`，与 `global.json` 一致。
- Windows 实机环境记录为 build **26200.9457 / X64**；不从注册表历史显示名称推断系统能力。普通用户 token，无管理员提权。
- `NAudio.Core`、`NAudio.Wasapi` 均使用精确 NuGet 范围 **`[3.1.0]`**；恢复后的 `packages.lock.json` 锁定包内容 hash。没有添加音频驱动、C++ 编译器或新工具链。
- 包 nuspec 标记官方仓库 commit：`0aaef29d04bec9567bdf2f669036fabecc33a2e2`。
- 本地 `NAudio.Core.dll` SHA-256：`2414B224766B74619B9EF313EA46D6F66A23CDBD34BC18C66370120C41EBC1D3`。
- 本地 `NAudio.Wasapi.dll` SHA-256：`B9402D5D2F72C23B0D62A8147E0D61E568B6C22388A31FFC1AEC256FF9DD45AB`。

本轮直接检查项目缓存的 XML、nuspec，并通过 PowerShell `Assembly.LoadFrom` 反射真实 DLL；反射只读类型定义，不激活音频。真实解决方案初次 Windows 构建已通过，0 编译错误；6 个 NU1900 是 NuGet 漏洞数据源不可达，不能解释为“漏洞检查通过”。后续复核结果以 `STATUS.md` 和最新本地日志为准。

统一执行命令与证据：

```powershell
.\scripts\Dev.ps1 -Action Restore
.\scripts\Dev.ps1 -Action Build
.\scripts\Dev.ps1 -Action Test
```

日志位于 `.local/diagnostics/m0-restore.txt`、`m0-build.txt`、`m0-test.txt`。合成算法测试不激活音频，也不能替代真实 Stop、麦克风隐私、两小时同步或平台测试。

## 真实 API 合同

下表成员已由本地 **3.1.0 程序集/XML**核对，并由正式后端引用编译。

| 用途 | 实际 API |
|---|---|
| 设备枚举 | `MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow, DeviceState)` |
| 真实输出会话 | `MMDevice.AudioSessionManager.RefreshSessions()`、`Sessions`、`AudioSessionControl.GetProcessID` / `State` |
| 固定端点麦克风/回环 | `MMDevice.CreateAudioClient()`，`AudioClient.MixFormat` |
| 进程树回环 | `AudioClient.ActivateProcessLoopbackAsync(uint, ProcessLoopbackMode.IncludeTargetProcessTree)` |
| 流初始化 | `AudioClient.Initialize(shareMode, flags, bufferDuration, periodicity, WaveFormat, Guid)`、`SetEventHandle(IntPtr)` |
| 启停 | `AudioClient.Start()` / `Stop()` / `Dispose()` |
| 原始包 | `AudioCaptureClient.GetNextPacketSize()`，`GetBuffer(out int, out AudioClientBufferFlags, out long, out long)`，`ReleaseBuffer(int)` |
| WAV 原轨 | `WaveFileWriter(Stream, WaveFormat)`、`Write`、`Flush`，收尾后 `WaveFileReader` 检查 PCM 字节长度 |

另外核实了新版高层 `WasapiRecorderBuilder.WithProcessLoopback(...).BuildAsync()`、`WithDevice(...)` 和 `WasapiRecorder.DataAvailable`；回调本身已提供 `devicePosition` 与 `qpcPosition`。这不是“NAudio 无时间戳”的情况，也不需要另写音频 COM 接口。3.1.0 的 `WaveFormat` 不再支持旧式 `Marshal.SizeOf`/`PtrToStructure` 用法；本项目不这样封送格式。

选择低层 API 的原因是停止契约：[同 commit 官方实现](https://raw.githubusercontent.com/naudio/NAudio/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/WasapiRecorder.cs) 的高层停止循环在停止请求后进入 Stop/Reset，不能直接作为本项目“先保留剩余有效尾包”证明。因此样机用单独 MTA 线程管理低层 client，不在读取有效尾包之前 Reset。

进程虚拟端点不支持 `MixFormat`；样机明确请求 **48 kHz / Float32 / 双声道**，使用 `Loopback | EventCallback`，不加入该虚拟端点不支持的自动格式转换标志。实体端点采用实际 `MixFormat`，保留格式与 channel mask；M0 拒绝超过双声道的设备，避免把尚未实现的通道映射称为真实混音。

## 时间、flags 与最低 build

2026-10-02 用户追加要求支持 Win10 19043：下文 20348 为旧保守门槛；现增加 19041–20347 的显式兼容尝试，以锁定 NAudio 3.1.0 与 OBS 官方代码为依据，保留实际API失败和不扩大范围规则。目标电脑尚未实测；见[58](58_M3D_ENDPOINT_RECORDING_FIX.md)。

`GetBuffer` 给出的 device position 是包首帧位置；QPC 已转换为 **100 ns**，不是原始 `Stopwatch` ticks。代码用 `AudioClock.CounterTo100ns` 把会话 epoch 转为相同尺度，并逐包保存原始 device/QPC、原始帧数与 flags；device/QPC 在 JSONL 中为十进制字符串。[Microsoft GetBuffer 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)

- `SILENT`：不读取原生指针内容，生成相同帧数的零 PCM，保留标志；无包保持无包。
- `TIMESTAMP_ERROR`：保留原始时间字段并标为 invalid；停止边界中无法判定的包舍弃并报告降级，不能导出伪精确混音。
- `DATA_DISCONTINUITY`：完整写入索引。首包单独警告；没有前包可核验，不能仅凭标志断言丢帧。后续包出现则标记不完整。首包策略依据该标志可指示状态转换这一官方定义，属于样机报告策略，不是“无缺口已验证”。[Microsoft buffer flags 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags)

包内 XML 把 process loopback 标注为 build 19041+，而[微软 Application Loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)写 20348+。本项目采用较保守的 **20348** 预检门槛；高于门槛只表示可以尝试，真实激活必须在用户主动试录时验证。失败不会自动切成全端点录音。[S01,S02,S13]

## 本轮已实现

`SourceDiscovery`：枚举实际 render sessions → 音频 PID → 可读取身份/祖先候选；身份含 PID、UTC start ticks、可执行文件路径。Toolhelp32 仅用于读取父进程树，这是小范围 Windows 进程枚举互操作，不是新的音频后端。拒绝系统、shell 和本程序根；启动前及采集中重新核验身份与祖先关系，身份变化明确停止，不持久复用旧 PID。[T01,T03,T17]

预检只枚举和读取选中设备格式，不 `Initialize`/`Start`。麦克风默认关闭时不创建 mic client；主动启用后仅使用明确选择的固定 endpoint，消失时不替换。端点兼容模式必须显式确认“该输出端点所有应用声音”。[T11,T18]

`CaptureSession`：最多 10 秒主动样机录音；两轨独立电平；有界 writer 队列（32 MiB PCM 预算、256 项），队列或磁盘错误可见并停止。采集线程只复制/归还音频包并投递，不压缩、不 ASR。每路停止返回后记录自身 cutoff，再读取其剩余包；仅复制边界前完整帧，不把丢弃数据送入 writer。异常路径逐路停止和释放，writer 排空合法队列后才返回结果。重复 Stop 返回同一 Completion。[T15,T51,T52]

磁盘输出为独立 `sources/remote.wav` 与可选 `sources/mic.wav`、`journal/capture-index.jsonl` 和 `capture-result.json`。JSONL 含 session/segment/packet/boundary/checkpoint 及精确 data 字节 SHA-256；WAV 先写 `.partial`，正常收尾校验后 rename。checkpoint 含实际 PCM 水位与 WAV data offset，原轨与索引分开刷新。这个短样机结果清单明确标记 `m0-spike-1`，不冒充完整 meeting manifest 或 M1 恢复器。[T22,T53]

结果清单只在本地保存本次 scope、根/音频进程身份、端点及 mic 选择与范围确认，不包含命令行、会议 URL 或窗口标题；`reusableSelection=false` 明示这只是会后核对证据，下次必须刷新选择。

## 未执行与保留限制

| 项目 | 本轮状态 |
|---|---|
| Windows 真实程序集 API 存在性与后端编译 | 已验证；见构建日志 |
| QPC 单位换算、完整帧边界数学 | 合成测试；非硬件边界验收 |
| 真实进程回环初始化/采集/停止尾包 | **NOT_RUN** |
| 固定 mic 权限/采集、实际 mic-off/pause 边界 | **NOT_RUN**；本轮未提供暂停/中途 mic 切换 |
| Zoom / Webex 桌面，Chrome / Edge 网页范围 | **NOT_RUN** |
| 专用 Chrome 660 Hz / 普通 Chrome 440 Hz 隔离 | **NOT_RUN** |
| 两小时漂移、设备失联/切换、锁屏/睡眠 | **NOT_RUN** |
| 30 秒切块、崩溃恢复、仅凭磁盘重建验收 | 留给 M1，未实现/未验收 |

身份轮询不能替代 PID 复用、浏览器子进程重建和 reparenting 的真实压力测试。单个短录文件和 flush 也不构成断电恢复保证。当前索引已保留原始时钟证据，仍须 M1 针对跨边界、积压、半行/坏 hash、失联及恢复做完整验收。M0 可保存原轨不代表所有平台都已支持，也不代表可靠同步已通过。
