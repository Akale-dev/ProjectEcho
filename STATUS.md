# Development status
Updated: 2026-09-30

## 最新结论：M3b 七项反馈已修复，真实 mic 复测待用户主动执行

**已实现：**针对三份 16k mic 失败索引支持的时钟问题，增加连续设备帧证据约束的短时 QPC 偏差处理、磁盘严格复算、完整原包跨块映射及收尾诊断；保留真实关麦/暂停段间隙。支持初始不选 mic 后在录音中明确选择开启，成功后固定设备，预算拒绝不连带中止远端 writer。回听按钮为图标，支持六档倍速和当前版 SRT/VTT 导出；字幕改为标点/token边界分句，校订页自动读取版本，新会议清除旧会库选择。旧音频、失败记录、字幕与人工修订保留。

**已测试：**Windows25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release **0错误0警告，141组默认回归PASS**。包括8组QPC/跨块/边界与篡改检查、首次开麦失败/重试/固定设备/预算保护、5组句级字幕。实际WPF自动读取/未保存编辑保留/新会议清选/隐私回执，静音回听六档与0.5x/2x实际推进、所选字幕版本导出往返通过。现有base识别公开JFK新参数完整一句、取消重试/原件哈希通过。用户报告Zoom **mic关闭**时录音/保存/转写通过，记录为PASS_USER_REPORTED。

**交付：**新完整自包含目录 `artifacts/ProjectEcho-win-x64-20260930-M3b-Fix2`，运行其中 `ProjectEcho.exe` 并保留整个文件夹。旧资料在设置选择旧包Data目录（目前每次启动需重选）；旧字幕需重新识别才使用新分句逻辑。最终发布验证及证据见 [51](docs/51_M3B_FEEDBACK_FIXES.md)。

**发布复验：**617文件完整性、默认WPF隐私启动与包内Host运行时通过；直接运行最终包的真实Whisper、转写/清选、静音倍速回听/字幕导出、导出关窗四流程全部exit0。证据 `.local/diagnostics/m3b-fixes-bundled-workflows-02.json`；不替代真实mic或干净Windows。

**未测试/限制：**本轮没有采集、启麦或入会。历史三次记录没有被拒绝包/异常堆栈，使用其已知相对时钟加明确合成包复现旧故障机制，不能声称原堆栈已取到。**修复后真实16k mic+Zoom/中途首次开启仍NOT_RUN**；44.1k硬件、两小时双设备同步、干净Windows等原闸门不变。字幕时间仍approximate；中文/混合语言/长会议质量未验。状态为`FIX_IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_MIC_RETEST_PENDING`。

证据：`.local/TestResults/M3bFixes/final-02/results.json`、`.local/diagnostics/m3b-mic-fix-diagnosis.json`、`.local/diagnostics/m3b-fixes-environment.json`、`.local/diagnostics/m3b-sentence-real-jfk-20260930/report.json`。以下为历史记录。

## 最新结论：M3b 界面重构、字幕播放器与多规格录音链路已实现

**已实现：**01新会议录音、02左侧日期/标题会议库、03导入录音、左下04诊断与设置；每场会议的基本信息/导出音频/回听/转写与搜索四标签。录音栏只在录音页，播放器只在回听页；支持字幕版本、当前字幕、点击定位、暂停/继续与进度条。历史混音增加源清单/索引指纹，重开校验复用不自动生成重复文件。mic格式协商不再固定48k；ASR实际经通用接口调用默认Whisper，Qwen3-ASR仅预留准备。

