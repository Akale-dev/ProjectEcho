当前交付：[M3O Lite 完整便携 ZIP](../artifacts/ProjectEcho-win-x64-20261006-M3O-Lite-Final.zip)，[修正与验证](docs/93_M3O_PLAYBACK_BUTTON_POLISH.md)。M3K–M3O 已按用户明确请求同步为 M5E Pro，保留 M5Dfix Qwen 修复。下方为历史说明。

# ProjectEcho｜项目会议记录器

2026-10-06 **M3N Lite已交付，待用户验收后一起同步M5 Pro**：修复mic低底噪/DC不断重置无声计时；录音/暂停中可启用、取消自动结束及改时长，Host确认后生效并留证。回听新增−10/−5/+5/+10秒矢量按钮、上下切字幕及左右5秒快捷键，跳转保留暂停/播放和倍速；两栏从头播放改为左竖线+右三角，焦点/按下不裁切。378/0回归、18/18包内WPF、四进程启动及672文件中文ZIP通过。1681个Pro源码/旧包文件不变，32文件增量补丁及同步记录已保存。见[交付与验收记录](docs/92_M3N_MIC_SILENCE_AND_PLAYBACK_CONTROLS.md)、[完整便携包](../artifacts/ProjectEcho-win-x64-20261006-M3N-Lite-Final.zip)。真实mic/会议、目标机、物理键鼠/DPI和clean Windows待验；无新采集、下载、上传或关机。下方保留历史记录。

2026-10-06 **M3M Lite已交付，待用户验收后一起同步M5 Pro**：移除手动预检，开始录音自动检查保留；原按钮位置增加自动结束勾选，默认关闭/5分钟，可选1、5、10分钟，所选根进程退出或全部已启用来源无声音到期后正常停止并保存。mic声音重置，暂停不计；缺包不冒充静音，尾包与结束原因持久保留。362/0回归、16项包内WPF覆盖（含剪贴板权限复验）、四进程启动及671文件中文ZIP核验通过。1680个Pro源码/旧包文件hash保持，23文件增量补丁和同步清单已留存。见[交付与验收记录](docs/91_M3M_AUTOMATIC_RECORDING_END.md)、[完整便携包](../artifacts/ProjectEcho-win-x64-20261006-M3M-Lite-Final.zip)。真实会议/麦克风/目标机及旧闸门仍待验；本次未新增采集、下载、上传或关机。下方保留历史记录。

**M3J Lite：** [完整便携ZIP](../artifacts/ProjectEcho-win-x64-20261005-M3J-Lite-Final.zip)；源码2a7f2e5，ZIP SHA-256 60dc2986a572e128610bbaee835855736dece14440cb7abf27f9b7ac9af22ae2。[详细修复与验证](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M5C仍未同步。

当前已交付 **M3J Lite**：逐段ASR时间越界保全、版本标识、会议时长与录音页录后处理。说明见[84](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M3I包保留；M5C尚未同步。

**M3I Lite下载：** [完整便携ZIP](../artifacts/ProjectEcho-win-x64-20261005-M3I-Lite-Final.zip)；入口同名目录下ProjectEcho.exe。源提交98d3420，ZIP SHA-256 74af54360a02473d6fde2cc5bd0229af3ce6e1bc782395c278dccd896d2788fc。M5C未同步；目标公司电脑验收待完成。

当前为长期维护的 **M3I Lite** 工作树，基于M3Hfix。修复与测试见[82](docs/82_M3I_LITE_CAPTURE_AND_ASR_FIX.md)，验收后同步Pro见[83](docs/83_M3I_TO_M5C_SYNC_CHECKLIST.md)。M5C在主目录保留，本次未改。
Windows 本地会议音频 → 可回放合并文件 → 手动外部转写或本地转写 → 项目档案。

**实施更新：2026-09-30 · M2 + M3 验证版**

已实现可靠录音、完整WAV/RF64/M4A/MP3与限大小导出，以及M3文字导入、版本修订、本地检索、分片时间映射和可选本地whisper.cpp适配；无SQLite。UI拆为五个任务页，支持明暗主题并加入原创图标。105组默认回归、实际WPF文字流程与便携包无采集检查通过。**完整验收仍有未测项**：M2双设备同步/干净Windows/部分现场平台，M3真实模型准确率与速度。见 [STATUS.md](STATUS.md)、[本轮报告](docs/46_M3_MP3_UI_IMPLEMENTATION.md)。

便携程序位于 `artifacts/ProjectEcho-win-x64-20260930-M3/ProjectEcho.exe`，保持整个文件夹结构；使用说明见 [便携指南](docs/38_PORTABLE_GUIDE.md)。开发者可运行 `scripts/Dev.ps1 -Action Run`。界面默认不采集、mic默认关；普通录音与主动10秒试录各有入口。录音、导出、文字导入无需模型或API密钥。

用户可打开 `START_HERE.html`；Codex先读 `AGENTS.md`、`docs/00_HANDOFF.md`、`STATUS.md`，按当前阶段继续验收，勿重新把历史M0规划当作未实现状态。

## 已确认需求
Zoom / Webex 桌面版和网页版；独立本地麦克风与会议声音；合并音轨保存；本地处理不好时可手动上传外部语音模型；会后按项目分析；Windows 便携、尽量免管理员权限。

## 优先级
M0 可行性 → M1 可靠录音 → M2 完整混音、上传导出与便携包（首个可用版本）。M3 本地转写、M4 项目分析、M5 可选单标签页扩展均为后续；M6 扩大发行回归，无需等 M3–M5 完成。

不要把 M2 的合并/分段导出排到本地大模型之后。没有安装任何模型，也必须能录音、回听、导出。

首版默认未分类、日期命名，录后再整理；每场确认实际来源与 mic，试录由用户主动选择。停止后自动生成完整混音，主要操作为回听和导出供转写。WAV + 至少一种已验证压缩格式即可交付，暂停保留时长并填静音。复杂剪辑、音量编辑、术语管理与数据库延后；独立分轨、真正停采、尾音完整、崩溃恢复、两小时同步测试与导出范围保护继续保留。

## 文件说明
- `docs/`：产品、架构、录音、浏览器、导出、隐私、部署、验收和来源。
- `prompts/`：分阶段 Codex 提示词，以及用户手动交给外部语音模型的提示词。
- `schemas/`、`examples/`：数据契约与合成示例；不是实测记录。
- `scripts/`：环境探测、构建入口、独立会议浏览器启动与初始包校验；各脚本的执行证据与限制见 `STATUS.md`，有脚本不代表有产品。
- `tests/`：验收矩阵、数值用例与人工测试指引。
- `reference/`：C# 接口草案与导出算法规格，不是已实现引擎。

除文档内明确注明的官方事实外，采样率、阈值、界面和里程碑都是本项目设计决定，不是对实际效果的保证。


