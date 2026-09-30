# 50｜M3b 界面、格式协商与本地 ASR 接口

2026-09-30。M3b 已实现并完成下列 Windows 自动验证；真实 44.1 kHz 麦克风仍未测试，不把合成输入或 WPF 路由事件称为硬件采集 / 物理鼠标验收。当前总状态见 STATUS，中文操作见 [49](49_M3B_USER_GUIDE_ZH.md)。

## 已实现

- 左侧 **01 新会议录音**、**02 会议库**、**03 导入录音**；**04 诊断与设置**固定左下。会议库按日期倒序显示日期和名称；外部录音标“导入于”，不伪造开会日期。主题切换移到设置。
- 选择会议后右侧为**会议基本信息 / 导出音频 / 回听 / 转写与搜索**四个标签。底部录音栏只在录音页，播放器只在回听页；顶部保持来源、mic 和状态入口。10 秒试录在录音页提供录后试听按钮。
- 回听支持字幕版本、当前字幕、字幕列表、单击 / Enter 定位、暂停 / 继续、停止、进度条；重叠字幕同时显示，无字幕区间不延用上一句。导入 / 读取字幕不自动播放；切会清除播放器与待打开请求。录音、暂停、停止保存和异常状态禁用所有回放入口。
- 普通历史会议重开只读校验已存在混音，不因点击列表重复生成。新 render 关联 `meeting.json`、`capture-index.jsonl` 和原轨指纹；检验完整 WAV 帧数 / 哈希与压缩 EOF。旧缓存缺关联指纹、源改变或损坏须主动重新生成，原件不改。切会取消旧读取，检查 / 恢复以右侧可见会议为准。
- 麦克风跨采样率时请求设备报告格式，并使用已编译验证的 NAudio 3.1 `AutoConvertPcm | SrcDefaultQuality`；预检、M0、M1、磁盘预算共用策略。同速率路径保持现状。原始 device / QPC、格式和帧数不改，严格边界检查仍在，不因格式不同自动换设备或扩大音源。
- 新 `ILocalTranscriber` 合同包含本地配置、输入规格、语言 / 线程、时间戳能力、结果与取消。App 实际调用默认 Whisper 适配器，旧 `whisper.json`兼容。Qwen3-ASR 是未来实施说明，未部署、未注册成可运行后端；详情见 [48](48_M3B_LOCAL_ASR_BACKENDS.md)。
- [中文使用说明](49_M3B_USER_GUIDE_ZH.md)覆盖录音、保存、回放、本地识别、时间轴字幕、搜索校订和导入 / 导出文件类型，并随便携包提供。

## 已测试

环境：Windows **25H2 / 26200.9457 / x64**，项目 SDK **10.0.401**、运行时 **10.0.12**、NAudio **3.1.0**。完整 OS 小版本来自只读系统检查；WPF `Environment.OSVersion`仅返回 26200.0，不用它抹掉 UBR。音源模式为 **SYNTHETIC_FILES / PUBLIC_FILE_IMPORT / MUTED_WPF_PLAYBACK**；没有启动音频采集、启用麦克风、打开会议、上传数据或修改系统设置。

| 检查 | 实际结果与证据 |
|---|---|
| Release 构建 | 0 错误 / 0 警告；`.local/diagnostics/m3b-build-04.log`、`m3b-build-layout-fix.log` |
| 默认回归 | **128 PASS / 0 FAIL**；`.local/TestResults/M3b/default-02/results.json`；113既有 + 5多速率 + 4后端接口 + 6缓存复用 |
| 多采样率 | 16 / 22.05 / 44.1 / 48 / 96 kHz；真实生产 writer、跨边界mic-off / 暂停、磁盘恢复、远端48k与mic混音、16k识别副本、完整EOF与原件哈希；输入包为合成，非硬件 |
| ASR接口 | 错后端拒绝、语言与线程传递、原稿 / 近似时间保留、数字零不启动模型、输入hash拒绝、独立CLI夹具取消 / 失败 / 重试；夹具不作准确率验收 |
| 实际 Whisper UI | 现有1.9.4/b5130 CPU / 多语言base，11秒公开英文样本，经通用接口、auto / 2线程成功；UI总耗时2798ms；`.local/diagnostics/m3-whisper-ui-20260930-090646-769/report.json`。不是普适速度或识别质量承诺 |
| 实际播放器 | 原9类字幕定位回归 + 新回听页路由点击、暂停、暂停中进度定位、当前字幕、继续播放、四标签 / 操作栏范围及四种禁回放状态；`.local/diagnostics/m3-replay-20260930-091445-536/report.json`；真实MediaPlayer静音运行，原件哈希不变 |
| WPF历史工作流 | 导入、修订、搜索、Paused保护、普通录音缓存重开不生成新文件、日期标题 / 当前选择同步、关闭等待后台；`.local/diagnostics/m3-transcription-20260930-091321-986/report.json` |
| 布局 | 38张实际WPF渲染，标准 / 最小窗口、明暗与150% / 200%像素倍率；`.local/diagnostics/m3-layout-20260930-091128-098/report.json`。小窗口字幕问题修正后另3图与播放器复验在 `m3-replay-20260930-091445-536`。像素倍率不是OS DPI切换 |
| 缓存安全 | 无额外输出；源 / WAV / 压缩损坏拒绝，同PCM但不同会议时间轴拒绝，旧无指纹拒绝，真实M4A EOF重开；默认回归 `m3b-saved-mix` |

