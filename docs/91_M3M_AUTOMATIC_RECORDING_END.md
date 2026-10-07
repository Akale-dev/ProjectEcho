# M3M Lite：会议录音自动结束

日期：2026-10-06。基线为实际交付的 M3L Lite。仅维护 Lite 产品代码；旧发行包、原始数据和 Pro 产品源码保留。用户要求在本次 Lite 验收后，将相关变更一起同步到 M5 Pro；已有同步授权，无需再次询问。

## 使用与行为

- 移除录音栏的手动“预检”按钮。“开始录音”仍自动验证本场来源、设备、权限/能力和保存位置；CaptureHost 仍检查实际协商格式及磁盘预算，失败不能开始采集，也不扩大声音范围。
- 原按钮位置改为“自动结束会议录音”勾选框，默认关闭。旁边“无声音”下拉框仅有 1、5、10 分钟，默认 5 分钟。录音、暂停、收尾期间锁定本场配置。
- 勾选后，两个条件任选其一即可结束：从特定应用录音时，**选中的根进程退出**；或所有已启用录音来源连续无声音达到所选时长。从所有应用录音时仅使用无声音条件。关掉会议窗口、某个浏览器标签或音频子进程，不一定等于根进程退出。
- 已启用的独立麦克风声音也重置计时。未开启的麦克风不会为检测而打开或采集。声音活动沿用录音健康显示的保守峰值阈值 `>0.0001`（约 −80 dBFS），背景噪声也可能阻止自动结束；这不是语音识别或说话人判定。
- 从实际来源启动后开始计时。暂停期间不累计，恢复后重新给予完整时长；麦克风开关确认后也重新计时，准备开启麦克风期间不触发无声音结束。暂停期间若所选根进程退出，仍结束并保存。
- 有些系统音频流在没有声音时不回调，因此**没有音频包也计入无声音时长**。记录与界面仍区别“明确静音包”和“没有音频包”；从未收到有效包、来源故障或时钟/写盘问题，不会因自动结束而变成完整录音。
- 不剪掉末尾这段无声音，也不修改原 PCM 或时钟。自动结束沿用同一个 Stop 流程：停采 → 保存有效边界前尾包 → 排空 writer → CaptureHost 退出确认 → 显示保存结果 → 原有后台混音。手动停止先被接受时优先，不能重复保存。
- 控制逻辑运行于 CaptureHost，切换界面或最小化不影响判定。进程身份通过固定的原始 Windows 进程句柄监测，核对创建时间与可执行路径，不依靠单独 PID；身份不可确认是检查/采集错误，不能谎称正常退出。

## 可验收记录

每场的 `selection.json` 增加本场 `autoStop` 配置；`journal/events.jsonl` 追加一次 `auto_stop` 决策，保留原因、所选分钟数、录音时间及远端/麦克风缺包状态；原 `stop` 与来源边界记录照常保留。`recording-result.json` 保存同一 `AutoStop` 结果。正常 UI 的会议 Log 和停止/保存状态也说明结束原因。

原因代码：`source_process_exited`（选定根进程已退出）；`no_audio_activity`（达到所选无声音时长）。这两个代码不是录音完整性判定。旧会议文件无需迁移，未传新选项的旧 IPC/调用仍默认关闭。

## 实现与验证记录

证据位于 `lite/.local/M3M/`。本轮没有实际音频采集、麦克风启用、私人音频读取、上传、下载、推送或关机。合成源直接驱动实际 RecordingSession 采集线程和磁盘 writer，自建无音频进程用于 Windows 句柄退出检测；不可把这些记为真实 Zoom/Webex 会议验收。

本机 Windows 11 25H2 **26200.9457/x64**；SDK **10.0.401**、runtime **10.0.12**、NAudio **3.1.0**。`build-final-02.log` 为 Release 零警告、零错误。`regression-final-02/results.json` **362 项全部通过**，含 19 项新增自动结束测试。

新增覆盖 1/5/10 分钟精确阈值、短暂声音、麦克风讲话/关闭、长暂停恢复、根进程退出、子进程身份错误、无包不能冒充静音、手动/自动停止竞争、实际 writer 收尾和尾包保全。额外验证跨过到期时刻的包：已发生的有声前缀能延后结束，尚未发生的未来声音不能延后结束。Windows 自建无音频进程实测固定句柄的存活/退出及创建时间不匹配拒绝；不等于真实会议采集验证。

`ui-final/runs.json` 五项实际 WPF 入口通过：M3M 三语与正常/最小窗口布局、开始自动检查、选项锁定、收尾等待/迟到健康回报，以及旧紧凑布局、无采集校验、暂停时间、M3L 编辑与启动库。下拉框三语 10 分钟均单行；截图人工查看通过。

过程记录保留：`build-01` 的受限并行构建未成功，`build-02` 暴露新测试局部断言函数缺失，修复后零错误；`regression-01` 为 354 通过/5 失败，五项旧目录链接/本地 IPC 测试被沙箱拒绝。项目范围内的无采集复验 `regression-final` 为 361/0；最后的最小进程权限改进和自动收尾闸门测试完成后，以 **362/0 的 final-02** 为最终程序集结果。没有删除失败日志或把它们写成首轮全过。

