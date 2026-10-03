# 58｜M3D Windows 10 进程兼容与端点录音修复

日期：2026-10-02。范围：既有录音可靠性修复，不进入 M4。

## 用户反馈与已确认结论

用户最初称目标电脑为 Win11，随后通过 winver 更正为 **Windows 10 Pro 21H1，19043.2364**，没有启用程序兼容模式。完整 M3D ZIP 可以打开；进程模式被预检拒绝，端点模式有一次缺少范围确认，另有三次 remote 录音因 QPC 重叠校验失败而停止。mic 关闭。日志中设备为 Jabra LINK 860；没有取得目标机原包、被拒绝包、音频或完整索引。用户另补充仅留下几 KB 的 `.wav.partial`，没有可用音频；这与早期停止一致，但未检查文件，不能断言其中有多少有效 PCM。

1. **进程模式：旧版保守门槛拒绝，并非 OS 版本读错。** 19043 < 原项目采用的 20348 门槛。用户随后明确要求支持这版 Windows；进一步核对锁定的 NAudio 3.1.0 包内 XML 和 OBS 官方指南后，为 19041–20347 加入显式标注的进程兼容尝试。低于 19041 仍拒绝；实际 API 激活失败时不扩大录音范围。
2. **端点预检：独立的范围确认。** “必须明确确认”表示所选端点全部应用声音尚未确认。切换来源/端点或重新开始一场后应重新核对范围；mic 关闭的说明是正常隐私提示。
3. **采集中失败：首因是原包时钟校验。** “写盘队列不可用”“边界未能写入”“停止事件写入失败”是 writer 拒绝音频包后的后续错误；现有证据不指向磁盘容量不足。不能据此断言硬件驱动故障或确定抖动幅度。

## 代码缺口与修复边界

### Windows 10 进程兼容尝试

