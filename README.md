当前交付：[M5E Pro 完整便携 ZIP](artifacts/ProjectEcho-win-x64-20261006-M5E-Pro-Final.zip) · [M3O Lite 完整便携 ZIP](artifacts/ProjectEcho-win-x64-20261006-M3O-Lite-Final.zip)。[M5E 同步与验证](docs/94_M5E_LITE_SYNC_AND_PLAYBACK.md)说明 M5Dfix Qwen 修复的保留及本次更新；下方保留历史说明。

# ProjectEcho｜项目会议记录器

2026-10-06 **M3N Lite已交付，待用户验收后一起同步M5 Pro**：修复mic低底噪/DC不断重置无声计时；录音/暂停中可启用、取消自动结束及改时长，Host确认后生效并留证。回听新增−10/−5/+5/+10秒矢量按钮、上下切字幕及左右5秒快捷键，跳转保留暂停/播放和倍速；两栏从头播放改为左竖线+右三角，焦点/按下不裁切。378/0回归、18/18包内WPF、四进程启动及672文件中文ZIP通过。1681个Pro源码/旧包文件不变，32文件增量补丁及同步记录已保存。见[交付与验收记录](lite/docs/92_M3N_MIC_SILENCE_AND_PLAYBACK_CONTROLS.md)、[完整便携包](artifacts/ProjectEcho-win-x64-20261006-M3N-Lite-Final.zip)。真实mic/会议、目标机、物理键鼠/DPI和clean Windows待验；无新采集、下载、上传或关机。下方保留历史记录。

2026-10-06 **M3M Lite已交付，待用户验收后一起同步M5 Pro**：移除手动预检，开始录音自动检查保留；原按钮位置增加自动结束勾选，默认关闭/5分钟，可选1、5、10分钟，所选根进程退出或全部已启用来源无声音到期后正常停止并保存。mic声音重置，暂停不计；缺包不冒充静音，尾包与结束原因持久保留。362/0回归、16项包内WPF覆盖（含剪贴板权限复验）、四进程启动及671文件中文ZIP核验通过。1680个Pro源码/旧包文件hash保持，23文件增量补丁和同步清单已留存。见[交付与验收记录](lite/docs/91_M3M_AUTOMATIC_RECORDING_END.md)、[完整便携包](artifacts/ProjectEcho-win-x64-20261006-M3M-Lite-Final.zip)。真实会议/麦克风/目标机及旧闸门仍待验；本次未新增采集、下载、上传或关机。下方保留历史记录。

2026-10-05 **M5Dfix Pro已交付：Qwen定位异常时保全已识别全文**。同一1.7B录音ASR成功、3窗raw定位反序，旧程序拒绝整场保存；现严格核对完整字文后将坏窗保为untimed，好窗仍定位。用户原会已另存368字/3待定位，49旧文件不改；原生CPU和官方F32也仍拒绝这3窗，定位质量不冒充通过。404回归、最终13项WPF、公开Qwen CPU/Vulkan及人物/Whisper通过，806文件ZIP核验完成，版本标签裁切已补强实测。见[修复记录](docs/86_M5D_QWEN_ALIGNMENT_TEXT_PRESERVATION.md)、[完整便携ZIP](artifacts/ProjectEcho-win-x64-20261005-M5D-QwenFix-Final.zip)。Lite不改，目标Win10/clean Windows等旧缺口保持；下方为历史。

**M5D Pro已交付：** [完整便携ZIP](artifacts/ProjectEcho-win-x64-20261005-M5D-Pro-Final.zip)。M3Hfix之后至M3J的录音、Whisper异常保全文、校验提速/进度和界面修复已同步，保留Qwen、人物与人工稿。最终包396回归、13项WPF、真实Qwen CPU/Vulkan及说话人链通过；完整记录见[85](docs/85_M5D_LITE_FIXES_FORWARD_PORT.md)。解压运行ProjectEcho.exe并选择原Data，Qwen模型继续使用现有外部目录。目标Win10新版复测和clean Windows尚未完成。

