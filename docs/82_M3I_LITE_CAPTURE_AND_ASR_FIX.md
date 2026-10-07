# 82｜M3I Lite：录音、转写与长会处理修复

2026-10-05。本轮基于已交付 M3Hfix 维护为 **M3I Lite**；M3 长期保留为 Lite，M5 及以后为 Pro。用户报告验收通过后，再按本次要求同步 M5C，**本轮未同步 Pro**。增量迁移与冲突清单见 [83](83_M3I_TO_M5C_SYNC_CHECKLIST.md)。本文件包内副本为封包前快照；最终封包验证以项目本文件追加记录和同名 `.package.json` 为准。

## 现场证据

仅读取项目内两份脱敏 DEBUG 日志；没有取得或上传会议音频、原始字幕、提示词。日志目标环境为 Windows 10.0.19043.0/x64、NAudio 3.1.0、ProcessTree、MicEnabled=False，不能由该字符串推断补丁号。

- 录音失败：来源报告音频包超过预分配缓冲区，该段未收到有效音频；22.505 秒文件是明确标注的占位静音，不是可恢复的会议声音。用户报告预览有电平后等待数分钟再开始；日志没有实际 BufferSize/包帧数，不能把等待时长断言为已证实根因。
- 转写失败：另一场3439.371秒原轨保存、WAV校验和三片M4A导出成功；CPU识别在2890774ms后因第906段2864760–2864760ms零时长失败。发生在47:44.760，非EOF，与旧250ms末端容差无关。零时长从何产生仍缺原SRT/JSON证据。
- 长会等待：现场混音阶段约469秒，界面笼统显示“校验”；本机同样构造一小时原轨复现旧完整链439.393秒，其中绝大部分发生在原件恢复校验、混音文件创建之前。

## 修复内容

### 1. 合法音频大包

每路在 Start 前预分配既有1MiB硬上限，reported BufferSize只保留为诊断值，避免小reported值把实际合法包误判为超限。没有动态无限扩容、截短合法包或扩大录音来源。保留原始帧/QPC/device、唯一finally中的ReleaseBuffer、截止前完整帧复制、静音与清零。超限错误附rawFrames/retainedFrames/blockAlign/reportedBufferFrames/scratchBytes数值。

旧发行DLL的合成内存对照：reported=1帧、blockAlign=8、实际16字节，旧容量8字节返回ExceedsScratchCapacity；新版容量1048576返回Success。这证明代码路径修复，不冒充Win10真实采集复测。

### 2. Whisper零时长段

仅内部Whisper输出路径允许音频范围内Start==End的局部处理。全部JSON/SRT段数、文字与原起止一一匹配后，若本段完整词token的时间有效、单调、在录音内并覆盖原时间点，使用本段自身范围，仍标approximate。

缺少可靠证据时保留全文与顺序，将该段标为无时间待校对，不猜1ms、不借相邻段、不删除文字；合法其他段照常分句/回听，不跨无时间段合并。全文TXT与搜索可用；此版本SRT/VTT明确不可导，避免漏词。回听页显示无时间段数量，转写页禁该段seek。

负数、倒序、EOF和任意越界仍拒绝；普通手动导入不因自称Whisper而绕过严格校验。原始输出、原稿及人工修订不覆盖。诊断最多列100条并给总数，10001段仍能保存重开。只有脱敏Log，未对公司那场原字幕直接恢复；需在新版本主动重试转写。

### 3. 正常录音的开发提示

这些固定文字并不都代表录音失败。成功存储与设备计数能力、长期声学同步验收是不同事实。显示层精确识别完整旧模板，把remote使用QPC改为中性系统时间戳说明，移出固定的长测验收文字；有已知remote能力依据时不重复展示旧Complete开发字段。缺该依据时仍保留“时钟完整性需检查”。

真实gap、无PCM、无效QPC、来源故障、保存不全、磁盘错以及任何未知/追加诊断完整保留。Complete/StorageComplete、原始warnings、PCM和时钟文件都不改。新操作结果/日志使用清楚的文字；旧日志原样保留和导出，旧处理回执只做内存显示投影。Log导出标题不再固定误称M3F。

### 4. 一小时处理提速与真实进度

主要瓶颈是M1Recovery对每个packet重复调用M1Paths.Resolve，反复查询同一个chunk文件的目录祖先。现只在首次发现chunk时验证路径；后续每包仍严格比对路径/segment，实际打开文件前仍复查重解析点。PCM哈希、整WAV哈希、水位、帧数、时间轴、混音与所有峰值计算/复查全部保留。

**30秒分块不变。** 旧开销主要按包数发生，改长分块不能消除重复查询，还会改变恢复与存储边界。本轮没有改变原文件格式、暂停规则、峰值算法或采样计算，也未引入SIMD/新依赖。

阶段包括索引、原始PCM、整原轨文件、持锁复核、混音音量分析、写混音、峰值检查/保护/复查、回放文件hash。百分比依据当前阶段实际字节/帧数；未知总量显示准备中，不伪造整体匀速百分比。当前会议卡片直接可见，无需滚到页底；显示阶段及累计耗时。

中间进度仅内存显示，不逐次写Log/处理回执。每次任务完成/取消/失败追加一次阶段耗时。会议、操作代次、取消令牌隔离迟到事件，切会不串信息，完成后不被旧进度覆盖，仍可开始下一场录音。

## 已完成验证

环境：Windows 11 25H2 **26200.9457/x64**；SDK10.0.401/runtime10.0.12、NAudio3.1.0；已有项目缓存离线构建，无依赖/模型下载。