本轮用户请求覆盖之前项目按微软 sample 固定 20348 的保守约定。微软 [AUDIOCLIENT_ACTIVATION_PARAMS](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params) 和 sample 仍列 20348；[OBS 官方应用音频捕获指南](https://obsproject.com/kb/application-audio-capture-guide)明确 Windows 10 2004+，锁定 NAudio 3.1.0 包内 `AudioClient.ActivateProcessLoopbackAsync` XML 也列 19041。依据这些第一方实现证据允许 **尝试**，不把声明冲突解释为目标机实测成功。

三个入口（预检、M0、M1）采用同一能力判定。19041–20347 显示真实系统版本及“兼容尝试、尚待实测”；只有用户主动开始时调用原来的进程树回环 API，无注入、无驱动、无组织策略更改。激活失败显示技术错误且不自动降级到端点；进程树/PID复用/独立mic规则不变。

### 有效设备帧支持的小幅时钟抖动

端点回环直接使用所选设备的 MixFormat；当前错误路径不是本程序强制将 16k/44.1k 改成 48k 的转换链。旧 `MicrophonePacketClock` 对 `sourceId != mic` 一律返回零，使具有连续有效设备帧的 remote 端点也无法声明已经用于 mic 的短期 QPC 抖动。

新增通用设备包时钟规则，由写入、离线恢复、混音共同验证。仅适用于 remote/mic 的同一段：原始 QPC 有效且递增、两包设备帧有效且严格连续、前包已保留到包尾、当前没有 discontinuity/timestamp-error。允许的偏差必须大于一输入帧，同时不超过 2ms 和前包时长的 25%。这是有界相邻包映射，不是长时漂移估算或双设备同步证明。

保持原始 PCM、QPC、设备帧及 Stop/Pause/MicOff 截止，不伪造设备计数，不启用 mic，不扩大来源。没有有效设备计数的进程回环继续使用原 QPC-only 规则。旧麦克风 API 保持 mic 限制作为兼容包装。拒绝消息补充数值时钟诊断，不包含音频、设备标识、进程名或会议内容。

混音对具有有效设备计数的 remote 也保留真实微小缺口；派生版本提升为 `m2-qpc-mono-4-device-clock-preserving`。旧派生文件不删除、不覆盖，也不被静默当作新版缓存使用；可从原件重新生成。

微软 API 合同：[GetBuffer 的帧位置与 100ns QPC 单位](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)。进程范围门槛依据：[Application Loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)。这些文档不能替代目标机实际激活、采集和回放验证。

## 验证状态

当前开发环境：Windows 11 25H2 **26200.9457/x64**，SDK **10.0.401**，运行时 **10.0.12**，NAudio **3.1.0**。数值环境记录：`.local/M3D-EndpointFix/environment.json`。

最终 Release 构建 **0 警告 / 0 错误**，完整默认回归 **201 PASS / 0 FAIL**。包含9组端点时钟/跨块/尾帧/恢复/篡改测试、3组Windows版本边界与模拟激活成功/错误码/无fallback测试；既有mic隐私边界、写盘预算、真实Windows压缩编解码合成文件回归也通过。无任何硬件录音。证据：`.local/M3D-EndpointFix/build-final-02.log`、`.local/TestResults/M3D-EndpointFix/default-final/results.json`。

首轮默认回归 **197 PASS / 1 FAIL** 保留：旧测试把有连续设备帧的小重叠一律当非法，与新的已声明规则冲突，修订为缺少设备帧证据的重叠仍须在PCM写入前拒绝，并保持160帧原件/恢复断言。Windows兼容改动的中间构建曾有2个CA1416警告，已用真实条件和平台guard解决，不禁用警告；中间日志保留。独立代理分别审查时钟和进程兼容修改；不把代码审查当目标机实测。

本次新交付为 `artifacts/ProjectEcho-win-x64-20261002-M3D-Win10Fix.zip`，仍附原CPU/Vulkan引擎与base。包的实际ZIP逐文件hash、解压目录WPF启动/自带Host运行时证据独立写到 `.local/M3D-EndpointFix/zip-verification.json`；只在文件结果为PASS时算包验证通过，不等同Win10实机采集通过。旧ZIP、Data和失败原件不覆盖。

交付核验实际 **PASS**：ZIP **260,720,845字节**，SHA-256 `3702c260029c2530de7c1d1ea745b301323c1db2c5260ab883a250f7b50104af`；655个清单文件及manifest共656个ZIP文件逐一校验，解压到`artifacts/Echo58`后运行实际WPF与Host空闲IPC（无录音）。Host从解压包加载coreclr/hostfxr，无效外部DOTNET_ROOT下通过。ASR两引擎/base/默认相对配置与旧完整包hash相同，本轮未重跑识别。证据另见`.local/diagnostics/portable-20261002-114437/portable-smoke.json`。ZIP内修复说明为打包时版本，其验证段指向上述独立实际结果；本段是打包后的仓库交付记录。

**未测：**目标 Win10 19043 上进程回环实际激活/隔离/录音、Jabra LINK 860 修复后真实端点录音；真实设备持续录音/声学同步；旧失败目录的恢复；干净 Windows 完整录音/回放/压缩导出。M0 严格设备时钟审计没有因 M1 修复升级为 PASS；历史 M1/M2 失败和未验项目不升级为 PASS。

## 新电脑复测

将新 ZIP 解压到新的短目录（如 `C:\AI\EchoFix`），打开其中 `ProjectEcho.exe`，保留旧包和旧 Data。

先选目标“进程树”，mic 保持关闭，由用户主动开始 **普通录音 30–60 秒**，检查保存、回听及完整混音导出。19043 的预检会显示兼容尝试提示，不能凭能点开始就认定隔离或完整录音通过；Chrome 仍是整棵进程树，不是单标签页。

若进程 API 不可用，保留完整错误文字。用户可自行选择“端点兼容”，明确选会议实际播放的输出设备并勾选全部应用声音范围确认后，另做普通录音短测。该步骤会采集所选端点上所有应用的声音，应由用户自行安排测试声音与范围。切换来源不会自动开始采集。

若仍失败，请仅提供新版界面中的完整错误文字（尤其 `rate`、QPC 间隔、设备帧和 flags 数值），不需音频、会议内容、整个 Data 或日志上传。失败仍保留为失败，不重复正式长会试录来代替定位。

原失败目录请保留；“原件已保留”不等于完整会议已保存，也不代表从未落盘的音频可以恢复。本轮没有读取或修改目标机上的任何录音。
