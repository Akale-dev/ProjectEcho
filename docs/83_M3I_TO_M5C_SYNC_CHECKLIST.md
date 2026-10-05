# 83｜M3I → M5C 同步清单（等待用户验收）

2026-10-05。用户决定长期保留 M3 为 Lite，M5 及以后为 Pro。本轮只修改 Lite；**M5C 同步状态：NOT_APPLIED，等待用户报告验收通过后，按本次授权同步。** 验收结果尚未到达，因此现在不执行同步；验收通过后无需重复询问同步许可。

## 版本与边界

- Lite 工作树：`lite/`，分支 `codex/m3i-lite`。
- M3I修复源码提交：`98d342005fef76dc3571db73a8de157076cc2b70`，完整交付最终665文件ZIP与包内307回归/七WPF/CPU及Vulkan已通过；用户公司验收仍NOT_RUN。
- 已核验 M3Hfix 源码基线提交：`e734e0557f1bdde826b201c0fefd1f44acab79d8`（基于 M3H 的 `dc26ea8`，有意重建，非历史原始快照）。
- 原 M3Hfix ZIP SHA-256：`09179e69a3ca36152fd3812e81071af51a1481d71b4b17413721725240e3976a`。
- Pro 当前产品基线仍为 M5C-Final；主目录存在其未提交源码，禁止 reset/checkout 覆盖。只迁移下面的具体修复，不把 Lite 整棵树覆盖 Pro。
- Lite 原始 282 回归通过。Capture 1552、Export 822 方法的 IL/局部签名/异常区与旧包一致；Core 1149 方法在生成正则类型路径标记与字符串 CRLF/LF 归一化后一致。二进制哈希不同，不声称逐字节原构建复现。对照工具与原始未归一化差异都保存在 `.local/M3I/`。

## 待迁移项目

| 项目 | 核心文件 / 接口 | M5C 迁移时必须保持 | 验证要求 |
|---|---|---|---|
| 合法音频大包 | CapturePacketScratch、RecordingSourceFactory、CapturePacketScratchTests | 仅移植容量及数值诊断；1MiB上限、真实帧/QPC、ReleaseBuffer、只复制cutoff前缀和清零不变；不扩大来源 | 小reported/合法大包/恰上限/超限/silent/cutoff/零分配；用户公司Chrome等待预览后主动录音 |
| Whisper 零时长段 | WhisperZeroDurationTimestamp、WhisperSentenceSegmenter、TranscriptService、WhisperTranscriber、M3iWhisperTimestampTests | 保持Pro已有token音频/混音hash绑定、frameCount、timelinePolicy与人物定位证据；不要用Lite的短签名覆盖M5C ImportWhisper；Qwen路径不改 | 第906段同值、自己token恢复、证据缺失untimed、全部文字/原稿/人工稿、异常仍拒绝；人物侧应对缺时间明确待确认 |
| 无时间字幕显示 | MainWindow.Replay、M3iUntimedUiSmoke | Pro已有DisplayText/人物标签/不同revision类型不能退回Lite；只加缺时间数量提示 | 正常句仍可回听，untimed禁seek，全文可导，SRT/VTT不悄悄漏词 |
| 正常录音说明精简 | RecordingDiagnosticPresentation、MainWindow.DiagnosticPresentation、ExportHistory、MeetingLog、SetStatus、三语资源 | 只识别整句已知模板；不变更Complete/StorageComplete、原始warnings、原轨/时钟/旧日志；未知或追加故障必须完整保留 | 正常QPC能力中性显示；真实gap/noPCM/QPC错误/磁盘错仍可见；旧文件hash不变 |
| 长录音处理进度 | MixProgress及Export管线、App混音任务进度接线 | 保留Pro的Qwen/说话人作业退让、任务归属及切会；百分比按实际字节/帧而非时间；进度不反复写盘/Log | 阶段/百分比/取消/跨会/迟到/失败无完成；公开或合成完整链路 |
| 原件校验提速 | M1Recovery、M3iRecoveryProgressTests | 仅缓存同一chunk的路径验证；每包精确身份/路径约束、打开前安全复查、PCM/整文件SHA和全部时间检查保留 | 同一小时原轨439.393秒→29.297秒；WAV哈希完全相同；改路径/换segment/重复路径/坏hash仍拒绝 |
| 诊断版本与发行 | 中性Log导出标题、M3I Lite版本与独立封包脚本 | Pro使用自身版本名称与完整Qwen/人物依赖许可；不得复制Lite包中的旧THIRD_PARTY替换Pro | 构建/全套Pro回归/包内WPF/真实本地引擎/完整ZIP检查 |

## M3J追加迁移项（2026-10-05）

用户已确认M3I录音错误不再复现及后处理提速，但ASR又发现914ms越界，因此Lite整体验收尚未完成，Pro仍不执行同步。M3J记录见84号。

- 将WhisperOverrunTimestamp v2与分句前逐段处理迁入Pro，保留Pro自己的token/音频hash/帧数/人物证据；不要只扩大旧250ms常数或整篇平移。多段异常保全文、各自证据恢复、手动导入严格、诊断有界必须一同迁移。
- 迁入品牌左移9px（不改246侧栏）、会议自身时长、独立录音请求归属的录后处理卡。Pro现有Qwen/说话人操作及进度不可被Lite较短文件覆盖。
- 增加M3iAsrEndBoundaryTests的12组与M3jUiSmoke；用户数据只在本地验证，不入Git/发布包。原M3I的307回归应继续保留，M3J共319。
- 本机整场重转写、原输出恢复、目标机用户验收、模型文字准确性要分开记录。
## 用户验收后流程

1. 先读 `82_M3I_LITE_CAPTURE_AND_ASR_FIX.md`、本文件、最新 STATUS 与用户反馈。明确哪些项通过、哪些仍失败。
2. 保全 M5C 未提交源码和旧包；在独立维护分支/目录应用已验收的增量，记录所用 Lite 源码提交与迁移差异。
3. 按上述接口冲突点手工审查，尤其 TranscriptService 的人物证据和 App 的 Qwen/人物任务归属。不得整文件盲目覆盖。
4. 完成 Pro 构建、原有355及新增回归、实际无采集WPF、包内模型链、导出/取消/原稿及人工稿保护。性能数值和目标Win10复测分别记录。
5. 新建Pro修复包，保留M5C旧目录/ZIP与Data，更新 STATUS、验收表和本同步清单。未执行的检查仍标 NOT_RUN。

本轮不录音、不启麦、不访问私人会议、不下载模型、不上传或推送。只有用户主动验证可产生目标机新录音。