M3J继续作为独立Lite版保留。下方为各版历史交付记录，其中“Pro未同步”已由上述M5D交付更新。

**M3J Lite：** [完整便携ZIP](artifacts/ProjectEcho-win-x64-20261005-M3J-Lite-Final.zip)；源码2a7f2e5，ZIP SHA-256 60dc2986a572e128610bbaee835855736dece14440cb7abf27f9b7ac9af22ae2。[详细修复与验证](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M5C仍未同步。

当前已交付 **M3J Lite**：逐段ASR时间越界保全、版本标识、会议时长与录音页录后处理。说明见[84](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M3I包保留；M5C尚未同步。

**M3I Lite下载：** [完整便携ZIP](artifacts/ProjectEcho-win-x64-20261005-M3I-Lite-Final.zip)；入口同名目录下ProjectEcho.exe。源提交98d3420，ZIP SHA-256 74af54360a02473d6fde2cc5bd0229af3ce6e1bc782395c278dccd896d2788fc。M5C未同步；目标公司电脑验收待完成。

2026-10-05：M3维护为Lite，M5及之后为Pro。当前只修M3I，M5C待用户验收后同步。见[修复记录](docs/82_M3I_LITE_CAPTURE_AND_ASR_FIX.md)与[同步清单](docs/83_M3I_TO_M5C_SYNC_CHECKLIST.md)。
Windows 本地会议音频 → 可回放合并文件 → 手动外部转写或本地转写 → 项目档案。

**历史交接：2026-10-04 · M5C 已知人数全场聚类修复**

M5C修复“已设两人仍把主要发言合为一组”的聚类方法问题；同模型/同特征改用全场球面聚类，保留自动模式旧限制。用户十条单人发言声学主组10/10，字幕9匹配/1待确认/0确定错归；另一个漏词复合句不造字。已修故意损坏模型测试触发的弹窗/挂起，并加入显式手填时间切点。详见[81](docs/81_M5C_GLOBAL_CLUSTERING_FIX.md)。

M5B补齐回听标签、人物版正文编辑和人物列表单击回听，附TitaNet/ERes模型预设。已核对用户两人样本的原生退化，局部改善及待确认明确分列；见[80](docs/80_M5B_REPLAY_EDITING_AND_VOICE_QUALITY.md)和STATUS。351回归通过，已有人工修订保留，M6未启动。

同场全场聚类、显示改名和纠错、独立版本、Whisper/Qwen词界复用与标签导出已实现。真实三小时构造压力和本机WPF通过；自动人数及叠声存在已记录失败，不宣称所有会议分组可靠。最终包与精确状态见[STATUS](STATUS.md)，使用见[76](docs/76_M5_USER_GUIDE.md)，验证见[75](docs/75_M5_MEETING_SPEAKERS.md)、[78](docs/78_M5_NATIVE_EVIDENCE.md)。M6及后续阶段未启动。下面M4及更早段落保留历史事实。

当时便携包：`artifacts/ProjectEcho-win-x64-20261004-M5C-Final.zip`，现保留为旧版。355回归、804文件中文解压及11项WPF通过。附更新原生程序、TitaNet和旧模型；外部绝对程序路径不自动更换。已知人数用新算法，自动人数旧限制和P5未测继续保留。

性能更新 **M4-AlignFix** 已交付：三小时压力轨定位约115分钟→2分钟，新增旧CPU兼容开关和可选外置官方Python。311回归、完整长测与便携验证见[74](docs/74_M4_ALIGNMENT_PERFORMANCE.md)；三分钟CPU实测仍保留原生默认。特定legacy长暂停GPU失败安全拒绝，显式CPU重试通过；具体限制及最新包见STATUS。

用户已明确授权 M4：Qwen3-ASR 0.6B CPU/Vulkan、原生声学句子定位、完整分窗和独立版本已实现并完成本机三小时压力验证与便携交付。模型外置 `H:\AI\Qwen`，不进入软件目录或 ZIP，现有 Whisper 保留。当前事实与包路径见 [STATUS](STATUS.md)，操作见 [M4 使用说明](docs/73_M4_QWEN_USER_GUIDE.md)，实测见 [71](docs/71_M4_QWEN_CHINESE_ASR.md)、[72](docs/72_M4_QUALITY_AND_WINDOW_EVIDENCE.md)。M5/M6 尚未启动；原项目分析继续作为 M7 暂缓。总计划见[70](docs/70_M4_M6_LOCAL_ASR_ROADMAP.md)。下面历史段落仅保留当时快照，不代表当前版本或新任务授权。

**实施更新：2026-09-30 · M2 + M3 验证版**

已实现可靠录音、完整WAV/RF64/M4A/MP3与限大小导出，以及M3文字导入、版本修订、本地检索、分片时间映射和可选本地whisper.cpp适配；无SQLite。UI拆为五个任务页，支持明暗主题并加入原创图标。105组默认回归、实际WPF文字流程与便携包无采集检查通过。**完整验收仍有未测项**：M2双设备同步/干净Windows/部分现场平台，M3真实模型准确率与速度。见 [STATUS.md](STATUS.md)、[本轮报告](docs/46_M3_MP3_UI_IMPLEMENTATION.md)。

便携程序位于 `artifacts/ProjectEcho-win-x64-20260930-M3/ProjectEcho.exe`，保持整个文件夹结构；使用说明见 [便携指南](docs/38_PORTABLE_GUIDE.md)。开发者可运行 `scripts/Dev.ps1 -Action Run`。界面默认不采集、mic默认关；普通录音与主动10秒试录各有入口。录音、导出、文字导入无需模型或API密钥。

用户可打开 `START_HERE.html`；Codex先读 `AGENTS.md`、`docs/00_HANDOFF.md`、`STATUS.md`，按当前阶段继续验收，勿重新把历史M0规划当作未实现状态。

## 已确认需求
Zoom / Webex 桌面版和网页版；独立本地麦克风与会议声音；合并音轨保存；本地处理不好时可手动上传外部语音模型；会后按项目分析；Windows 便携、尽量免管理员权限。

## 优先级
M0 可行性 → M1 可靠录音 → M2 完整混音、手动导出与便携包 → M3 本地转写及已记录的修复。下一步以 M3Hfix 完成交付为基线，用户明确启动后依次实施 M4 中文 ASR/句子定位、M5 同场说话人、M6 可选深度复核，无需额外通用文本 LLM。M7 项目分析暂缓；M8 单标签页可选；M9 扩大发行回归可独立补验。

不要把 M2 的合并/分段导出排到本地大模型之后。没有安装任何模型，也必须能录音、回听、导出。

默认未分类、日期命名，录后再整理；每场确认实际来源与 mic。停止后生成完整混音，主要操作为回听和手动导出。WAV + 至少一种已验证压缩格式仍是交付要求。M3Hfix新录音暂停停采且不计录音时长，旧录音维持原时间轴；M3C已采用可重建SQLite索引。复杂剪辑、音量编辑和术语管理延后；独立分轨、真正停采、尾音完整、崩溃恢复、长时同步测试与导出范围保护继续保留，验收状态见STATUS。

## 文件说明
- `docs/`：产品、架构、录音、浏览器、导出、隐私、部署、验收和来源。
- `prompts/`：分阶段 Codex 提示词，以及用户手动交给外部语音模型的提示词。
- `schemas/`、`examples/`：数据契约与合成示例；不是实测记录。
- `scripts/`：环境探测、构建入口、独立会议浏览器启动与初始包校验；各脚本的执行证据与限制见 `STATUS.md`，有脚本不代表有产品。
- `tests/`：验收矩阵、数值用例与人工测试指引。
- `reference/`：C# 接口草案与导出算法规格，不是已实现引擎。

除文档内明确注明的官方事实外，采样率、阈值、界面和里程碑都是本项目设计决定，不是对实际效果的保证。


