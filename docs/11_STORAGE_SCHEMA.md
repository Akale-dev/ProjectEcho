# 11｜存储、版本与数据契约

2026-09-29 M1已实现的开发版采用`m1-recording-1`清单和本文的capture-index扩展；实际合同、验证及局限见 [35_M1_IMPLEMENTATION_REPORT.md](35_M1_IMPLEMENTATION_REPORT.md)。早期v2示例仍为设计示例，不对旧录音原地迁移。

2026-10-01 M3C 用户决定覆盖旧“不使用 SQLite”：启用 SQLite 会议库和转写检索索引；原轨、capture index、manifest、filing 和不可变转写文件的权威不变。SQLite 合同与当前验证范围见 [52](52_M3C_IMPLEMENTATION.md)。

## 用户选择的数据根
建议：`D:\MeetingRecords\ProjectEcho\`（示例，不硬编码盘符）。
`Projects/<projectId>/Meetings/<meetingId>/meeting.json`；`sources/<sourceId>/*.wav`；`journal/capture-index.jsonl`；`journal/events.jsonl`；`renders/<renderId>/`；`exports/<exportId>/`；后续再有 `transcripts/original/`、`reviewed/`、`notes/`。默认未分类项目和日期时间名，开会前不强制填资料。
`BrowserProfiles/Meetings/`放在数据根独立分区，不属于任何会议，导出永不遍历此目录。开发例子只在 `examples/`，不含真实音频。

上面的 `Projects/...` 为早期设计路径；当前 M1–M3C 实际会议位于所选数据根的 `M1Recordings/<目录标识>/`。项目为会议元数据，录后归档不移动源音频。M3C 在数据根新增 `library.sqlite3`，在每会 `transcripts/` 新增 `search.sqlite3`；可恢复删除位于 `DeletedMeetings/<UTC时间-GUID>/<原目录标识>/`，不属于会议库扫描范围。

## 权威与索引
源文件和 capture index 为录音事实；manifest 是可恢复快照，events 只保存用户操作/非音频事件。**当前按2026-10-01用户决定启用 SQLite**，仅承担会议元数据索引、筛选排序和文字检索。2026-09-30 的“不加入 SQLite、搜索临时建立内存结果”保留为历史决定，现已被覆盖；唯一时间证据仍不依赖数据库。
每次结构迁移保留schemaVersion；加载旧版先备份manifest并做明确迁移，不改源音频。写入临时文件+同卷finalize；不要把两个文件rename当跨文件数据库事务，恢复器须能识别中间状态。

### M3C 可重建数据库合同

- `library.sqlite3` 的 `user_version=1`：从 `M1Recordings` 直接子目录读取 `meeting.json` 和可选 `filing.json`，事务刷新 `meetings` 表。目录标识为主键，复制会议即使 `meetingId` 相同也独立存在。日期/项目/时长可索引检索；无清单或信息异常保留 `incomplete`/`metadata_error` 状态，不能假装验证完好。
- `transcripts/search.sqlite3` 的 `user_version=1`：`source_state` 保存修订指纹，`segments` 保存修订/段引用及检索文字，`segments_fts` 为 FTS5 external-content trigram 索引。搜索前复核权威修订、缓存内容和词元，结果文本与时间从修订读取。缓存不是原稿替代品。
- 新数据库由现有文件建立，不给旧音频或转写执行原地迁移。未知数据库版本、已识别的结构/内容损坏会先保留 `*.sqlite3.rebuild-<GUID>` 及存在的 journal/WAL/SHM 文件，再重建一次。忙锁、磁盘不可写等错误显示为该功能失败，不靠删除原件或改采集范围解决。
- 数据根、会议目录、缓存及旁文件拒绝网络路径、备用数据流和重解析点。读取缓存返回的目录标识时再次限制为当前 `M1Recordings` 的直接子目录；不执行数据库中的绝对路径。索引/坏库备份含会议资料，均按本地会议数据处理，不收入 Git 或音频导出。
- 录音采集、Stop/Pause/MicOff 边界、原轨/journal/checkpoint 写入均不调用数据库。会议信息先保存到文件再刷新索引；数据库不可用不会将成功写入的原轨改标为数据库事务的一部分。

### M3C 可恢复删除合同

用户在会议菜单确认后，将整会目录同卷移动至 `DeletedMeetings/<UTC时间-GUID>/<原目录标识>/`。源与目标必须留在当前数据根，移动前检查整棵目录树不得含重解析点；不递归删除，不移入系统回收站，不改会议内部文件。目录移动是删除的权威边界，SQLite 清理在移动成功后尽力完成；查询会排除已经不存在的原目录，重建仅扫描 `M1Recordings`，因此数据库清理失败或程序中断不能让已移动会议重新出现。

恢复时把最内层原会议目录移回同一数据根的 `M1Recordings`，保留目录名并避免覆盖已有同名目录，然后刷新会议库。删除过程中应阻止该会议新增导入、识别、修订、导出或回听任务，等待已有文件读取结束；关窗等待该移动工作收尾。当前实现与相应回归的最终状态见 [52](52_M3C_IMPLEMENTATION.md)。

## 单位
session 时间 `startUs/effectiveSessionUs/sessionStartUs` 为整数微秒；API `qpcPosition100ns` 与 epoch QPC 为 100ns，不与 raw QPC ticks 混用。device frame position/QPC 的 uint64 原值用十进制字符串保存，避免 JSON 消费者浮点精度损失。epoch 来自单调计数器的同尺度转换。transcript `startMs/endMs` 为毫秒；UTC 为 ISO8601，显示时区不参与对齐。[S13]

## 最小 capture index（M0/M1 设计合同）
meeting manifest v2 新增 captureIndexPath 与 chunkId/segmentId/byteLength；format 移到 segment，避免用 source 单一格式描述格式变化。v1 设计示例迁至 v2；未来读实际旧数据先备份、显式迁移，不凭空补造原始时间戳。

`schemas/capture-index-record.schema.json` 验证 JSONL 的**每一行**。一个 CaptureHost writer 对会话串行追加五种记录，无须通用事件框架：

| data.type | 内容 |
|---|---|
| session | meetingId、epochQpc100ns、UTC 起点、时区；第一条 sequence=0 |
| segment | segmentId/sourceId、实际 format（含 blockAlign/channelMask）、设备/QPC/session 锚点；可选processingMode；启停/重置/格式变化新建 |
| packet | segment/chunk/path、原 device/QPC/rawFrameCount/flags、保留偏移及帧数、chunk 内帧位置、派生 sessionStartUs 与 timestampQuality |
| boundary | requestId、stop/pause/mic_off、各 segment 生效时间、首个禁存设备帧（可确定时）、acceptedThroughSequence、边界舍弃说明 |
| checkpoint | coveredThroughSequence、各 chunk committedFrameCount/committedPcmBytes/dataOffsetBytes；此前数据和索引已刷新 |

包络为 schemaVersion/isSynthetic/sequence/data/sha256。正式行 hash 是**原始 UTF-8 行中 data JSON 值从 `{` 至匹配 `}` 的精确字节**的 SHA-256，不含包络或换行，不重新排序/序列化。writer 输出紧凑单行 UTF-8 无 BOM；reader 按原始 token 字节边界校验并拒绝重复属性名。sequence 从 0 连续递增，hash/序号均验证。合成示例 hash 可 null，正式记录不可 null。

采集线程回执与落盘 boundary 行是两步：回执确认实际停采/释放和帧边界，允许 UI 显示 mic 已关；writer 写完此前获准 packet 后，再为 boundary 行填写自己的 acceptedThroughSequence；该 segment 从未有获准 packet 时填 null。回执不猜测尚未分配的 journal 序号，写盘继续时仍须通过边界过滤。

一包跨 chunk 时可拆成多条 packet，保留相同原始时间/rawFrameCount，以 retainedFrameOffset/frameCount 指明不同子区间；sessionStartUs 是第一个保留帧的派生时间。frameCount × blockAlign 对应实际 PCM bytes。SILENT 按零 PCM 帧存储且保留 flag；没收到的包不能伪造为 packet。segment 锚点与每包实际位置足以退出后重建映射；TIMESTAMP_ERROR 数值不能当精确锚点，timestampQuality 明示 device_referenced/estimated/invalid。漂移纠正输出派生 render 元数据，不篡改原包。

## 写盘与恢复有效边界

M1增加：`checkpoint.chunks[].segmentId/pcmSha256`、`checkpoint.observedSessionUs`、`boundary.effectiveQpc100ns`、`segment.format.validBitsPerSample`。无包与暂停的经过时间由采集线程写入observedSessionUs，不能代替包的原始API QPC。正向未观察音频区间以packet的`gapBefore100ns/gapReason`明确记录，不伪造SILENT或discontinuity；负重叠超过一个采样帧拒绝。manifest时长仅允许最终checkpoint后0–9个100ns的取整余量，全部验证成功才采用；事件恢复只复制确认时长内的合法前缀。

2026-09-30 M3b修复补充：对同一mic段，相邻原包设备位置有效且严格连续、QPC有效递增、当前无discontinuity/timestamp-error时，允许声明有独立设备帧证据的小幅QPC间隔偏差。可选整数`packet.clockJitter100ns`保存有符号的`(本包QPC−前包QPC)−前包原始帧数/采样率`（100ns，向零取整）；非零偏差须大于一输入帧、不超过±20000个100ns（2ms），且不超过前包时长的25%。超过范围或缺少连续设备帧证据的负重叠仍拒绝；正向大缺口仍保留gap。零/缺字段维持旧校验，不回填或改写旧索引。此规则只处理短期包时钟抖动，不代表真实双设备长时同步通过。

2026-10-02 端点修复将同样的有界规则用于具有有效连续设备帧的 remote 段；阈值不变、原始时钟不改。写入/恢复/混音共同使用 `DevicePacketClock` 校验；QPC-only 来源没有新增豁免。混音 v4 对有设备计数的 remote 同样保留真实微小缺口，旧原件/索引不改，旧派生缓存不自动冒充 v4。目标机复测边界见[58](58_M3D_ENDPOINT_RECORDING_FIX.md)。

同日AudioFix2派生混音升级v5：合法一帧内负量化重叠与存储合同一致；设备连续段作有界平稳映射，清单`deviceClockSmoothing`记录窗口、包数和最大校正，`wave.appliedGain`记录写PCM前和后置保护的总固定增益。原packet/边界索引不改。新增每会`processing-result.json`是带源manifest指纹的界面历史记录，绝不取代原件校验、不是最小音频导出内容；旧v4历史仅显示，不能当成v5可播放缓存。详见[59](59_M3D_ENDPOINT_MIX_AND_HISTORY_FIX.md)。

writer保留原始QPC、device、PCM和所有Stop/Pause/MicOff截止规则；不把抖动写成来源缺口。reader从磁盘重新计算并验证声明，篡改数值、缺标记负重叠、设备帧不连续均拒绝。同一原包跨30秒块时，只有首个保留子区间声明该偏差，后续子区间必须为0；恢复保留`SegmentId`。混音先按同段原包整体在相邻实际QPC锚点间映射，再按原包保留偏移映射所有chunk片段，不能把全部偏差压进块尾的一个帧；量化容差也遵循同一原包映射，明确mic-off/暂停段间隙不拉伸，末包名义尾点和实际截止不变。

录音开始时未选择mic设备，可由录音中的明确`mic_on`首次指定；只有实际打开成功才固定该ID，后续开关/恢复不得换设备。`events.jsonl`的mic_on事件附`microphoneDeviceId`以保留当时选择；初始`selection.json`保持初始事实。会话结束另存技术诊断`recording-result.json`，即使writer未能生成meeting.json仍尽力保留错误原因；它不是音频完整性权威，不能代替index/checkpoint或提升失败录音状态。

2026-10-01 mic启动修复：segment的可选`processingMode`为`shared-default`、`shared-raw`或`shared-converted`；旧记录缺字段/null表示未知，未知非空值拒绝。manifest轨道与恢复副本保留实际模式，原journal不重写。该模式只解释采集格式协商，不豁免任何时钟、缺口、hash或隐私边界检查。`recording-result.json.Diagnostics`可含MMCSS请求/释放结果、来源缓冲大小/延迟、轮询/持包时长和进程GC计数；这些是调度诊断，绝不代替原始API QPC或构造音频时间轴。详见[54](54_MICROPHONE_STARTUP_GAP_FIX.md)。

`StorageComplete/IsCleanEnd`表示保存验证，与时钟完整性分开；device无效或存在显式未观察区间时，恢复返回的旧`CaptureResult.Complete`不自动提升为true。恢复副本始终保留recovered标记和原件。

M0 2026-09-28 修复补充：packet 可含 `devicePositionValid`，并新增质量 `qpc_referenced` 表示 API QPC 可用、原始设备帧位置不可依赖；原始位置数值保持不变，不用累计帧伪造。设备位置停滞/回退或 QPC 错误使当前会话完整性降级。旧 packet 缺少新布尔字段时仍须独立核查原始序列，不能默认验收通过。详见 `28_M0_OFFLINE_CAPTURE_REPAIR.md`。

2026-09-29：同一连续采集段内，正帧原始包的 QPC 也必须严格递增；重复值不能作为两个不同包的有效时间锚点。此规则不禁止一包跨 chunk 拆出的多条索引共享同一原始 QPC，但这些子区间必须遵循上文的 retainedFrameOffset 去重合同。M0 尚不拆包，当前生产健康检查及离线审计已拒绝重复原始 QPC，见 `32_M0_REVIEW_REPAIR.md`。
1. 写完整 PCM 帧与对应 segment/packet/boundary 行；先刷新音频及这些索引，再追加 checkpoint 并刷新。≤2 秒为待验证目标。
2. checkpoint 只覆盖此前连续、hash 正确的记录。水位为 PCM 数据区字节数，WAV header 偏移单列；每个水位必须有 packet 索引覆盖并与文件完整帧吻合。
3. 恢复从 session 起读至首个截断、错序、坏 hash 或语义错误行，只承认有效 checkpoint、有效索引与实际文件完整帧的交集。未 checkpoint 的尾部不算确认保存，原件保留但不默认拼入。水位以内损坏报告损失/降级，不静默缩成“完整恢复”。
4. 完成 chunk 时修 header、关闭/校验、rename，最后写 manifest。跨文件不是事务；以 chunkId/path/hash 检查 final/partial 中间状态。结束追加含全量尾帧的 checkpoint，所有收尾/刷新/manifest 成功后才 Saved。

语义检查还包括 source/segment/chunk 引用、无重叠/未报告洞、同一包子区间不重复、mic 边界后无 PCM、根内安全路径及重解析点、实际长度/hash 一致。不能依赖内存缓存证明可恢复。[T20,T22,T51,T52,T53]

## Schema 边界
本包 JSON Schema 覆盖 manifest、capture-index 行、export、transcript、analysis 的结构；真正实现还要验证上述数值/跨文件语义。schema 过关不代表音频真实/完整。
examples全部synthetic；其中音频路径仅为例子而不是已存在的录音。文件hash为null时表示尚未产生/仅示例，正式导出Complete不允许缺hash或byteLength。

## 时间映射
export.parts[].spans 给出 `fileStartMs/fileEndMs`→`sessionStartMs/sessionEndMs` 的线性区间。M2 完整混音保留真实经过时间，暂停/缺口填静音，完整文件与各分片一般各一段映射。删除暂停/范围剪辑为后续能力，若实现须多段 spans。gaps 保留原因，设备失联不包装成用户沉默。
编码后decode probe验证时长和首尾延迟，再保存映射。压缩时间戳误差不要伪装精确；JSON中保留timestampQuality。

## 转写与分析版本
原始转写永不覆盖；reviewed版本有parentRevisionId。analysis evidence明确引用revisionId。重跑ASR不冲掉人工修改；项目结论变更新建版本而不是直接抹除旧决策。

M3实际规范化转写使用schemaVersion=2，结构见 `schemas/transcript-v2.schema.json`，v1保持作为兼容导入。原稿存 `transcripts/original/<revisionId>/`，校订存 `transcripts/reviewed/<revisionId>/`；只在完整写入临时目录后rename发布。失败/取消的pending目录不会被当作完成版本。详细文件/时间映射合同见45。

## M3M Lite：可选自动结束记录（2026-10-06）

`selection.json` 的可选 `autoStop` 保存本场 `Enabled` 和 `SilenceMinutes`，后者只允许 1、5、10；未传入选项的旧调用默认关闭。选项与实际录音来源绑定，不持久复用 PID。

`journal/events.jsonl` 可增加 `operation=auto_stop`，其 `autoStop` 对象含 `Reason`、`SilenceMinutes`、`TriggeredSessionUs`、`RemotePacketMissing`、`MicrophonePacketMissing`；正常手动事件该值为 null。只追加一次决策，之后仍保留普通 stop 和原来源边界。文件仍使用原序号/哈希和 flush 流程，不更改 packet/index 的时钟合同。

`recording-result.json.AutoStop` 保存同一结果。原因 `source_process_exited` / `no_audio_activity` 分别代表选定根进程退出 / 无声音到期，均不证明静音或完整性。未收到音频包也可能导致用户选择的无声音到期，但缺包/故障和不完整状态不得被清除。时间是去暂停后的录音时间，不修改 WAV 或原始 QPC。见 [M3M 记录](91_M3M_AUTOMATIC_RECORDING_END.md)。

## M3N Lite：录音中自动结束设置

启动时 selection.json 的 autoStop 保持原始选择；autoStopMicrophoneActivity 保存 m3n、per-channel-dc-removed-10ms-rms 和 thresholdDbfs=-50。原始 PCM、index、QPC/设备时钟和完整性合同不变。

用户录音中启用/取消或改时长，经过 HostCommand.AutoStopSettings 发送 auto_stop_settings，在采集线程验证后写 journal 的 autoStopSettings 并通过 RecordingAck.AutoStopSettings 回执。界面只接受匹配请求 ID 的回执；无效/冲突设置不改变原政策。新开启和改时长重置无声间隔，暂停不累计；进入保存后拒绝迟到变更。最终 recording-result.json.AutoStop 的分钟数来自生效设置。旧可选字段缺失保持兼容。见 [M3N交付记录](92_M3N_MIC_SILENCE_AND_PLAYBACK_CONTROLS.md)。
