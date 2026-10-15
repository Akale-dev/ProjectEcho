# M3P Lite：字幕时间戳异常高亮与定位

2026-10-15。基于实际 M3O Lite（`d91c26a511e8e03e5a3b76263b4e2efefd0336ec`）。本次只更新 Lite；待用户验收后按已有授权同步 M5 Pro。当前 Pro 为 M5E，必须保留 M5Dfix 的 Qwen 全文保全、待定位窗口及人物功能。

## 使用与判定

- “字幕回放与校订”列表中的时间异常字幕显示浅红色背景（`#FFFFE1E1`），选中时仍保持浅红；悬停说明原因。输入时间后立即重新判断，改好即恢复正常颜色。
- “寻找错误”紧靠“导出字幕”左边。按当前完整版本的字幕顺序寻找选中项之后的下一条错误；到末尾后回到第一条，只有一个错误时重新选中它。没有错误时提示。
- 含时间异常或尚未保存修改时，“导出字幕”仍可点击。先检查完整当前版本及其草稿，若有时间错误则选中并滚动到第一条错误，弹出修正提示；修正后先“保存为新修订”，再导出 SRT/VTT。时间有效但草稿未保存时提示先保存，不导出旧内容。
- 搜索结果可能隐藏异常条目。寻找错误或导出发现错误时会展开对应完整版本；跨会议搜索时切到字幕所属会议，原有各版本草稿保留。此操作不自动播放声音。
- 检查范围：缺少起止时间、输入不完整或不可解析、开始为负、结束不晚于开始、超过版本时长，以及超过已校验完整回听音频的时长。由旧 ASR 保全为 `untimed` 的文字会显示异常并可人工补时间。
- 正常重叠、重复发言及可导出的非时间排序不被误报。不推测字幕文字实际应对应哪一句声音，不自动改写或猜测时间；数值有效但与声音语义错位仍需回听校对。
- 文字错误与时间错误分开判断。全文 TXT 仍可保全无时间文字；原稿、旧修订、音频与录音回放锁定保持原有合同。

## 本轮实现与测试入口

实现：`TranscriptRevisionDraft` 统一提供时间诊断及循环查找；`MainWindow.TranscriptTiming.cs` 管理跨搜索/版本的错误选择；`MainWindow.Transcription.cs` 接入导出前检查；XAML 使用虚拟化条目模板进行浅红高亮，并将寻找/导出按钮成组换行。新增简体/繁体/英文文案。

证据根：`lite/.local/M3P/`。环境为 Windows 11 25H2 **26200.9711/x64**，使用项目锁定 SDK 和已有依赖缓存，无新增下载。

- `source-before.json`、`pre-change/`：466 个 Lite 已跟踪源文件的 M3O 前像。
- `pro-before.json`：452 个 Pro 产品、脚本与测试文件的前像哈希。
- `old-releases-before.json`：140 个旧版归档/校验/元数据文件的前像哈希。
- `build-*.log`：实际 Release 构建。
- `regression-*/results.json`：原有完整回归及新增 6 项时间诊断/导出回归。
- `ui-*/`：实际 WPF、真实路由点击及属性绑定、三语言/标准与最小窗口、选中高亮、修正消色、跨会议搜索、5000 条虚拟化列表定位到第 4801 条、保存新稿与 SRT/VTT 导出。测试拦截对话框消息以便自动运行；原生弹窗和物理鼠标确认独立待用户验收。
- 长列表为合成字幕压力检查，单元回归另含 10000 条/三小时时轴；没有运行自然长会议识别，不能据此宣称 ASR 对齐质量已修复。

最终包、完整结果、差异补丁和同步清单见下方。旧包、私人 DEBUG、用户会议和模型不改动；没有录音、麦克风、上传、下载、推送或关机。目标公司机、物理输入/OS DPI、clean Windows 及原有 M1/M2 闸门未升级。

## 最终交付与核验结果

- 完整便携包：[M3P Lite Final](../../artifacts/ProjectEcho-win-x64-20261015-M3P-Lite-Final.zip)，**262804580 字节**。SHA-256：`8184e45d027b8f9c4c4061c63cab502405b49c44ec840ecd8a426de43fed20bf`。
- 同名解压目录入口 `ProjectEcho.exe`。新目录第一次启动需要主动选择原数据目录；同目录更新应保留 `settings/data-root.json` 和原数据目录。
- Release 构建 **0 警告/0 错误**。SDK **10.0.401**、runtime **10.0.12**、NAudio **3.1.0**。
- 完整自动回归 **384 通过/0 失败**（原 378 + 新增 6）：`regression-01/results.json`。最终包的 Core/Capture/Export 与这些回归所测 DLL 哈希一致：`tested-components-final.json`。
- 最终中文解压包实际 WPF **19 个入口：18 通过，1 个旧布局检查失败**。新增 M3P 检查通过；窄窗口三语言截图已实际查看。汇总 `bundled-ui-summary.json`，全部初次运行与后续复验分别保存。
- 失败项为旧录音页英文 1080 布局：当前桌面请求 1920×1000，实际窗口 1920×887，录后处理卡未完全落在测试要求的初始可视区内。原 **M3O 同一检查、同一环境出现完全相同失败**；源码和阈值未改，也不记为通过。证据 `bundled-ui-final/`、`m3o-baseline-layout/`。
- 首轮旧字幕复制检查失败，普通用户剪贴板权限下同一最终 EXE 单独复验通过；失败和复验分别保留在 `bundled-ui-remaining/`、`bundled-clipboard-retry/`。
- **674 个 ZIP 文件**逐项长度/SHA 核验、中文路径解压、App/Host 自包含空闲启动及 IPC 通过：`zip-verification-final.json`。已有 Whisper CPU/Vulkan 程序和 base 模型与旧发行一致，没有新增下载。
- 目录记忆、启动自动刷新、目录不可用不回退及恢复，**4 个独立进程**通过：`bundled-startup-final/report.json`。
- **452 个 Pro 产品/脚本/测试文件 + 140 个旧发行归档/校验/元数据文件**哈希全部保持：`preservation-final.json`。

包内说明是封包前快照。本文件、根目录 `docs/95_M3P_LITE_PENDING_SYNC.md` 和包旁 `.package.json` 补记封包后的最终结果，不重写已核验 ZIP。

## 验收后同步记录

`lite/.local/M3P/pre-change/` 为 M3O 前像，`final-source/` 为 M3P 完整源码快照；`source-before.json`、`source-final.json`、`changes-final.json` 逐文件记录 SHA-256。`M3O-to-M3P.patch` 为可在该 M3O 前像应用的增量补丁，`pro-sync-inventory.json` 保存补丁哈希及检查结果。

当前 `USER_ACCEPTANCE_PENDING / PRO_NOT_APPLIED`。后续按[Pro 迁移清单](95_M3P_LITE_PENDING_SYNC.md)合并，保留 M5Dfix Qwen 与人物功能；不可整文件覆盖 Pro。用户验收后按已有授权同步，无需第二次同步批准。

## 用户验收顺序

1. 解压完整 M3P Lite，运行 `ProjectEcho.exe`，选择原数据目录。
2. 打开已知含待定位条目的长会议字幕，检查浅红标识；点击“寻找错误”，确认逐条选择、滚动及循环。
3. 选择任意一条后点击“导出字幕”，确认提示并跳回第一条异常。
4. 手工修正时间，确认颜色恢复；保存为新修订，再导出字幕；重开旧版本，确认旧稿仍保留。
5. 验收后同步至 M5。同步时按增量迁移，禁止以 Lite 整文件覆盖 Pro 的人物/Qwen 分支。
