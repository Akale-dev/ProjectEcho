# 45｜M3 本地转写合同（2026-09-30）

本文件记录2026-09-30的M3后端合同；整阶段运行结果以 STATUS、验收矩阵和实施报告为准。当时没有新增 SQLite、NuGet 依赖、上传、网络模型调用或自动下载。录音与转写没有调用依赖。

2026-10-01用户决定在M3C启用SQLite，覆盖上述当时的索引实现：不可覆盖JSON原稿/修订继续权威，搜索改用可重建SQLite FTS5，另增加全文TXT和实际引擎进度；详见52。没有加入上传或自动下载。真实中文/混合语料补测的时间越界/文字质量问题亦在52中保留，不以界面进度显示证明识别质量通过。

2026-09-30追加：按用户显式授权在 `H:\AI\Whisper` 配置官方 CLI 1.9.4/b5130 和多语言 base，完成公开英文语音实测。新增外部录音导入、字幕单击回听及 SRT/VTT 导出；见 [47](47_M3_WHISPER_AND_SUBTITLE_REPLAY.md)。配置脚本的显式下载不属于产品自动下载。

## 导入与不可覆盖版本

`ProjectEcho.Core.Transcription.TranscriptService` 提供 `Import`、`ListRevisions`、`Load`、`CreateRevision`、`Search` 与 `FindOverlapCandidates`。调用方提供已保存会议目录、meetingId 及从会议清单核对的会话时长。只能在该会议的 `transcripts/` 子目录写派生结果。

导入允许 `.txt/.md/.srt/.vtt/.json`；每个输入或映射文件最多 8 MiB，严格 UTF-8 或带 BOM 的 UTF-16。最多五万段、单段十万字符、总文字四百万余字符、会话最多七天。SRT/VTT 时间须正向、有效且映射后不超过会话。文本/字幕按块迭代并提前限量，不先将恶意文件整体拆成数百万段对象。JSON 拒绝重复属性、未知段属性和缺失必需字段。

TXT/MD 始终 `untimed`，不猜时间或说话人。字幕标记/Markdown/HTML/链接全部作为普通文字；VTT `NOTE/STYLE/REGION` 在规范化段落中忽略但原稿保留。VTT 多行头元信息可保留；暂不接受 `X-TIMESTAMP-MAP`。用户 JSON 自称 `verified` 不构成验证，定时导入一律 `approximate`；`remote` 也不是某人的身份。

原稿位于 `transcripts/original/<revisionId>/original.<ext>`，与规范化 `revision.json` 同一暂存目录写完、刷新，再以唯一目录名 finalize。途中失败/取消留下 `.pending-*`，列表不把它当成功。旧原稿不删不改；加载复核原稿 SHA-256。修订位于 `transcripts/reviewed/<revisionId>/revision.json`，只允许修改文本，保留段标识、时间、来源和说话人，以 `parentRevisionId` 指向父版。重复运行 ASR 同样产生新原稿。

现行保存格式为 [transcript v2](../schemas/transcript-v2.schema.json)，原 [v1](../schemas/transcript.schema.json) 保留导入兼容。v2 保存 provider/model、导入时间、原稿哈希、分片标识和帧映射；v2 再导入创建新原稿而不覆盖旧版。Schema 仅覆盖结构，代码另验数值、时间顺序、原稿哈希、本地路径和重解析点。没有新增数据库；本地内存检索返回版本与段 ID，最多 5000 个结果，用户可缩小查询。每会最多读取 2000 个版本。

## 实际 M2 分片映射

接受 `m2-export-1` 导出清单及本地 receipt 的 `export` 对象，按实际 `sampleRate`、`spans[].fileStartFrame/sourceStartFrame/frameCount` 映射。调用方必须提供当前完整混音 `ExpectedMixSha256`，与清单的 `sourceMixSha256` 一致后才应用。`InputPartId` 是清单的 `parts[].file` 字符串，仅选择条目，不执行或打开该路径。

