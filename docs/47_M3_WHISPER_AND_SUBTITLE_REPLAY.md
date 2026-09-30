# 47｜M3 真实 whisper.cpp、外部录音与同步字幕（2026-09-30）

## 本轮范围

用户授权在 `H:\AI\Whisper` 配置 CLI 与 base，选择公开语音完成 M3 接入，并要求外部录音/字幕导入后单击字幕跳转播放；随后明确要求识别结果可保存为同步字幕。该指定目录是本轮额外授权范围；没有改全局 PATH、代理、注册表策略、其他项目或浏览器资料。没有入会、采集音频、启用麦克风或上传内容。

执行环境：Windows 11 25H2 **26200.9457 / x64**，SDK **10.0.401**、运行时 **10.0.12**、NAudio **3.1.0**。来源模式：`PUBLIC SPEECH FILE / SYNTHETIC FILES / NO CAPTURE`。OS 补丁见 `.local/diagnostics/m3-whisper-20260930/windows-version.json`；注册表兼容字段 ProductName 仍写 Windows 10 Pro，不用它覆盖实际 build/DisplayVersion。

## CLI、模型和测试材料

- 官方 [whisper.cpp b5130](https://github.com/ggml-org/whisper.cpp/releases/tag/b5130) CPU x64；实际 `--version` 为 **1.9.4**。官方 v1.9.4 页面没有附件，使用同次 b5130 构建附件。CLI 路径 `H:\AI\Whisper\whisper-b5130\Release\whisper-cli.exe`，保留完整 Release 目录及原生 DLL。
- ZIP 为 8,573,270 字节，SHA-256 `f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c`，与 GitHub release asset digest 一致。
- 多语言 **base**（不是 base.en），`H:\AI\Whisper\models\ggml-base.bin`，147,951,465 字节；SHA-256 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`，与上游 LFS 对象一致。来源是官方文档引用的 [ggerganov/whisper.cpp 模型库](https://huggingface.co/ggerganov/whisper.cpp)，固定 revision `5359861c739e955e79d9a303bcbc70fb988958b1`。
- CLI、模型 MIT 许可和模型卡保存在 `H:\AI\Whisper\licenses`；下载来源、字节与全部解压文件哈希在 `H:\AI\Whisper\provenance.json`，项目内副本为 `.local/diagnostics/m3-whisper-20260930/whisper-provenance.json`。
- 公开测试语音为 [官方 JFK 样本](https://github.com/ggml-org/whisper.cpp/blob/b5130/samples/jfk.wav)，11.000 秒、16k mono PCM16；路径 `H:\AI\Whisper\samples\jfk.wav`，SHA-256 `59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e`。没有使用用户会议内容。
- `scripts/Install-WhisperLocal.ps1` 是用户显式授权的开发配置工具，产品不调用它、不在录音或识别时下载。检查官方 ZIP 摘要、固定模型哈希、解压路径及目录重解析点；不改全局环境，失败保留下载证据。

配置文件为 `H:\AI\Whisper\projectecho-whisper.json`，已接入开发数据目录 `.local/settings/whisper.json`。默认 `auto`、2 CPU 线程、预期模型 SHA-256；CLI 采用 `-ng` 禁 GPU，`-ml 48` 生成较短字幕段。`-ml` 已通过该真实 CLI 的帮助和输出核验；分段时间由模型生成，仍标 `approximate`，不宣称逐字强制对齐。

## 文件与回放行为

新增 `AudioImportService`：WAV/MP3/M4A 文件先只读锁定、哈希和复制，完整解码到 EOF，再生成完整 48k mono PCM16 回放及峰值保护。原文件和副本不修改。使用独立 `m3-audio-import-1` 清单，不伪造来源、设备计数、QPC 或 capture journal；时间从完整解码文件的 0 秒开始。最多 12 小时/32 GiB；多声道按同帧平均混合，不把声道首尾拼接。完成前目录为 `.pending-*`，失败不会列为已保存会议。

会议库新增“导入录音”入口；重开导入会议时从磁盘校验原件、回放 SHA-256、帧数和时长。外部文件没有采集 journal，禁用采集恢复。原本 M1 历史录音仍按既有流程生成/重试完整混音，不把外部导入当作采集验收。

字幕条目通过真正的点击事件触发定位：首次打开/换文件后等待 MediaOpened，同文件已打开则直接定位；同条重复点击、停止后再点和快速切换保留最后一个请求。切换文件时隔离旧播放器实例，旧事件不能消费新请求。开始录音、Paused、Stopping、Faulted、关窗和选会均清除待回放请求。

## SRT / VTT 同步字幕

识别原始 SRT/TXT/JSON 自动保存在每次 `transcripts/asr/<attemptId>/`。UI 在“字幕回放与校订”提供 **导出字幕 SRT / VTT…**，可保存所选原稿或人工修订；输出 UTF-8，起止时间保持完整会议时间轴，精确保留已有毫秒数值。排序稳定、重叠与重复发言保留；暂停和静音不裁掉、不按字幕重新拼时间。时间来自模型/外部字幕的可信度仍是近似值。

无时间文字拒绝生成同步字幕，不伪造起止时间。文本中的空白分段/时间箭头可能破坏字幕语法，需先在新修订中处理，失败不覆盖目标。通过同目录暂存文件完成写入再替换；未显式允许时不覆盖已有目标。任何含 meeting.json 的会议原件目录均不作为导出目的地，保护原稿和修订。

## 已执行的验证

| 项目 | 结果与范围 | 证据 |
|---|---|---|
| Release 构建 | PASS，0 错误/0 警告 | `.local/diagnostics/m3-whisper-20260930/build-updated.txt` |
| 默认自动回归 | **113 PASS / 0 FAIL**；含 5 组音频导入、3 组字幕导出新检查 | `.local/TestResults/M3Whisper/default-first-20260930/results.json` |
| 真实 CLI 直接识别 | PASS，auto/2线程，官方原样本约2.55秒；`-ml 48`输出3段 | `.local/diagnostics/m3-whisper-20260930/cli-stderr.txt`、`jfk-segments.srt` |
| 真实 M3 文件管线 | PASS，11秒样本→48k528000帧→16k176000帧→真实base；en/8线程首次984ms、重试786ms | `.local/TestResults/M3Whisper/real-20260930/report.json` |
| 真实进程取消/重试 | PASS，观察本probe的真实CLI子进程后取消；144ms结束、确认子进程退出；原版保留 | 同上 |
| 真实字幕SRT/VTT导出往返 | PASS，3段文字及所有StartMs/EndMs一致；输入、模型、exe、原稿与音频hash不变 | 同上 `subtitleRoundTrips`；同目录 `jfk-real-transcript.srt/.vtt` |
| 合成字幕边界 | PASS，Unicode/多行/超1小时/毫秒、重叠、重复、修订、无时间拒绝、取消/覆盖保护 | 默认回归 `m3-subtitle-export` |
| 真正 WPF 本地识别入口 | PASS，启动加载持久配置→导入录音→实际按钮Click→base auto/2线程→显示3段→导出及UI重新导入；含输入转换与界面总耗时2823ms | `.local/diagnostics/m3-whisper-ui-20260930-075337-340/report.json` |
| WPF 字幕单击定位 | PASS，9项实际定位与进度推进；包含0/5500/9760ms、重复、快速、跨媒体、Stop后、Saved后与磁盘重开；4种禁止回放状态及无时间文字通过 | `.local/diagnostics/m3-replay-20260930-075221-375/report.json` |
| 实际WPF渲染检查 | PASS，字幕行/单击说明/当前会议可见，操作区可滚动；自有WPF控件渲染，不是OS截图或物理鼠标测试 | 同目录 `imported-subtitles-replay.png` |

最终路径规范化补充断言后：完整回归仍 **113/0**，证据 `.local/TestResults/M3/replay-path-fix-20260930/results.json`；Release 0错误/0警告见 `.local/diagnostics/m3-whisper-20260930/replay-path-fix-build.txt`。再次完整WPF回放PASS，证据 `.local/diagnostics/m3-replay-20260930-075542-083/report.json`；上下两张实际WPF图审查通过，`imported-subtitles-replay-bottom.png`显示可见的SRT/VTT导出按钮。

## 最终交付与发布包实测

`artifacts/ProjectEcho-win-x64-20260930-M3-Whisper/ProjectEcho.exe`，保留整个目录。**616个发布文件，231040862字节**；release-manifest SHA-256 `be3e55cde83835445f7386f8f4e1ec8febc93d2a3ff5a4b36dbcc7db8b9de2a0`。旧包和用户原件没有替换。发布清单不含Whisper模型/CLI、录音、转写或开发SDK。

先以新包通过全部发布文件哈希/默认启动/包内Host运行时检查，证据 `.local/diagnostics/portable-20260930-155739/portable-smoke.json`。之后仅添加机器本地 `Data/settings/whisper.json`，指向已核验的 `H:\AI\Whisper`；该配置属于可变用户数据，不属于616个发布文件，不重新把Data收入发布清单。

发布版直接exe（不依赖开发Dotnet包装器）实跑以下4项，全部退出0：

- `--m3-whisper-smoke`：真实UI/base/auto/2线程，含准备与界面总耗时 **2762ms**，识别进程记录2701ms。SRT/VTT导出及UI再导入时间/文字一致。证据 `.local/diagnostics/m3-whisper-ui-20260930-075809-712/report.json`。
- `--m3-replay-smoke`：使用上轮真实UI导出的3段字幕（0/5760/8000ms）导入公开录音，9种实际定位推进及所有禁止回放状态通过。证据 `.local/diagnostics/m3-replay-20260930-075813-620/report.json`。与开发版0/5500/9760ms测试互补，未经修改/伪造语音时间戳。
- `--transcription-smoke`：既有导入、搜索、修订、Paused保护和关闭清理通过。
- `--export-close-smoke`：既有M2导出取消/关窗清理通过。

四项命令与退出状态见 `.local/diagnostics/m3-whisper-20260930/bundled-workflows.json`。实际操作入口为“会议库 → 导入录音”及“转写与搜索 → 本地识别/导入文字 → 字幕回放与校订 → 导出字幕 SRT / VTT…”。本机公开示例语音在 `H:\AI\Whisper\samples\jfk.wav`，可用的真实识别SRT/VTT在上述UI证据目录的 `real-whisper-ui/jfk-ui-real.srt/.vtt`。

追踪：R18；T37真实进程生命周期（仍保留并发真实采集未测的PARTIAL）、T38格式、T40混合语言NOT_RUN、T47依赖、T55回放边界；新增T58外部录音与字幕回听、T59同步字幕导出。

以上耗时是本机短样本测量，不是长会议、中文或普适速度/准确率承诺。

## 本轮失败记录与剩余验证

- 首次直接启动开发版 `.exe` 未进入托管测试（未配置全局 .NET）；终止的仅是本次测试进程，改由 `scripts/Dotnet.ps1` 的项目本地运行时启动。未改全局 PATH 或安装系统运行时。
- 首次 M3 WPF 回放测试为 FAIL：文件 MediaOpened 后 360ms 内 Position 仍为0；证据 `.local/diagnostics/m3-replay-20260930-074736-033/replay-stages.jsonl` 和相邻 failure 日志保留。随后4秒观测仍不推进，确认不是单纯启动延迟：导入返回路径包含混合斜杠，MediaPlayer URI自动规范化使路径比较失败，pending被清除但Play未执行。现统一入口/返回路径的 `Path.GetFullPath`，保留对真正播放进度推进的断言，完整复验通过。
- 对照旧 `--playback-smoke` 的 WAV 正常推进并 EOF；M4A 因既有 WMP NaturalDuration 整秒报告导致旧断言 FAIL（实际进度到1.70秒），不当作新回放功能通过；既有限制见 docs/43。
- 新字幕定位也不再用 WPF NaturalDuration 的整秒值否定已经验证的完整混音尾部时间；以混音实际帧数/时间界定可定位范围。播放器集成使用真实 ListBoxItem 路由点击事件和真实 MediaPlayer，播放器静音、未改系统音量；**没有物理鼠标自动化、外部播放器或可听输出逐样本对照**。
- M2 的双设备两小时同步、干净 Windows、真实平台/浏览器隔离及暂缓现场检查保持原状态。M3 中文/混合语言、专业词、噪声幻觉、长会议以及真实录音同时识别尚未验收。没有用短公开英文样本替代这些检查。
