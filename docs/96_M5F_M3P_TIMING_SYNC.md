# M5F Pro：同步 M3P 字幕时间戳检查

2026-10-23。用户明确要求将 M3P 功能同步到 M5，此指令完成本批次的验收后同步授权。本次从实际 M5E Pro（`4b72fa624d4ff54fe6f8a8fcb720300290711dbd`，包含交接文档）增量合入已发布 M3P Lite（`3aeea23aaee7f6cc1186b125d986868bfdcd7215`），本地工作分支 `codex/m5f-pro`。

## 已实现

- “字幕回放与校订”中，起止时间缺失、格式不完整、反序/零长、超过版本或已校验音频时长的字幕显示浅红色；选中时仍可见，改好后即时恢复。
- “导出字幕”不再因时间错误或未保存草稿直接变灰。点击后检查完整当前版本及草稿；有时间问题时提示修正并选中第一条错误，时间有效但尚未保存时提醒先保存新修订。
- 导出左侧增加“寻找错误”，从当前字幕之后循环寻找下一条异常；搜索隐藏的错误可展开至完整版本，跨会议结果切到所属会议，保留其他草稿。
- 普通字幕、Qwen 待定位文字和人物版字幕共用检查与定位；正常字幕重叠不被误报。数值有效但与声音语义不符的时间仍需人工回听，未增加自动对齐推断。
- 人物版继续通过 `SpeakerService.EditSubtitles` 保存新人物修订，保留人物 ID、显示名、原稿/父稿/分析/音频绑定。SRT/VTT 的可选人物标签保留，未改成普通稿保存或导出。
- Qwen 的全文保存、待定位提示、文字/对齐完整性分离保留。无时间文字可全文导出，不能静默丢弃后生成字幕；旧定位质量限制不升级。

## 验证与证据

证据根 `.local/M5F/`。本机 Windows 11 25H2 **26200.9711/x64**；源构建、最终包回归、实际 WPF 和便携验证结果完成后追加。所有新增输入为合成文件，无录音、麦克风或私人会议重处理。

- `pre-change/`、`source-before.json`：584 个 Pro 源文件的完整前像。
- `lite-before.json`：475 个 Lite 已跟踪文件的哈希；此次不改 Lite 产品代码。
- `old-releases-before.json`：145 个旧发行归档/校验/元数据文件的哈希。
- `build-*.log`、`regression-*/results.json`：构建及完整自动回归，包含 M3P 的 6 组与 Pro 人物版的 2 组新增测试。
- `ui-*/`、`bundled-ui-*/`：实际 WPF 普通/人物/Qwen 场景，错误提示和定位、颜色/虚拟化/窄窗口、保留草稿、另存修订、标签导出和原件哈希。
- WPF 测试通过路由点击及测试回调检查提示文案；未宣称已进行原生弹窗、物理键鼠或 OS DPI 验收。

首轮新增人物 WPF 测试将显示姓名填入了内部 A/B 代号字段，验证器正确拒绝。已修正测试为先创建 A/B，再调用正式改名流程，保留失败证据；没有放宽产品验证规则。

## 最终结果