构建与测试均通过 `scripts/Dotnet.ps1 -Arguments ...` 使用项目本地SDK / 缓存；没有安装新依赖。现有M2导出关窗检查也以 `--export-close-smoke`重跑通过，日志 `.local/diagnostics/m3b-close-01.log`。发布命令为 `scripts/Publish-Portable.ps1 -OutputDirectory artifacts/ProjectEcho-win-x64-20260930-M3b`，离线使用已缓存依赖。

## 发布产物

`artifacts/ProjectEcho-win-x64-20260930-M3b/ProjectEcho.exe`，须保留整个文件夹。**617个发布文件、231143051字节**；release-manifest SHA-256：`4579b3ecebde4a67d6030f1d458e04b75a69c42976a60bac986a4eb5792c2f17`。App、CaptureHost、自包含运行时、许可与来源清单齐备；发布时不含录音、转写、浏览器资料、Whisper模型或开发SDK。

`Test-Portable.ps1`实际通过617个文件/清单哈希、默认WPF隐私启动、包内Host IPC与运行时路径核验；证据 `.local/diagnostics/portable-20260930-171813/portable-smoke.json`。该测试未采集，且在开发机执行，**不是干净Windows验收**。随后仅为本机复制既有Whisper配置到新包Data/settings（此可变配置不属于617文件），直接启动发布版EXE执行4个流程均exit0，证据 `.local/diagnostics/m3b-bundled-workflows.json`：

- `--transcription-smoke`：文字导入/修订/搜索、会库日期与选择、普通会议混音重开无重复文件、关窗清理；`m3-transcription-20260930-091849-410/report.json`。
- `--export-close-smoke`：既有M2混音/导出取消与等待收尾通过。
- `--m3-replay-smoke`：实际静音播放器、字幕点击、暂停/进度/恢复、当前字幕、切会与四状态保护；`m3-replay-20260930-091852-534/report.json`及明/暗/最小窗口三图。
- `--m3-whisper-smoke`：真实本地base经通用接口识别公开英文、SRT/VTT导出及再导入；`m3-whisper-ui-20260930-091914-850/report.json`。

上述简写证据目录均位于项目 `.local/diagnostics/`。最终12张图的独立视觉复核及小窗口修复见 `.local/diagnostics/m3b-visual-review.json`。本轮默认测试输入为合成文件或已存在的公开样本副本，没有用户会议内容进入源码/Git或发布包。

新包的Data与旧包独立，既有录音不会自动搬迁。需读取旧录音时在设置选择原来的Data目录；目前选择仅本次运行有效。机器本地Whisper配置可在校验后复制到新包Data/settings，不属于发布清单。旧包、旧数据和早期FAIL证据全部保留。

## 失败记录与修复

1. 初次新回听页路由事件测试未触发播放，见 `m3-replay-20260930-090416-819/replay-stages.jsonl`；原因是新列表把鼠标处理放在父列表，旧夹具触发的是实际行的direct routed event。改为与已验证转写列表相同的行级EventSetter，之后完整定位 / 继续播放通过，未放宽断言。
2. 最小窗口的首轮字幕列表仅约一行高度，见 `m3-replay-20260930-091128-383/imported-subtitles-replay-minimum.png`。收紧重复标题与说明，字幕上部可滚动，保留底部播放器；复验 `m3-replay-20260930-091445-536/imported-subtitles-replay-minimum.png`可显示三条字幕，不以首轮图冒充通过。
3. 首个音频子任务构建调用将 `build`误绑定为DotnetPath，脚本拒绝且未构建；更正 `-Arguments`后成功，原log保留在 `m3b-microphone-20260930`。不是产品失败，也未安装系统SDK。
4. 复审修复了换数据目录 / 无效会议残留字幕、停止后的晚到MediaOpened、当前会议与左侧高亮不一致；减少后台页 / 暂停时全字幕扫描。没有删除旧回归或降低隐私 / 尾帧规则。

## 未测试与限制

- **真实44.1k麦克风 Initialize / GetBuffer、转换后的设备计数与QPC关系仍NOT_RUN**。真实采集须用户主动授权；五速率合成通过只证明协商逻辑与后续时间轴。录制过程中修改Windows设备格式也未验收，不承诺无缝切换。
- M2双设备两小时声学同步、干净Windows、未验平台 / 浏览器隔离及暂缓拔插、锁屏 / 睡眠保持旧状态。
- 真实物理鼠标、键盘全流程、屏幕阅读器、Windows高对比及真实显示器DPI切换未执行；WPF状态断言 / 渲染不替代它们。
- 中文 / 中英混合、专业词、噪声和长会的真实模型质量未验；时间戳为近似段落，无说话人识别 / 逐字对齐。Qwen未部署；不预先承诺其时间戳能力、依赖或速度。
- 旧混音需要主动生成一次才能使用新关联指纹复用；后台复核长录音需读取原件及解码，但可取消，不写重复音频。

追踪：R07/R18/R25；T52/T55/T57–T63。没有提交、推送、改Git身份或操作其他项目。
