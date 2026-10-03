# 61｜M3F：字段提示、回听状态与单实例

2026-10-02。用户要求 M3F 六项修正，并追加决定：默认录后只生成完整混音 WAV；M4A 按导出选择生成。会议通常不超过 3 小时，本轮不做超大文件分段识别或专门兼容适配，保留明确提示和既有长度/格式限制。

## 已实现的行为

- 删除新录音页最上方的“确认会议声音……”说明。保留实际来源范围、端点全部声音以及 mic 状态提示。
- 点击开始录音/试录/预检，未选择进程声音来源、输出端点或已启用但未选麦克风时，对对应字段画红框并显示文字。静态预检的失效来源/磁盘等错误映射至对应字段；纠正后清除提示。
- 本地识别同样指出缺少会议、完整混音、CLI、模型、许可确认及非法线程/GPU/硬件等问题；只做验证时不启动模型。运行时失败保留任务 Log，不将任意引擎错误误标成某个输入字段错误。
- Logo 从仓库已有 SVG 重建为 WPF 矢量图形，与 ProjectEcho 和 `Ver. M3F` 同行；避免小尺寸位图缩放的锯齿。
- 未选来源不再导致全局回听不可用。先完成表单与静态预检，通过后才停止回听、进入录音状态及建立 CaptureHost。缺字段或预检失败保持历史会议与标题草稿，不创建录音目录。已确认捕获进程退出的错误使用独立 `StoppedWithError` 状态，历史回听可以继续；Recording/Paused/Stopping/未确认停止的 Faulted 仍禁播。
- 同用户、同 Windows 交互会话的普通 M3F 启动共用单实例锁，与便携目录无关。再次打开通过当前用户本地管道请求恢复最小化窗口/激活，然后退出，不启动第二个 MainWindow。启动早期请求、并发启动和异常退出重开有专门测试；旧版 M3E 没有该协议，升级时应先关闭旧版。不同 Windows/RDP 会话分别保留自己的窗口。

## 为什么重开后回听要等待

此前选中历史会议会核验来源清单、时间轴、源轨、完整 WAV，并再次完整解码独立 M4A。即使实际播放 WAV，也等待 M4A 解码和峰值扫描完成；等待时播放按钮不可用，用户在回听页看不到原因。同一会议重复选择还可能取消并重启检查。

M3F 的普通 WAV 回听仍验证实际播放文件和全部来源/时间轴依赖，但不再为它解码不使用的压缩副本；结果中不将压缩文件冒充已验证。M4A/MP3 导出有各自编码、hash 与 EOF 校验。已有 RF64 对应压缩回听文件仍须完整验证；新超大 WAV 无兼容副本时明确提示暂不支持程序内回听，可按需导出 M4A，不增加特殊适配。

等待校验时回听区明确显示状态；用户点击播放或字幕定位可保存一次明确播放请求，验证成功且仍为同会议/可播放状态时才执行。切会议、取消、停止回放、启动录音、暂停/停存或关闭都会清除待播请求。同场重复点击不重复开启校验。首次大文件的必要磁盘读取仍可能耗时；没有取得目标电脑数据，不能承诺其 10 秒等待已经在该机缩短为特定数值。

## WAV、M4A 与长会议识别

本地 Whisper 使用完整混音 WAV 转出的 16 kHz / mono / PCM16 识别副本，约 **115.2 MB/小时**，3 小时约 **345.6 MB**（十进制，不含原轨、模型和转写输出）。M4A 不参与此路径。默认自动混音现在传入 `encoder: null`，不生成额外 M4A；选择 M4A/MP3 导出时才编码。既有 M4A 和原件不删除。

当前锁定 whisper.cpp b5130 CLI 将整段解码音频放入 float 缓冲，模型/计算另有内存开销；压缩为 M4A 不消除解码内存。因此超长录音的稳定方案通常需要分段、重试与全时间轴字幕拼接，但用户明确暂缓这项适配。本轮保持现有识别路径，不新增自动切段，不静默截尾；现有会议时长上限、WAV 容器长度、磁盘预算、24 小时任务超时及引擎失败提示继续生效。3 小时是用户主要场景，**不代表本轮执行过完整 3 小时真实识别验收**。

上游依据：[b5130 common-whisper.cpp](https://github.com/ggml-org/whisper.cpp/blob/b5130/examples/common-whisper.cpp)，项目对应 `AsrAudioPreparation` 与 `WhisperTranscriber`。本轮只查看公开源码，没有上传音频、转写或日志。

## 已执行验证与证据

开发环境：Windows 11 25H2 26200.9457 / x64；SDK 10.0.401，runtime 10.0.12，NAudio 3.1.0。原始环境记录 `.local/M3F/environment.json`。

Release 构建 **0 警告/0 错误**，**235 项默认自动回归 PASS**。新增普通 WAV 回听跳过无关压缩解码但不绕过源/WAV 完整性校验的 2 项回归；包含真实按需 M4A 编解码/全长与原件 hash。证据 `.local/M3F/build-01.log`、`build-app-final.log`、`.local/TestResults/M3F/default-01/results.json`。

实际 Windows WPF 测试接口通过：三语言最小窗口、矢量 Logo 100%/200% 渲染检查、ASR 多字段红框/自动清框/缺会议/缺混音拦截；录音缺来源/端点/mic、异步预检失败、重复开始均不启动 Host、不创建新录音，历史与草稿保留，**两个已转写会议实际静音回听成功**。还检查正常应用自动混音流程不生成 M4A。证据 `.local/M3F/ui-*`、`recording-*` 的 `report.json` 与 PNG。不是物理鼠标或真实多显示器 DPI 验收。

排队回听通过 10 项 WPF 检查：单次校验/重复点击、最后字幕定位的实际静音 MediaOpened、缓存导航、停止/切会/Recording/Paused/Stopping/Faulted/关闭取消的晚完成禁播、失败可见与原件 hash 保持。`.local/M3F/replay-102212475/report.json`。

单实例真实多进程/WPF **6 项 PASS**：最小化恢复、测试 scope 隔离、正常重开、崩溃重开、六进程竞速仅一个主实例、窗口创建前多个请求排队。`.local/M3F/single-instance-02/report.json` 与 `environment.json`。自动后台启动测试中 `foregroundGranted=false`，Windows 没有授予抢焦点；**窗口恢复为非最小化且可见已通过**，不夸称强制前台通过。测试使用独立命名空间，不接触用户生产实例/会议库。

失败记录保留：首轮单实例脚本对尚未生成的进程报告按非空访问，StrictMode 报错，修脚本后全部通过；录音 UI 测试首轮在 WPF 布局完成前断言 `IsVisible` 失败，等待实际 Render 后通过。目视检查发现 ASR 顶部字段缺少 AdornerLayer，新增控件级 AdornerDecorator 并补真实 Outline 断言后通过。

所有文件测试使用自生成音频或既有公开样本；不启动采集、不启 mic、不进入会议、不改系统设置/用户其他项目。现有未提交改动保留，未提交或推送。

完整 win-x64 交付为 `artifacts/ProjectEcho-win-x64-20261002-M3F.zip`，**260,806,537 字节**，SHA-256 **`72e387b146f34d65148addcba11a9130fb10a319286345cf02d5f33c0ab5af82`**。包含 CPU/Vulkan/base 与许可证、不含 Data；658 项清单文件+manifest，解压内容 **452,328,320 字节**。manifest SHA-256 `e2f4ef7fb4ee67dc795d29faf616bc2776c3c342ec60e7d2d536e90621a53049`。

**最终包实际核验 PASS：**独立解压至 `artifacts/E61/`，逐文件 hash/长度、自带 Host runtime/IPC、新 WPF 三语/录音验证/回听排队及 M3E Log 回归通过。Core/Capture/Export 三生产 DLL 与 235 项自动回归使用的文件 SHA-256 完全一致。包内再次执行隔离单实例六项通过，随后普通 **无 smoke 参数** 连续启动两次，第二进程退出且原 MainWindow 保持；所有自建程序已正常关闭。普通启动附加脚本首轮将 Windows 输入法指示窗误认作主窗，改用所属 PID＋ProjectEcho WPF 窗口匹配后通过；没有强制终止或操作用户应用。

证据：`.local/M3F/zip-verification.json`、`tested-components.json`、`normal-startup.json`、`single-instance-bundled/report.json`、`ui-102837754/report.json`、`recording-102840006/report.json`、`replay-102841723/report.json`及`.local/diagnostics/portable-20261002-182834/portable-smoke.json`。ZIP 内说明为打包时快照；本段及同名 `.package.json` 是打包后追加核验，不改变已测 ZIP。ASR 引擎/base 与 M3E 相同，本轮没有重新进行 GPU 或长会议识别。

使用：先关闭旧版，完整解压新包后打开 `ProjectEcho.exe`，设置中选择原 Data。未选来源时先按红框纠正；历史回听校验中可点一次播放等待。需要压缩文件时在导出页选择 M4A/MP3。请保留原 Data 和旧原件。

目标新电脑、物理键鼠、高DPI多显示器、3小时自然会议本地识别、RF64新适配、真正干净Windows完整录音闭环及原M1/M2平台/双设备长期同步不因本轮升为通过。计划/合成检查与现场验收分开记录。