| 项目 | 实际结果 | 证据（`.local/M5F/` 下） |
|---|---|---|
| 构建 | Release 零警告、零错误；SDK 10.0.401、runtime 10.0.12、NAudio 3.1.0 | `build-01.log`、`build-02.log`、`test-build-02.log` |
| 自动回归 | **473 项覆盖通过**：完整第二轮 470 通过/3 失败，3 个失败用哈希相同的原测试与生产 DLL 定点复验全部通过 | `regression-coverage-final.json`、`regression-02/results.json`、`regression-targeted-03/results.json` |
| 最终包 WPF | **25 个入口，23 通过、2 个既有布局检查失败**；新增普通/人物时间检查及 Qwen 待定位检查通过 | `bundled-ui-summary.json`、`bundled-ui-final/`、`bundled-ui-retry/` |
| 布局对照 | 旧 M5E 在同环境下出现完全相同的两项失败：校订列表实际 216 DIP 未达旧检查 240 DIP；录音页请求 1920×1000 实际 1920×887，初始可视区检查失败 | `baseline-layout/` |
| 同包复验 | 人物界面一次 55 秒超时后同 EXE 通过；复制字幕两次失败后独立复验通过；原失败不删除 | `bundled-ui-timeout.json`、`clipboard-isolated/`、`baseline-clipboard/` |
| 真实 Qwen | 既有 0.6B Q8_0 与 ForcedAligner Q8_0：公开短中文 CPU、合成中英 Vulkan0 通过 | `real-ui/runs.json` |
| 真实人物/Whisper | 既有公开四人样本：人物分析、Whisper、结果绑定/另存/编辑/重开流程通过 | `real-ui/runs.json` |
| 自包含与启动 | ZIP **808 个文件**逐项长度/SHA、中文路径、App/Host 无外部 runtime 空闲握手通过；记忆目录/自动刷新/不可用不回退的四独立进程通过 | `zip-verification-final.json`、`bundled-startup-final/report.json` |
| 已测与交付相同 | Core/Capture/Export 与回归使用的 DLL 一致；最终源码再次发布得到相同 App DLL | `tested-components-final.json`、`repro-publish.json` |
| 保留 | **31** 个 Qwen/人物/原生实现文件、**475** 个 Lite 跟踪文件、**145** 个旧发行文件、**5** 个只读模型/公开输入均保持 SHA | `preservation-final.json` |

首轮完整回归为 466 通过/7 失败，保留于 `regression-01/`：包含 sandbox junction/IPC 权限、临时 `.pending-*` 文件访问失败，以及新增测试误用嵌套集合的引用相等比较。后者已改为原文件哈希与字段比较，未改产品逻辑。第二轮的三个失败均可在原尝试文件中查到保存时 `Access denied`，其中一个旧断言只报告了“有效时间被延长”的概括错误；没有据此误改 Whisper 时间规则。定点复验不跳过任何原断言，不把初次失败改写为通过。

最终包截图已查看，浅红色在选中时保持可见，人物显示名和标签开关仍在，窄窗口寻找/导出按钮相邻。两项布局限制保留，不通过降低阈值或修改旧录音页掩盖。短公开模型检查不等于自然长会议、私有 1.7B 例或自动人数/对齐质量验收通过。

## 交付与同步状态

[M5F Pro 完整便携 ZIP](../artifacts/ProjectEcho-win-x64-20261023-M5F-Pro-Final.zip)，**462109080 字节**，SHA-256：`b1d769f7252df669b5561101252a72ce9777c2fb84dd90a2dc9a475f184ecbad`。

状态：**IMPLEMENTED / WINDOWS_LOCAL_VALIDATED_WITH_RETAINED_LIMITATIONS / M3P_SYNC_APPLIED**。包内说明是封包前快照；本文件及同名 `.package.json` 补记最终结果，不重写已核验 ZIP。

M5E 前像、最终源码、逐文件差异与增量补丁保存在 `.local/M5F/`，同步记录见 `sync-inventory.json`。M3P 的 Lite 源码和已发布 ZIP 保持；后续维护不得再次把 M3P 当作尚未迁移功能。GitHub 中的历史 M3P 发布回执不改写，本次没有远端发布。

## 使用与边界

完整解压新包并运行 `ProjectEcho.exe`，首次在新目录选择原数据目录。打开原会议和所需字幕版本，使用“寻找错误”，人工修正后保存为新修订再导出。旧包、旧稿、音频及现有模型配置保留。

真实会议/麦克风、目标公司机、干净 Windows、物理输入/DPI及原有 M1/M2/M4/M5 质量缺口仍单列。本次是本地 M5 功能同步；2026-10-15 的公开发布授权限定 M3P 与 M5 交接文档，不自动扩大为新 M5 产品包的远端发布。未推送或上传本次 M5F，也未下载模型、启动 M6 或关机。