完整便携包与包内复验在封包后补记。既有 M1/M2 现场录音、长期声学同步、目标公司机、真实 OS DPI/物理键鼠和 clean Windows 等闸门不因本功能而升级。

## 验收后同步到 M5 Pro

待用户确认本次 Lite 验收后，连同 M3K、M3K-TimeFix、M3L 尚未迁移部分一并迁入 Pro。历史 M5D/M5Dfix 已完成的同步与 Qwen 修复保留。

迁移清单：

1. 录音栏替换入口、三语资源、冻结配置和新版标签；保留 Pro 的 Qwen、人物、人工稿和各页面。
2. `RecordingAutoStopOptions/Info/Monitor`、固定根进程句柄、`RecordingOptions/Result` 和 `HostEvent` 可选字段。
3. RecordingSession 启动、计时、麦克风/暂停边界、到期前有效 PCM 检测、单次停止与 writer 决策记录；不覆盖 Pro 已有采集修复。
4. CaptureHost 的 finalizing 通知、UI 的原因/保存等待状态、迟到健康报告不能覆盖收尾状态。
5. 新增合成/Windows 进程测试、M3M WPF 验证及便携包清单；重跑 Pro 自身回归，不能只拿 Lite 结果作 Pro 通过证据。

不要整文件覆盖 Pro。保留本次源码差异、测试日志、失败记录、旧包和哈希，再在 Pro 基线上逐项迁移。

进程观察仅请求 `SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION`，不申请写入、终止或全部权限。创建时间和路径都从固定句柄读取。Windows API 合同核对：[进程访问权限](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)、[GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)、[QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)。

## 用户验收建议

由用户主动选择来源并开始录音：不勾选确认仍可手动结束；勾选 1 分钟验证无声音结束与短暂讲话重置；分别验证所选应用退出、所有应用模式、暂停/恢复，以及用户主动开启麦克风后的本地讲话。确认会议 Log 结束原因、原轨/混音尾部和录音时长。5/10 分钟可按实际需要复验。

## 最终交付（2026-10-06）

完整便携包：`artifacts/ProjectEcho-win-x64-20261006-M3M-Lite-Final.zip`，**262769344 字节**，SHA-256 **`a41fd29626f709e544538f9e3dbff99b386e3c47eb53c5fa3510c65879ba757d`**。同名目录入口 `ProjectEcho.exe`。升级时保留原数据及 `settings/data-root.json`，或在新程序中明确选择原数据目录。

670 项清单加 manifest 自身共 **671 个文件**，中文解压逐项 hash、自带 App/CaptureHost 运行时及无采集 IPC 通过；CPU/Vulkan/base 和默认 ASR 配置与原已验资产逐项一致，包内无私人 Data、音频、日志或本机设置。见 `zip-verification-final.json`。初次封包在沙箱并行 restore 未成功，候选保留于 `preliminary-release/`；改用单节点、现有本地 NuGet 缓存后封包成功，没有新增下载。

最终中文解压包 **16 项 WPF 覆盖通过**：首轮 7 通过，旧 M3C 字幕测试读取剪贴板被沙箱拒绝；保留失败记录后，同一包在所需本机权限下复验该项及后续 8 项，9 项全部通过。该测试只暂存/恢复剪贴板并静音播放合成音频；未开启采集。汇总 `bundled-ui-summary.json`，明细 `bundled-ui-final/runs.json` 与 `bundled-ui-retry/runs.json`。不是首轮 16/0。

`bundled-startup-final/report.json` 的四个真实独立 App 进程验证记忆目录、自动刷新、目录不可用不回退及恢复通过，合成会议元数据不改。`tested-components.json` 证明最终包 App/Host 的六份 Core/Capture/Export DLL 与 **362/0** 回归所用文件逐项相同。

`preservation-verified.json` 核对 **1680 个 Pro 源码/旧发行包文件**保持原 hash。M3L→M3M 的 **23 个源码文件**增量补丁保存在 `m3m-incremental.patch`，已对重建的本轮开始时 M3L 源码前像执行 apply-check；补丁 SHA-256 `d550c78c2a3620087737ef291612e7dfcd7ae50fa37b56dba9fb7bab8c02c313`。迁移清单与逐文件 hash 在 `pro-sync-inventory.json`。原 preexisting diff、失败日志、最终源代码复审记录均保留。状态与验收表同步更新；包内说明为封包前快照，本节及包旁 `.package.json` 补记核验结果，不改已核验 ZIP。

状态：**IMPLEMENTED / WINDOWS_LOCAL_VALIDATED / USER_ACCEPTANCE_PENDING / PRO_NOT_APPLIED**。实际会议的进程退出/静音、真实麦克风及目标公司机行为仍待用户主动验收；旧 M1/M2、物理 DPI/键鼠和 clean Windows 闸门保持原状态。验收后按已有授权连同待迁移 Lite 改动同步到 M5 Pro。