分片文件首部编码延迟不算会议语音；超出有效 spans 的字幕、跨不连续映射的单段字幕明确拒绝，需先由用户拆段。保留原清单副本；inputExportId 是清单 export 对象的 SHA-256。无时间文字即使来自分片也保持 untimed。规范化 v2 已是会议时间，拒绝再次套分片映射。

同一导出、不同分片、相同完整混音哈希且全局字幕区间重叠时才列候选（最多 1000 个）；相同或冲突文字均保留，绝不自动删句。相同文本出现在不同时间仍保留。不同分片的同名 Speaker 不合并为一个人。候选判断只是复核提示。

## 可选本地 whisper.cpp

`WhisperTranscriber.Inspect` 只检查本地配置，不同步散列大模型。`RunAsync` 校验所选 exe/model/input WAV 哈希，在唯一 `transcripts/asr/<attemptId>/` 中运行独立 CPU 进程；UI 应通过后台任务调用，避免首次 await 之前的文件校验占用界面。进程尝试设为 BelowNormal，实际结果记在 attempt.json。没有配置/模型时返回 `NotConfigured`，可正常录音、导出与导入。

当前参数为 `-m/-f/-l/-t/-ng/-osrt/-otxt/-ojf/-of`；于 2026-09-30 查阅 [whisper.cpp b5130 官方 CLI 源码](https://github.com/ggml-org/whisper.cpp/blob/b5130/examples/cli/cli.cpp)，并以本机1.9.4/b5130/base的公开JFK输入真实核验。M3b反馈修复移除了旧`-ml 48`字符/token长度强切：它并不按句子分段。`-ojf`启用并保存完整token时间；规范化先核对JSON/SRT文字、段时间和token全文，再按中英文句末标点及模型token边界分句。保留句末引号，避免小数、常见英文称谓/缩写切句；附近非重叠未完句可在1秒缺口、30秒/240字符上限内合并。无法可靠拆分、token缺失/乱序/重叠或JSON不匹配时保留原模型段并提示，不按句长插值。时间始终为`approximate`，不等于强制对齐，也不保证所有语言的句法正确。原始SRT仍按原哈希存档，JSON/TXT不改；旧版本与人工校订不会自动重分句，用户重跑才生成独立新稿。运行使用 `ProcessStartInfo.ArgumentList`、`UseShellExecute=false`，没有拼 shell、任意参数或远程 URL。应用不随包分发或自动下载模型。

ASR 仅接受会话目录内、哈希已知的 16 kHz mono PCM16 WAV/RF64，验证实际容器/完整帧/时长；48 kHz 混音必须由会后转换层生成副本后调用。输入全为数字零时不启动模型、不造字幕，返回 `NoSpeech`。这只是数字静音保护，不证明噪声、低声或真实安静会议不会出现幻觉。

模型输出 SRT/TXT/JSON、stdout/stderr 与耗时、来源哈希、退出码保留于 attempt 目录；成功 SRT 再导入独立版本。所选版本可通过 `TranscriptSubtitleExporter` 导出 SRT/VTT，保持完整时间轴的原毫秒起止，拒绝无时间版本，不删除重叠/重复；只允许保存到会议原件目录之外。单个诊断流限 2 Mi 字符；单次运行最长 24 小时。取消结束该次进程树，状态 `Cancelled`；失败为 `Failed`，用户可再运行得到独立新尝试。进程失败不改变音轨或已有人工修订。`Succeeded` 仅表示程序输出已解析保存，绝不等于识别准确。

Windows 合成 CLI 夹具仍仅检验失败/取消/重试和输出保留；它不代表模型。另有真实base公开英文11秒样本的管线、UI、取消/重试与字幕往返测试见47。中文/中英混合、专业词、长会议准确率、GPU benchmark 与真实安静/噪声幻觉验收仍 **NOT_RUN**，不得用短英文或合成结果代替。