**已测试：**Windows25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release构建0错误0警告，**128组默认测试全部通过**。含16/22.05/44.1/48/96k合成原轨边界→磁盘恢复→混音→16kASR完整时间轴、4组ASR接口、6组安全缓存复用。实际WPF字幕点击/暂停/暂停中定位/当前字幕/继续播放、录音状态防回放、会库选择与缓存重开通过。现有whisper.cpp/base经新接口真实识别11秒公开英文样本通过（开发版UI2798ms，不作普遍速度承诺）。无采集、mic或上传；字幕时间仍为近似值。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3b/ProjectEcho.exe`，保留整个文件夹。617个发布文件、231143051字节；发布文件完整性、默认WPF启动、包内Host IPC/运行时已通过。本机Whisper配置在发布校验后加入Data/settings，不把模型、录音或本机配置收入发布清单。中文场景手册为 [49](docs/49_M3B_USER_GUIDE_ZH.md)，包内为`使用说明.zh-CN.md`。旧录音未迁移，新包须在设置选原Data目录才能读取。

**未测试/限制：**真实44.1k设备Initialize/GetBuffer及转换后的实际设备时钟、录制中修改系统格式仍NOT_RUN；本轮没有启麦，不用合成测试冒充设备验收。M2双设备两小时同步、干净Windows、未验/暂缓现场项目保持原状态。中文/混合语言/噪声/长会议ASR质量、真实显示器DPI/高对比/读屏未验。Qwen未部署。M3b状态为`IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_44K1_PENDING`。

**发布包复验：**直接运行新包EXE的文字/历史混音重开、导出关窗、字幕播放器、真实Whisper四流程全部退出0；证据`.local/diagnostics/m3b-bundled-workflows.json`。包内运行时与哈希证据`.local/diagnostics/portable-20260930-171813/portable-smoke.json`，不替代干净Windows检查。

详情：[50实施、失败记录与证据](docs/50_M3B_IMPLEMENTATION_AND_VALIDATION.md)、[48 ASR合同](docs/48_M3B_LOCAL_ASR_BACKENDS.md)。以下保留原M3及更早结论。

## 最新结论：M3 已接入真实 base，支持外部录音、字幕单击回听与 SRT/VTT 导出

**已实现：**按用户授权在 `H:\AI\Whisper` 安装官方 whisper.cpp **1.9.4 / b5130 CPU x64** 和多语言 **base**，核对官方 SHA-256 与 MIT 许可。开发版和新便携包的本机配置已接入。可导入 WAV/MP3/M4A，保留原件；单击字幕即跳转并播放，支持重复/快速点击与磁盘重开。识别原稿与修订可保存为 UTF-8 SRT/VTT，保留完整录音时间轴的毫秒起止、暂停和重叠；无时间文字不造字幕时间。

**已测试：**Windows **26200.9457 / x64**、SDK10.0.401、runtime10.0.12、NAudio3.1.0。最终 Release 0错误/0警告，**113组默认测试 PASS**；真实11秒公开JFK样本经M3转写、取消/重试、原件哈希和SRT/VTT往返全部通过。发布版实际UI自动语言/2线程识别全流程 **2.762秒**；实际WPF字幕点击9种定位/继续播放检查、4种录音状态保护、字幕导出再导入与重开均PASS。没有采集、mic或上传。不是物理鼠标/可听输出逐样本或普适识别准确率验收。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3-Whisper/ProjectEcho.exe`，保留整个文件夹。616个发布文件、231040862字节；完整性/默认启动/包内Host运行时PASS。发布包直接执行真实识别、字幕回放、既有文字流程和导出关窗检查均退出0。Whisper独立存放在用户指定目录；本机 `Data/settings/whisper.json` 是发布校验后添加的可变本机配置，不属于616个发布文件，未把音频/模型打入发布清单。

**未测试/阻塞：**中文/中英混合、专业词、噪声与长会议识别仍待实测，时间标记仍为approximate；短英文样本耗时不作普遍承诺。M2双设备两小时同步、干净Windows、未验平台/暂缓现场项保持原状态。首轮字幕路径比较错误导致“打开却不播放”的FAIL已保留并修复；旧WPF M4A整秒时长对照失败也保留，未把它写成新PASS。M3状态为 `IMPLEMENTED / PUBLIC_ENGLISH_MODEL_AND_REPLAY_VALIDATED`，不代表M2现场闸门已全过。

