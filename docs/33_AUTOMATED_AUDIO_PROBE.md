# 无界面音频集成测试入口

2026-09-29。用户提出让代理自主测试，避免人工/Computer Use 的点击延迟。本轮已实现并真实运行。入口复用生产 CaptureHost、同一份 WPF IPC 客户端源码及 Windows WASAPI；没有新增网络控制接口、包版本或工具依赖。

## 使用

```powershell
# 构建与默认无设备测试，不会录放音
.\scripts\Dev.ps1 -Action Build
.\scripts\Dev.ps1 -Action Test

# 显式选择的实机测试：约6秒低音量合成音，mic固定关闭
.\scripts\Test-AudioIntegration.ps1
```

底层参数为测试程序的 `--synthetic-audio-probe`。结果在 `.local/AudioIntegration/<UTC时间-GUID>/probe-report.json`；WAV、capture-result 与 journal 在其 `recording/` 子目录。每次新建目录，原件不覆盖、不上传，整个目录被 Git 忽略。退出0表示全部已定义断言通过；非零必须检查报告。

普通 `Dev Test` 不隐式运行这个入口。实机命令不接收外部PID、会议地址、mic、端点兼容或用户指定输出路径，只能捕获本次刚创建的合成音子进程。

## 固定握手顺序

1. target/control 两个独立子进程建立真实播放session，先持续送静音并回报 ready。
2. ready 后读取 PID、启动时间和可执行路径，重新枚举真实音频session；根进程与音频进程都须完整匹配本轮子进程。不能选择测试父进程树或其他应用。
3. 使用生产 IPC 向 CaptureHost 发 Start，固定 ProcessTree、mic=false、最长10秒，并执行生产预检。
4. **收到 Host 的 started 回执后才发 GO 播放声音**，不靠延时猜测，也不依赖鼠标点击。
5. target 播放5秒660Hz，再接1秒880Hz尾标记；control 播放6秒440Hz。每路48kHz双声道、峰值≤0.02，短渐入/渐出减少点击声，不改系统音量。
6. 播放器取完有声帧后继续静音500ms再回报 done；父程序发 Stop，等待保存终态及 Host 真正退出，再关闭测试子进程。
7. 自动检查信号长度/连续性、尾标记、440Hz阈值、原件哈希、无mic文件、时间戳与journal。

500ms是渲染余量，不是物理扬声器完成测量。子进程28秒取消、30秒独立硬退出；父流程和每次等待均有截止时间。超时关闭生产IPC触发停采，不杀 CaptureHost 制造 Saved；仅对自己创建的合成音子进程实施必要的终止。已有 CaptureHost 或另一轮探针运行时拒绝启动，输出路径拒绝越界/重解析点。清理失败记录 FAIL，保留原件。

## 本轮验证

Windows **26200.9457 / 25H2 / X64**，实际 SDK `--version` 为 **10.0.401**，NAudio **3.1.0**。报告 `sdkPinned` 是 global.json 声明；实际 SDK 命令另行执行核对。

| 检查 | 实际结果 |
|---|---|
| 产品及测试工程构建 | PASS，0错误/0警告；本轮无restore或下载，漏洞审计仍未完成 |
| 默认无设备测试 | **38 PASS / 0 FAIL**，新增波形、信号夹具、身份/started门控检查 |
| 首次实机入口 | FAIL：新子进程 MainModule 尚未可读导致空引用，未到录音/GO；已改为 ready 后读取并核验身份，失败证据保留 |
| 第二次自动实采 | 无Computer Use，固定测试目标ProcessTree，mic=false |
| 原轨 | 48kHz双声道Float32，649包、311520帧、6.49秒 |
| 主信号/尾标记 | PASS；约4.9923秒660Hz与0.9923秒880Hz（幅度加权等效长度），顺序/连续性符合断言 |
| 440Hz | `Forbidden440Absent=true`；峰幅约0.00000910，阈值约0.00035532 |
| QPC/flags/checkpoint | 本产物无QPC gap/overlap、timestamp_error、discontinuity或水位/头偏移审计错误；7个checkpoint |
| 设备帧计数 | **仍恒零**；648次device_position_unreliable、648次device_overlap，加capture_incomplete共1297项错误 |
| 原件与派生混音 | 原件哈希不变；保留Complete=false；带警告混音313280帧，覆盖约6.5267秒会话时间 |
| 停止与清理 | Host退出、两个合成子进程exit=0、最终剩余探针/Host进程数0；无mic轨/文件 |
| 总结果 | **FAIL**：时钟问题未解决；M0未标完成，M1尚未开始 |

首次失败证据：`.local/AudioIntegration/20260929-024210-51b4be6bbc6f427ba7449642953abd53/probe-report.json`。

第二次完整执行：`.local/AudioIntegration/20260929-024316-5cbac342a5a84e629bd65820cea90dea/probe-report.json`，全部错误在同目录 `audit-errors.json`。构建/测试/命令日志在 `.local/diagnostics/audio-probe-20260929/`，默认测试结果在 `.local/TestResults/M0/audio-probe-20260929-reviewed/results.json`。构建无警告沿用上一轮本地缓存资产，不代表漏洞审计通过。

## 证据边界与后续扩展

信号分析使用200ms Hann窗，每声道分别测量后取最大值，避免反相抵消隐藏混入声音。要求约5秒连续660Hz再接约1秒880Hz及正确顺序，拒绝明显截短、缺尾、静音、非有限样本、损坏WAV和超过阈值的440Hz；不只是检查文件非空。

`Forbidden440Absent` 只证明目标文件内未检出超过阈值的440Hz。两个子进程实际建立session并回报样本提交，但本轮未独立测量control非零输出，不能升级成完整进程/浏览器隔离验收。尾标记与500ms余量不证明随机Stop的每个末帧；T51仍待M1逐帧验证。信号通过不覆盖时钟FAIL。

| 类别 | 当前覆盖 |
|---|---|
| 算法、WAV、合成故障 | 默认自动测试，无设备采集 |
| WASAPI激活、生产IPC、受控进程采集/保存/时钟 | 本轮已实机自动执行，可重复复现设备计数问题 |
| M1随机Stop、积压、崩溃恢复、长时间标记 | 可扩展此入口，当前未实现或未完成对应验收 |
| UI布局/用户操作体验 | 此入口绕过WPF交互，保留少量UI检查 |
| Zoom/Webex、Chrome专用实例、真实mic/回声 | 仍须各自环境及授权，不能从本次合成进程结果推断通过 |
| 人工听感、两小时同步 | 本轮未验证；长时间自动标记可后续实现，仍需实机证据 |

新增组件为 `SyntheticAudioProbe.cs`、`ProbeToneSource.cs`、`ProbeSignalChecks.cs`、`ProbeSafetyTests.cs` 和脚本；测试工程注册明确CLI路由、拒绝未知参数、链接原有CaptureHostClient并复制Host运行文件。生产录音实现与原有时钟失败标准未修改。

独立审查覆盖进程范围、started门控、故障清理、报告边界；修复身份读取竞态及单个子进程清理异常跳过其余清理的问题。异常超时/强制退出尚未实机注入，不称已通过。后续开发可在本次固定合成范围内复用入口，无需用户手动制造来源或抢点录音；这种授权不扩展到真实会议、mic或系统混录。
