# 86｜M5Dfix Pro：Qwen定位异常时保留已识别全文

2026-10-05。用户反馈本机新录音Whisper成功、Qwen失败，要求检查尝试修复。本轮仅处理Qwen会后文字/定位失败，不改变录音、音频块、暂停时间轴或说话人原生模型；Lite不动。不录音、不采集麦克风、不播放私人音频、不上传。已有模型仅从用户提供的`H:\AI\Qwen`只读使用。

## 已确认根因

这场音频约74.393秒，Qwen使用**1.7B Q8_0 / Vulkan0**，分3个396764帧（24.79775秒）窗口。Whisper成功和Qwen失败所用16k音频SHA完全相同（`865be56a…`，见`provider-input-comparison.json`）。ASR进程退出0、全部文字已输出；ForcedAligner同样退出0、3窗完成，但raw声学时间存在实际错误：

- 第1窗第9个词：开始69120帧（4.32秒）、结束57600帧（3.60秒），结束早于开始。
- 第2窗同时有反序，末词结束396800帧、超过本窗36帧（2.25ms）。
- 第3窗也有反序，包括371200→364800帧。

旧Qwen严格映射在首个错误处拒绝发布整场版本，结果`Failed`、`revisionId=null`。原文实际保存在attempt的三个TXT中，但无法作为普通转写版本使用。因此这里要区分模型定位错误和应用未保存可用全文两个问题。Whisper没有走这个ForcedAligner流程，成功不意味着Qwen定位输出一定合法；这也不是Whisper旧250ms末尾容差问题。

CPU对照保留同一音频、文字、模型和4线程，用随包AVX2程序实际重跑3窗：**exit0、17.753秒、3窗均仍被生产严格映射拒绝**。CPU异常条数2/5/3，Vulkan为2/6/3；主要反序与36帧尾端越界仍在。不能宣称改CPU或换GPU驱动可解决。证据`.local/M5DQwenFix/cpu-alignment-01/summary.json`及前后hash；报告不含正文。

M5D交付时实际Qwen验证为0.6B与公开/合成短样本，本次是新的1.7B用户录音，不把此前通过扩张成所有音频/模型定位可靠。

## 修复边界

按窗口保全原文：只有全部词文字、JSON结构、输入及原文hash完整通过，而时间预测出现明确反序/越界等异常，才将该窗口全文另存为`untimed`。其他正常窗口继续保留原来的声学定位。严格映射接口继续拒绝坏时码，不插值、不裁到EOF、不放宽容差、不借Whisper时间、不按字长造时间，也不自动切换CPU。

文字覆盖与定位覆盖分别记录；待定位窗口不计为已定位。新证据策略区分旧完全定位版本，并显式保存各窗口对应的segmentId，防止前一窗口无时码导致后续人物词界错位。坏窗口不产生自动人物词界。原音频、旧Whisper稿、失败attempt及人工稿保持，重跑另存新版本。

界面明确显示“文字已保存，有N段待定位”，重开仍可见；待定位段不能按句跳播，全文TXT可用。SRT/VTT继续拒绝包含无时码的版本，避免默默漏文字。缺词、错误文本、坏JSON/hash/协议、进程非零退出或取消不能冒充成功。

## 进度与证据

实现已完成，Release构建0警告0错误，**404项回归通过**（396原项＋8组新增），候选包相同生产组件也已404项复验。新增测试覆盖前坏后好ID/词界、晚发字文损坏、全部无时码持久/搜索/校订/导出、证据篡改、旧v1兼容和各类时间错误/诊断上限。独立Core复审无阻断项。

旧M5D在私人副本完整重跑**12533ms**，同样失败且没有发布版本；修复后相同1.7B/Vulkan副本重跑**13662ms**，**368字全部保留、3段untimed**，TXT及重读通过，SRT拒绝、不产生人物词界。随后使用最终待验包的Core在用户原会议另存新版本，**23228ms**；当时并行合成回归，不将耗时差异作为速度比较。新版本ID与路径只保存在ignored本地报告，原会议49个既有文件全部hash保持。证据`baseline-full-01/summary.json`、`fixed-full-01/summary.json`、`original-final-01/summary.json`，均在`.local/M5DQwenFix/`。

这意味着应用的全文保存/呈现问题已修复，**该场定位质量仍未通过**，不能把3段待定位视为完整可回听字幕。现有Whisper版本保持可用；文字准确率未由本次验证。

修改前的381份实现输入保存在`.local/M5DQwenFix/pre-fix/`，源码与用户会议49个既有文件SHA分别记在`source-before.json`、`meeting-before.json`。既有M5D ZIP不重写，最终M5Dfix Pro包后验证在下节追加。

## 视觉检查发现及修正

首个候选的13项WPF自动检查通过，但人工查看实际截图发现侧栏`Ver. M5Dfix Pro`被裁掉末尾。原布局测试只查元素边框位置，未验证文本实际需要宽度，因此不能把该自动PASS当作视觉无裁切。保留候选包为`M5D-QwenFix-Candidate-01`和`rejected-candidate-01.json`（SHA `84b82cfb…`）。侧栏标签缩短为`M5Dfix Pro`，日志/程序集版本保持；侧栏246和字号不改，增加实际文字宽度及三语言布局检查。原候选的转写核心与数据验证不受此显示修正影响。

