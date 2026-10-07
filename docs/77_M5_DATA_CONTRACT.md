# 2026-10-06 M5E 增量

人物修订 schema 2 增加 `edit-subtitles`（批量字幕校订），对应 `SpeakerCorrection.SubtitleEdits` 保存每段 ID、原/新正文及原/新起止毫秒。一次保存全部合法草稿形成一个不可变子版本；取消、无效输入不发布部分版本。原 schema 1/2 可继续读取；旧版程序不认识该操作，新增稿应使用 M5E。

`SpeakerSegment.manualTiming` 默认为 false 且默认不序列化，只有明确人工改时才写 true。人工时间须成对、正向、不超会议时长；可同时清空。改时段的模型候选换人点清除，时间来源为 `human-edited-subtitle`；不自动重分人物。人物 ID/显示名、原始字文范围、音频/分析/源稿和父版本 hash 全部保留。重读按逐段修改记录重建预期结果，禁止未声明的时码/人物/来源更改，不能以人工标记绕过父稿核对。

完整同步及验证见 [M5E](94_M5E_LITE_SYNC_AND_PLAYBACK.md)。以下为历史合同。

# 77｜M5 同场说话人本地合同

M5C增量：新worker已知人数为`spherical-kmeans-global-v1`，自动模式明确保留`sherpa-complete-link-global-v1`；actual算法、程序/hash/源码引用逐次保存，不重标旧结果。人工时间切点新增`split-segment-manual-time`操作，强制schema2/父hash、`TimingSource=human-entered-speaker-boundary`；不要求模型候选，但必须由用户明确给出本录音时刻和文字光标。旧`SplitSegment`仍只接受模型候选。新操作需M5C读取，旧稿不覆盖。

M5B增量：人物正文可编辑，首次改字新增schema2；schema1历史读取不变。`SpeakerRevision.ParentRevisionSha256`绑定父稿真实文件，`SpeakerCorrection`的`edit-text`记录`PreviousText/ReplacementText`。`SpeakerSegment.TextEditGroupId`标记粗粒度原文来源；编辑后人工切分的子段共享来源组，原范围按组只计算一次，不将新文字长度伪装为原文/音频逐字映射。所有新字段为空时不写入v1，避免无意义的旧版兼容破坏。schema2须由M5B读取。

每次保存是经过验证的一项父稿转换；加载校验父哈希、单步修改及全条祖先链，索引/批量读取仅在本次调用内复用已验证祖先，不跨调用缓存权威资料。显示投影不继承原ASR的`Alignment`，原稿证据仍保留于原稿。原字幕外的重复/丢字仍拒绝；只有有`edit-text`证据的指定正文允许改变。具体行为与证据见[80](80_M5B_REPLAY_EDITING_AND_VOICE_QUALITY.md)。

2026-10-04。实现定义，不能代替 P5 的真实声音或 Windows 验收。所有时间均沿用当前已保存音频的 `elapsed-v1`、`pause-excluded-v1` 或 `file-v1`，不再应用暂停偏移。原 transcript v2 文件继续独立可读。

## 权威文件

- `diarization/analyses/{analysisId}/analysis.json`：`SpeakerAnalysis` v1。
- `diarization/revisions/{revisionId}/revision.json`：`SpeakerRevision` v1。
- 发布先写 `.pending-{id}`，flush 后原子移动目录；未完成目录不会列为成功。每次分析、纠错和改名都使用新 Guid N；不覆盖原文件。
- SQLite 为可重建查询缓存；不充当文字、名称、身份或版本的唯一来源。

## 音频分析

`SpeakerAnalysis` 必需字段为 `schemaVersion=1`、`meetingId`、`analysisId`、`createdAt`、`audio`、`engine`、`speakers`、`intervals`、`warnings`；`isSynthetic` 默认 false。

`audio` 保存 `inputWaveSha256`、可空 `sourceMixSha256`、`sampleRate=16000`、`frameCount`、`timelinePolicy`。音频哈希、帧数与 policy 一起核对，不能拿相同文件名或近似时长代替。

