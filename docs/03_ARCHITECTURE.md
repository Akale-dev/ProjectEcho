# 03｜架构与技术路线

## 技术基线
C# + WPF + .NET 10 LTS；本机开发SDK已锁定10.0.401（global.json），NAudio.Core/Wasapi 3.1.0已恢复，实际采集API仍须M0验证。JSON 清单/追加 journal 为原始事实；FFmpeg/ffprobe 作为本地导出进程候选；whisper.cpp 作为 M3 可选本地转写 worker。[S06–S11]
M0 必须验证精确 NuGet 版本的录音 builder、事件、时间戳访问和生命周期。NAudio 3 与 2 存在破坏性变更；不能把旧示例类名拼到新版包中。[S06,S07]

## 分层
UI（ProjectEcho.App）→ 少量会话/导出应用逻辑 → 核心契约（Core）→ Windows capture / storage / export；ASR 和 analysis 按后续阶段增加。
一个普通用户 CaptureHost 独立进程掌管采集与写盘；WPF 崩溃不应立刻破坏已写音频。导出先用一个可取消/重试的后台任务，必要时启动本地编码器子进程；不预建通用作业调度、插件系统、网络服务或多个空 worker。使用当前用户受限 named pipe 传状态/命令，录音块由 host 直接写盘，避免 UI 转运大音频。

会议列表从 meeting.json 建立内存索引，项目/标题允许录后整理。按2026-09-30用户决定不加入SQLite；M3文字搜索按需读取版本文件，源文件为事实来源。

## IPC 与生命周期
消息含 protocolVersion、sessionId、requestId；超时、重连、重复 stop 要幂等。Named pipe 只允许当前用户；不在公网监听。启动 token 仅本地内存，不写普通日志。
UI 存活 heartbeat 丢失时，host 在设计上限（建议 5 秒）内安全停采并收尾，防止 UI 关闭后无限隐蔽录音。托盘状态正常时才允许后台录音。显示“正在停止并保存”直至 host 返回已落盘结果。
Stop/Pause/MicOff 的生效边界、跨边界包与队列收尾规则见 `04_AUDIO_ENGINE.md`。正常 Stop 不能通过直接取消写盘队列丢掉边界前尾帧；mic 关闭完成前不能显示已关闭。

## 数据流
Remote capture + Optional mic capture
→ timestamped blocks + journal
→ recoverable source chunks
→ offline aligned timeline render
→ mixed archival/playback/export audio
→ optional local ASR OR manual external audio upload
→ imported/original transcript + reviewed revision
→ evidence-linked project notes.

## 错误隔离
音源故障可保留另一轨，但必须显示 degraded 状态/缺口。写盘失败触发明确告警并安全停止；模型失败不影响录音。导出失败保留源与失败任务，不能显示“录音失败/无资料”。退出处理要区分结束录音、结束转写、关闭窗口。
录音期间（含暂停）禁用本程序全部回放；试录必须停止并收尾后才回听。旧会议导出不得阻塞新录音，资源不足时优先采集/写盘并延后导出。

## M0 时间戳闸门
底层 IAudioCaptureClient.GetBuffer 可提供 device position 和 QPC position，后者为 100ns 单位，不等同原始 Stopwatch ticks。[S13]
如果封装只暴露 bytes/回调到达时间，M0 要记录限制并实现窄范围原生互操作或经过审查的小补丁，使后续双轨时钟校准有依据。不必预先引入整个 C++ 工具链；先评估 C# COM/interop 的最小实现。未通过长录音漂移验收，不准宣称可靠同步。
从第一天保存实际时间戳、帧数、flags、格式/segment、共同时间基准和恢复有效边界；磁盘契约见 `11_STORAGE_SCHEMA.md`。M1 要证明退出后仅凭落盘文件能还原时间轴，并保留两小时同步验证（T24）。先实现最小可验证映射，再按实测决定漂移纠正复杂度，不能用没有测量证明无需纠正。

## M2 分发边界
M2 即发布完整 win-x64 self-contained 文件夹，包含 App、CaptureHost、运行时和所选编码器；启动/录音/回放/压缩导出在无预装 SDK/.NET 的 Windows 验证。依赖来源、哈希和许可随包核对。M3–M5 均不构成前置条件，M6 再扩大平台/发行回归。

## 建议目录
`src/ProjectEcho.App`、`Core`、`Capture.Windows`、`CaptureHost`、`Export`；M3 再加 `Transcription`；M4 再加 `Analysis`。`tests/ProjectEcho.Tests` 放纯算法/存储/契约测试；手工矩阵独立保留。