| 项目 | 实际结果 | 本地证据 |
|---|---|---|
| Lite基线重建 | 基于M3H dc26ea8的独立工作树；纯M3Hfix基线提交e734e055；原282回归PASS | `.local/M3I/baseline-build.log`、`baseline-tests.log` |
| 原包方法对照 | Capture1552、Export822方法IL/locals/EH一致；Core1149在生成正则路径名与字符串CRLF/LF归一化后一致；整DLL hash不同，不称逐字节复建 | `.local/M3I/baseline-il-*.json` |
| 新实现构建/回归 | 零警告零错误；307项PASS，含9组ASR、5组说明投影、4组混音进度、6组恢复进度/篡改及合法大包 | `lite/.local/M3I/integrated/` |
| Windows实际界面 | 七个WPF入口PASS；含新增无时间文字、说明投影、真实dispatcher百分比/切会/取消/迟到检查；上方进度可见 | `lite/.local/M3I/ui-visible/ui-runs.json`、`lite/.local/M3E/log-063502063/` |
| 大包旧/新DLL | 合成内存旧拒绝/新成功，未打开设备 | `.local/M3I/scratch-old-new.json` |
| 一小时隔离对照 | 同3600秒/172800000帧，48k双声道float、10ms包、30秒块、QPC-only合成原轨；原发行439393ms，仅路径优化29297ms，约15倍；123原件hash保持 | `.local/M3I/benchmark-old.json`、`benchmark-path-cache.json` |
| 音频不变 | 两次最终WAV SHA-256均为b7cb3580dc7f8c846cbd6defcf4a06834a44650a252a912e6295fe2b989ec861 | 同上 |

一小时对照是合成波形/磁盘文件，不是现场录音或自然会议音质验收；不能外推公司Windows耗时。最终完整进度版/包内结果在后续追加。

过程中的失败也保留：首轮290PASS/2FAIL发现文本编辑因换行格式未命中缓冲赋值，已修；进度整合中测试插值括号错误和旧注入nullable警告已修；没有把这些初次运行记成功。

## 用户复测与未验证

完整解压M3I Lite新目录，关闭旧程序，在设置中选择原Data。旧版本与Data保留。本次没有新增录音、mic采集、私人会议、上传、推送或系统设置修改。

1. 在公司电脑主动选择Chrome进程，确认原范围和mic关闭，预览数分钟后主动录音，停止并确认声音/尾声。目标19043真实来源行为仍NOT_RUN。
2. 对原一小时会议主动重试转写，确认全文保存及无法定位段提示；原SRT/JSON未提供，因此原会议实际重试仍NOT_RUN。
3. 主动生成/重试完整混音，观察当前会议卡片阶段百分比与Log中的一次耗时汇总。旧已生成混音重开校验也复用恢复提速，但本轮实时百分比针对生成混音过程。

真实麦克风、长时双设备声学同步、其他硬件、干净Windows等M1/M2旧失败/未测不升级。M5C同步仍等待用户验收；不得整文件覆盖Pro中的Qwen/人物版逻辑。
## 最终交付及包内复测（2026-10-05）

交付 `artifacts/ProjectEcho-win-x64-20261005-M3I-Lite-Final/ProjectEcho.exe` 与同名ZIP。ZIP **262666645字节**，SHA-256 **`74af54360a02473d6fde2cc5bd0229af3ce6e1bc782395c278dccd896d2788fc`**。664项清单加manifest共665个ZIP文件，中文路径实际解压逐项hash通过，manifest SHA-256 `44a0c2ea710ebd3cf57309e26c8d04431d83d2598ff9b29f26ddf9a0c32ba55a`。包含自带App/Host运行时、CPU/Vulkan/base及原许可；无Pro组件或私人Data。

源码修复提交 **`98d342005fef76dc3571db73a8de157076cc2b70`**；与基线e734e055之间为本轮增量。后续文档收尾提交不改变已测程序。

- 包内原样Core/Capture/Export替换到独立测试运行目录后，**307/0**；App、Host、回归与最终一小时探针的三个生产DLL hash一致。
- 中文解压后App与Host在无有效外部DOTNET_ROOT下启动/IPC通过。七项包内WPF通过，含三项新增实际界面测试。ASR设置流程的独立合成CLI是开发用framework-dependent测试程序，首轮受无效DOTNET_ROOT影响失败；仅该测试给此CLI提供项目SDK后通过，产品文件没有修改。首轮失败保留，不冒充该测试本身是无SDK环境。
- 实际随包CPU/base公开11秒JFK识别成功，真实子进程取消及重跑通过；Vulkan0在合成17.345秒暂停、11秒有效音频链路成功，PCM/时钟保持，WAV/MP3/16k/SRT/VTT完整验证。
- 最终包完整进度版再次处理同一小时原轨 **29173ms**，对照原包439393ms，约 **15.06倍**。156条真实阶段事件数值不倒退；172800000帧及WAV SHA与旧版完全相同，123原件不改。此为同一开发机合成对照，仍不能外推公司电脑。
- 主目录 **297个Pro源码/测试/协议文件**与本轮前hash相同；旧M3Hfix/M5C两个ZIP保持。M5C修复仍NOT_APPLIED。

包后证据：`.local/M3I/zip-verification-final.json`、`bundled-regression/results.json`、`bundled-ui/runs.json`、`cpu-bundled-final/report.json`、`vulkan-bundled-final/summary.json`、`performance-final-summary.json`、`tested-components-final.json`、`preservation-final.json`。包内说明为封包前快照；同名`.package.json`与本节补充最终核验，未覆盖已验ZIP。

公司Win10/Chrome预览后开录、原一小时会议转写及现场处理耗时仍待用户主动验收；干净Windows、真实mic及旧M1/M2未验范围不变。