第二候选的新检查检出版本控件仍继承全局Wrap，因此保留为Candidate-02（SHA `f5662fbf…`）；给版本控件显式NoWrap后，实际最小/标准窗口三语检查通过。所需文字宽53.7、分配和可见宽54、右界220；旧长标签也被新检查正确拒绝。证据`.local/M5D/ui-152950710/report.json`，实际OS DPI切换仍未测。没有通过放宽宽度要求掩盖裁切。

## 官方定位模型追加对照

已有官方Python/F32定位模型也以原三窗、原文字、4线程实际运行：三窗均CPU/F32，桥退出1，raw异常条数2/6/3，生产严格Map全部拒绝。4.32→3.60秒及第二窗36帧越界仍然存在，因此不能把这场错误简单归因于GGUF量化或Vulkan，也不能推荐改官方CPU就能解决。

只使用现有离线runtime和模型，禁止字节码写入、缓存位于ProjectEcho；原输入/模型/程序hash及外部runtime46150个文件元数据保持。官方模型自带的修补结果不用于发布字幕。三窗分别冷启动，部分阶段与公开模型复验重叠，**不做速度比较**。初次PowerShell反射调用严格Map的参数绑定错误保留；随后只读同一raw修正审计，三窗均明确InvalidDataException，不把审计脚本错误当产品结果。证据`.local/M5DQwenFix/official-alignment-01/summary.json`、`summary-initial-interop-error.json`。默认配置没有切换。

## 最终交付与验收

实际环境Windows 11 25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12、NAudio3.1.0。本轮是已保存音频与公开/合成文件处理，没有硬件采集或私人音频播放。

| 检查 | 最终结果 | 证据 |
|---|---|---|
| 构建/自动回归 | Release零警告零错误；404/0，含8组新增；最终Core/Capture/Export与包内复验相同 | `.local/M5DQwenFix/build-01.log`、`bundled-regression/results.json`、`final-verification.json` |
| 最终包WPF | 13入口PASS，含待定位全文、禁无时码seek/字幕导出、跨会/重开、Pro人物/草稿及完整版本标签 | `.local/M5/m5dfix-final-ui-03/summary.json`、`.local/M3E/log-153256907/report.json` |
| 最终包视觉 | 最小/标准窗口三语完整标签检查PASS；实际最终包简中PNG已人工查看，M5Dfix Pro完整可见 | `.local/M5D/ui-153243681/report.json`及`m5dfix-brand-standard-zh-CN.png` |
| 用户会议 | 完整1.7B/Vulkan识别与另存全文PASS；368字、3段untimed、49旧文件不变；时间定位仍FAIL | `.local/M5DQwenFix/original-final-01/summary.json` |
| 正常Qwen路径 | 最终包公开/合成0.6B CPU识别+CPU定位、Vulkan0识别+定位均PASS，真实按钮/保存/回听定位/导出/切会保持 | `.local/M4/real-ui-153534662/report.json`、`real-ui-153558462/report.json` |
| 正常人物/Whisper | 最终包公开四人自动/已知模式、真实Whisper词界绑定/应用、取消和人工稿保持PASS | `.local/M5/real-ui-153611404/report.json` |
| 自包含与ZIP | 805项清单＋manifest共806文件中文解压hash通过；App/Host在无效外部runtime根下运行PASS | `.local/M5/m5dfix-verified-03.json`、`.local/diagnostics/portable-20261005-233240/portable-smoke.json` |
| 保留 | Lite403跟踪文件、M5D原ZIP及原会49文件保持；162个原生程序/模型/许可依赖与M5D相同 | `.local/M5DQwenFix/final-verification.json`、`assets-final.json` |

最终包：[ProjectEcho-win-x64-20261005-M5D-QwenFix-Final.zip](../artifacts/ProjectEcho-win-x64-20261005-M5D-QwenFix-Final.zip)，**461940702字节**。

- ZIP SHA-256：`1ddc1d3019c7fcbdc53449ed00cdf60123bd44daaf0daaae58c7607bd8f39fc5`
- Manifest SHA-256：`394a2ecce6759b196bca92952d7db3fa98af1bde75cc20849c9689cf13c57109`
- Core SHA-256：`1118a374c425299135e8328afaa8bfa8f437405e5f2c790900a206fc6fc7becb`

用户原会议中的新Qwen版本已另存，原Whisper稿及失败attempt未改。完整解压修复包，运行ProjectEcho.exe并选择原M5D的Data；重新打开会议、读取转写版本即可看到新稿。需要本场按句回听或SRT时可继续选已有Whisper版本。Qwen新稿可全文查看、搜索、校订和导出TXT，但3个待定位窗口仍没有可靠时间，不自动进行人物归属。

源码前后hash、最终实现清单与保留核验见`.local/M5DQwenFix/changes.json`、`source-final.json`、`final-verification.json`。本轮无Git提交或推送；Root历史HEAD不能冒充此补丁提交。包内本文件为封包前快照，本文件及同名`.package.json`补充包后结果，ZIP封包后不修改。**已实现/已测试的是全文保存与明确降级；定位质量、目标Win10/干净Windows及旧录音/质量缺口未升级。** M3 Lite未改，不启动M6。