`engine` 保存 `engine/version/executableSha256/segmentationModelSha256/embeddingModelSha256`、可空 `knownSpeakerCount`、`clusteringThreshold`、`actualDevice`、`clusteringPolicy`、`elapsedMilliseconds`、`peakWorkingSetBytes`。已知人数和自动估计是不同配置；运行时必须记录实际全场聚类政策。

`speakers` 每项包含稳定 `speakerId`、默认 `code`、可空 `displayName`；名称只是本场用户映射。`intervals` 每项为半开范围 `startMs/endMs`、可空 `speakerId`、`quality=estimated|uncertain|verified`、`overlap`。无可靠证据可留空人物；不能把 remote/mic 当人物。

## 带人物字幕版本

`SpeakerRevision` v1 必需字段：`meetingId/revisionId/parentRevisionId/createdAt`、`sourceTranscriptRevisionId/sourceTranscriptSha256`、`analysisId/analysisSha256`、`audio/meetingDurationMs`、`speakers/segments/corrections/warnings`。转写 SHA 是规范序列化内容哈希，分析 SHA 是实际权威文件字节哈希；加载时重新核对。

每个 `SpeakerSegment` 保存新 `segmentId`、源 `sourceSegmentId`、原句 UTF-16 `textStart/textLength`、`startMs/endMs/text`、可空 `speakerId`、`status`、`candidateBoundaries`、`timingSource`。所有输出按输入段顺序，文字范围必须从零到末尾恰好覆盖一次；Unicode代理对不得切开。前缀不进入 `text`。

`status` 为 `assigned`（自动估计）、`manual`（人工指定）、`unconfirmed`、`multiple-speakers`、`overlap-uncertain` 或 `untimed`。`assigned/manual` 必须存在人物引用；未定身份不得伪装分配。无时间文本不自动造时间。

`SpeakerCorrection` 记录 `operation/createdAt/segmentIds`、可空 `fromSpeakerId/toSpeakerId/displayName/textOffset/timeMs`。操作包含 rename、assign、merge、split-speaker、split-segment。改名只改显示映射；单段改人不改正文；拆出创建新身份与未使用代号；切句只接受本段模型候选换人时间与用户明确选定的文字位置。

## 已保存音文证据

既有 `TranscriptRevision.Alignment` 合同继续使用，未增加必填 v2 字段。新 Whisper 保存原始完整 token JSON 为该原稿 `alignment.json`，policy 为 `whisper-exact-token-boundaries-v1`，绑定输入 WAV/混音 SHA、真实帧数和 timelinePolicy；32 MiB 外或不可解析的证据不阻止原有句级字幕，但不得用于内部拆句。

Qwen 读取该稿已保存 `m4-qwen-alignment-1` 证据，沿明确的 attempt ID 找原始词界文件，核对 rawWords SHA、完整窗口文字 SHA及原始采样边界，再加一次窗口偏移。再次运行不会自动替换所选证据。

只复用与当前所选版本相同文字、相同句段时码的边界。人工改字或删字的段不用旧 token。缺证据、零时长词、重叠、反序、多候选或超过 160ms 容差时保留原句全部文字与候选时间，不按字数分配，不杜撰重叠双方文字。160ms 是保守配对容差，不是声学精度承诺。

## 导出

TXT/SRT/VTT 支持标签开关，名称或代号只在写出时加 `[说话人 名称]`。关闭开关保持纯发言正文。字幕毫秒沿用当前音频轴，旧暂停保留、新暂停排除；导入普通前缀仍是普通文本，不能还原可信 speakerId。

人物版正文与所选转写绑定；要校订正文先回源转写创建新稿，再将现有分析应用到该稿。旧人物修订和原版名称保留。权威文件加载拒绝越界路径、重解析点、重复身份、缺字/重字、源哈希变化、错误会议及错误时间轴。