详情：[47真实模型与同步字幕](docs/47_M3_WHISPER_AND_SUBTITLE_REPLAY.md)。以下保留此前历史结论。

## 本轮结论：M2复审通过已有回归；MP3、M3文字工作流及任务式UI已实现

**已实现：**保留WAV/M4A，新增实际MP3压缩与限大小分片；M3支持TXT/MD/SRT/VTT/JSON导入、分片时间映射、原稿/修订独立保存、本地搜索和定时回听，提供可取消/重试的本地whisper.cpp适配。按用户决定不加入SQLite。界面拆为录音、会议库、导出、转写与搜索、诊断五页，增加明暗主题与原创多尺寸EXE图标。

**已测试：**Windows 25H2 **26200.9457 / x64**；SDK10.0.401、运行时10.0.12、NAudio3.1.0。最终Release构建0错误/0警告；**105组默认测试PASS**（含实际MP3编码/解码、16k完整识别副本、M3导入/修订/映射和合成CLI进程失败/取消/重试）。实际WPF文字工作流、Paused回听锁定、关窗等后台清理通过；37张布局渲染图审查、EXE图标提取通过。本轮全部为合成文件/无采集验证，没有启mic、真实录音或上传。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3/ProjectEcho.exe`，保留整个文件夹；616个随包文件、230924906字节。文件哈希、默认隐私启动、Host IPC及包内运行时解析通过；新包直接执行M3文字流程和M2导出关窗检查退出0。旧包与原始音频保留。

**未测试/阻塞：**M2双设备声学同步、干净Windows及未验平台/暂缓现场检查继续保留。M3没有真实whisper.cpp/模型，真实识别准确率、速度与混合语言测试NOT_RUN；合成CLI夹具不是模型验收。MP3两小时/外部播放器和真实DPI/高对比/键盘读屏未测。M2未被标成全部退出闸门通过；M3为`IMPLEMENTED / MODEL_ACCEPTANCE_PENDING`。

本轮详见 [46实施与验收](docs/46_M3_MP3_UI_IMPLEMENTATION.md)、[45转写合同](docs/45_M3_TRANSCRIPTION_CONTRACT.md)。首轮99PASS/6FAIL及修复前截图/日志均保留。以下是历史M2及更早记录。

## 当前结论：M1问题已修复，M2便携验证版已实现；现场退出闸门仍未全部通过

**已实现：**可靠录音、独立mic真正停采、恢复与磁盘时间轴；完整mono WAV/RF64及M4A、真峰值保护、实际字节/时长分片、本地安全导出和可取消后台任务。复审修复了损坏清单数值、早于epoch的QPC、慢周期校验阻塞采集、归档错目标、关窗未等清理，以及压缩重命名后哈希失败的状态问题。原件始终保留；不上传、不依赖模型。

**已测试：**Windows26200.9457/x64，Release构建0错误/0警告；**91组默认测试PASS**。受控远端实际录制**7201.165849秒**，345655961帧完整WAV、全M4A和5分片的EOF/五处标记/覆盖/哈希通过，mic始终关闭，进程退出。实际WPF输出WAV/M4A首尾标记间隔2.300秒通过；M4A虽有WMP整秒时长显示限制，实际尾声未据此截断。受限外网、无模型环境下，新录音在旧导出失败/取消时持续写入并完成实际混音/压缩导出。用户报告的约30秒讲话/UI检查通过已记入。

**便携包：**`artifacts/ProjectEcho-win-x64-20260930-014055/ProjectEcho.exe`，须保留整个文件夹；616随包文件、230571321字节，来源/许可/哈希齐全。本机默认隐私启动、Host IPC及实际包内运行时加载通过。测试工具驱动目录冲突已修正，不需要另装.NET；不将开发机或测试驱动结果称为干净Windows通过。

**最新长文件复验：**采用上述最新包组件，独立复制已核验的两小时原件后，完整导出、真峰值/容器时间表和5分片检查全部PASS；各片实际字节≤20,000,000、解码时长≤1800秒，原件与旧报告哈希不变。所有实录和验证进程已完成；详情见docs/44。

**未测试/阻塞：**双设备两小时声学同步缺共同参考，不能用单轨长录替代；没有可用的干净Windows目标，现有虚拟化未启用且未改系统功能；设备拔插和锁屏/睡眠按用户安排暂缓。Zoom/Webex桌面只有既有客户端测试音有限证据，网页与同时隔离对照未全验。M2不能因此标作全部退出闸门通过。

详细证据：[37实施报告](docs/37_M2_IMPLEMENTATION_REPORT.md)、[42完成条件补强](docs/42_M2_COMPLETION_AUDIT_AND_HARDENING.md)、[43时长元数据](docs/43_M4A_DURATION_AND_WPF_METADATA.md)、[44实际Windows验收](docs/44_M2_AUTOMATED_WINDOWS_ACCEPTANCE.md)。所有早期FAIL和限制保留，不用后续成功覆盖。
以下按时间保留历史证据，后续结果不会覆盖早期失败。阶段表及本节是当前状态。

**2026-09-29 真实mic跟进：**用户已明确授权真实mic测试，并自行将Realtek端点改为48kHz。已修复音频就绪事件未参与等待导致的短缺口、健康时间差下溢；对尚未验证的端点/mix跨采样率组合在启动前明确拒绝，未放宽时钟或停采边界标准。**58组默认自动测试PASS；真实mic短测连续两次PASS**，每轮约20秒、mic10.56秒，开关/5秒暂停/恢复/固定设备重开与精确落盘边界通过；mic1056包全部QPC/设备计数有效，无缺口，原件不改，进程退出。初期沙箱访问拒绝、44.1→48组合失败及一次缺口失败均保留。正常用户Windows权限已Allow，无需重复索取测试授权。仍待手动说话/UI、拔插、锁屏/睡眠和两小时公共标记同步。详见 [36_REAL_MIC_TESTS_AND_MANUAL_CHECKLIST.md](docs/36_REAL_MIC_TESTS_AND_MANUAL_CHECKLIST.md)。

**2026-09-29 M1实施：**采纳独立时钟能力分类，M0以`FEASIBLE_WITH_LIMITATIONS / QPC_ONLY`进入M1，未修复/补造device计数，旧FAIL不变。M1已实现普通录音、暂停/恢复、固定mic真正停采、30秒块、checkpoint/journal/恢复副本、书签与健康/UI控制。Debug/Release均0错误/0警告，**55组默认自动测试PASS**。真实受控进程的40秒暂停/跨块录制、父连接失联停采、32秒Host崩溃恢复和**15分钟数字静音**均通过各自断言；静音保存900.658302秒完整时间轴、31块/43227840帧，PCM全零，进程全部退出。设备时钟/双轨同步单独保留未验收；真实mic、两小时同步、锁屏/睡眠未全测。详见 [35_M1_IMPLEMENTATION_REPORT.md](docs/35_M1_IMPLEMENTATION_REPORT.md)。未进入M2。

**2026-09-29 自主音频测试入口：**已增加 `scripts/Test-AudioIntegration.ps1`，通过生产Host/IPC自动协调测试子进程的播放、录制、停止及离线核验，不再依赖Computer Use点击。固定只录新建测试进程、mic关闭。产品构建0错误/0警告，默认无设备测试 **38 PASS**。首次入口启动竞态失败后已修复；第二次真实Windows自动实采的660Hz/880Hz顺序与时长检查PASS、440Hz未超过检测阈值，Host/子进程全部正常退出，原件不改。但新参数下设备计数仍恒零，**总结果FAIL（1297项时钟/完整性错误）**，M0不标完成、M1未开始。详见 [33_AUTOMATED_AUDIO_PROBE.md](docs/33_AUTOMATED_AUDIO_PROBE.md)。漏洞审计仍未完成。

**2026-09-29 复审修复：**已修复重复 QPC 被误判正常、shared/event 初始化参数、writer 未就绪即 Start、逐路收尾拖延 mic Stop，以及 IPC 输出阻塞拖住 Host 心跳保护的问题。最终 Windows 构建 0 错误/0 警告；**35 项自动测试 PASS**，WPF 无采集启动和 Host 两项无采集 IPC 检查 PASS。本地缓存恢复本次显式关闭 NuGetAudit，漏洞审计仍未完成（原基线 6 NU1900），不能将无警告等同安全通过。七份既有清单离线核验仍呈 device 恒零，旧 FAIL 不变；新参数实采/人工音频测试 NOT_RUN。本轮没有录音、启 mic、入会或继续暂缓的浏览器测试。**M0 仍 IN_PROGRESS / CLOCK_QUALITY_LIMITED；尚未进入 M1。** 详见 [32_M0_REVIEW_REPAIR.md](docs/32_M0_REVIEW_REPAIR.md)。

**2026-09-28 最新跟进：**用户确认第一份 Webex 录音期间未播放测试音，现更正为静音对照通过；第二份有声测试及回放也通过。Zoom 测试音回放和既有 Chrome 合成音实测保留。用户指示暂缓后续浏览器测试，网页/隔离未执行部分保持 NOT_RUN，不再要求现在继续该测试。所有 mic 关闭、原件不改；设备计数恒零的 Complete=false/时钟审计 FAIL 仍单独保留。见 [30](docs/30_M0_ZOOM_SPEAKER_TEST.md)、[31](docs/31_M0_WEBEX_SPEAKER_TEST.md)。最近代码基线为构建 0 错误、6 NU1900、30 项合成 PASS；本轮只更正记录。

当前目录为独立项目 `H:\AI\ProjectEcho`。已建立六工程 C#/WPF 解决方案、真实来源枚举/预检、单一 CaptureHost、主动触发的 10 秒短试录、默认关的独立 mic、两路电平、原轨/时间索引及录后混合 WAV 回放入口。原先等待用户本人按录音按钮的安排已由明确代操作授权更新，不再作为阻塞。

**历史预检：**在授权前的现场检查中，已实际选择 Zoom / Webex 音频进程树并通过静态预检；观察时均为 Inactive，输出为“远程音频”，活动 mic 为 0。当时 Chrome 是普通实例，尚无其音频 session 候选。这些结果不代表当前 PID、来源或采集状态，录音前须重新核对。

**已测试：**Windows 26200.9457 / 25H2 / X64；项目 SDK 10.0.401 与 global.json 一致；NAudio.Core/Wasapi 精确 3.1.0；实际产品构建成功，0 编译错误。合成算法/WAV测试、WPF 启动及真实设备/会话枚举、无采集 Host IPC 检查见 [M0 报告](docs/24_M0_REPORT.md)。NuGet 返回 NU1900：当前网络无法获取漏洞信息，不能把依赖安全审计称为通过。

**未测试：**端点模式、mic、Zoom/Webex 真实会议与网页、440/660/880Hz 同时对照隔离及长录音。Chrome 合成音、Zoom/Webex 客户端测试音的有限录制/回放路径已有证据；设备时钟完整性仍失败。M1 停采边界/恢复/同步与 M2 压缩导出/便携包仍未验收。

**阻塞/限制：**电脑重启后原生 Chrome URL 识别仍失败。用户授权全标签页枚举后 Playwright 正常审批通过，但只返回其 about:blank；随后项目合成页被 file: 协议限制拒绝。未绕过检查。设备位置恒零的底层原因待查；用户暂无 mic；NuGet 漏洞服务不可达。M0 保持进行中。

**工具分步诊断：**窗口枚举/绑定成功，文件和 HTTP 状态读取均失败。Computer Use 相关三个插件本地配置均启用；当前聊天另有内置浏览器 getInfo 路由缺失日志，但不能视为原生 Chrome 错误根因。没有证据支持改权限、安装扩展或改录音源码。详见 [26_COMPUTER_USE_DIAGNOSIS.md](docs/26_COMPUTER_USE_DIAGNOSIS.md)。

## 2026-09-28 规划已采纳，工程环境已配置
以下为 M0 实施前的历史记录；“未开始”只描述当时，当前状态以本页顶部及阶段表为准。
用户已接受复审意见。已同步AGENTS、产品/架构/音频/存储/导出/UI规格、里程碑、需求追踪、57项验收及开发提示词；首版精简且M2包含完整便携包。正式状态见修订文件；下方首次复审记录保留为历史。

项目内SDK **10.0.401**已从微软下载并通过SHA-512核验，NAudio.Core/Wasapi **3.1.0**已恢复到项目缓存。WPF/NAudio无采集环境小程序构建0警告/0错误、运行和self-contained发布后本机运行通过。Git复用现有安装，仅初始化本地仓库；无身份/全局PATH修改、无提交推送。详细版本、命令与证据见 [docs/21_ENGINEERING_SETUP.md](docs/21_ENGINEERING_SETUP.md)。

**未测试：**真实录音API、四平台、mic隐私、同步、恢复、混音，以及完整应用的干净环境运行。M0–M6保持未开始，T01–T57均为NOT_RUN；环境小程序不是录音应用。

## 2026-09-28 规划复审（未实施 M0）
首次审查结论见 [docs/20_PLAN_REVIEW.md](docs/20_PLAN_REVIEW.md)。当时仅提出建议、未改原标准；后续用户采纳后的更新见上节。

本机只读检查：Windows build 26200.9457 / 25H2 / X64；PowerShell 7.6.5；Git 2.53.0.windows.1。脚本检查范围内未发现 .NET SDK，未下载依赖。修改本文件前原交接包 63 个文件哈希校验通过；这不是产品测试。证据在 `.local/diagnostics/environment.json` 与 `.local/diagnostics/plan-review-2026-09-28.json`。M0–M6 状态及 T01–T50 的 NOT_RUN 均保持不变。

| Stage | State | Evidence |
|---|---|---|
| M0 capture feasibility | FEASIBLE_WITH_LIMITATIONS / QPC_ONLY | docs/34；实际API QPC/帧数可用；旧device审计FAIL与未测平台保留，不声称同步通过 |
| M1 reliable recording | IMPLEMENTED / FIELD_ACCEPTANCE_PENDING | docs/35–44；讲话/UI用户通过、修复后固定mic短边界通过；两小时远端通过；双设备声学/拔插/系统状态仍未全验 |
| M2 mixed/export MVP | IMPLEMENTED / FIELD_ACCEPTANCE_PENDING | docs/37–46；原M2回归保留，新增MP3实际编解码通过；20260930-M3完整包；T46干净Windows阻塞 |
| M3 local ASR/import | IMPLEMENTED / PUBLIC_ENGLISH_MODEL_AND_REPLAY_VALIDATED | docs/45–47；真实base英文/UI识别、取消重试、外部录音、字幕点击回听及SRT/VTT往返通过；中文/混合语言/长会议仍未验；无SQLite |
| M3b meeting UI / formats / ASR boundary | FIX_IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_MIC_RETEST_PENDING | docs/48–51；141组通过；七项反馈修复与WPF/公开base通过；修复后16k mic+Zoom和44.1k硬件待验，Qwen仅准备 |
| M4 project analysis | NOT_STARTED | none |
| M5 optional tab extension | OPTIONAL_NOT_STARTED | none |
| M6 release verification | NOT_STARTED | none |

## Next task
使用M3b-Fix2完整便携包；在设置选择原Data读取旧会议。下一步由用户主动复测当前16k mic+Zoom启动，以及初始关mic/未选设备后中途开启；核对保存、关麦/暂停边界和回听。旧字幕重新识别后选择新版本检查句级效果。真实44.1k、获授权中文/中英质量、干净Windows与双设备同步保持后续未验项，不改系统格式或扩大音源制造通过。

## Per-stage reporting template
Implemented:
Tested (actual command/platform/evidence):
Not tested:
Known issues / risk:
Files changed:
Next smallest task:




