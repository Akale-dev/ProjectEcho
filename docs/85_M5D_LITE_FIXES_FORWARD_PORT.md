# 85｜M5D Pro：同步Lite录音与识别修复

2026-10-05。用户明确要求现在将M3Hfix之后至M3J的Lite更新同步到M5，并命名M5D。该指令覆盖原等待验收的同步门槛，但不等于未测的目标机器或M4/M5质量闸门已通过。Lite继续独立保留，M6不启动。

## 基线与范围

主目录是实际M5C源码，含此前未提交的M3Hfix/M4/M5实现；Git HEAD仍为M3H，不能把HEAD当作M5C代码。修改前保存481份源码/脚本/文档前像、文件SHA清单、原跟踪差异及Git状态于`.local/M5D/pre-sync*`，原M5C ZIP及Lite工作树与M3J ZIP另有hash记录。没有reset、clean或覆盖旧版本。

M5C本已包含M3Hfix暂停排除、原始时钟与恢复映射，因此不重复覆盖这部分；先核查Root/Lite差异再迁入后续修复。

## 已迁入实现

- 每路Start前既有1MiB有界预分配，reported BufferSize不再误限合法包；原始帧、QPC/device、截止前缀、ReleaseBuffer和清零保持。
- M1Recovery只在首次chunk验证路径、打开前继续复查；后续每包严格路径/segment相等，PCM/整文件hash与全部时钟校验不删。保留30秒块。
- 混音索引/PCM/文件/音量/写盘/复核真实阶段百分比和一次性耗时记录。进度不逐次写Log，迟到/跨会/取消隔离。
- 精确投影正常QPC能力和开发验收说明，保留真正故障与原始Complete/StorageComplete/日志。
- Whisper零时长与逐段正向越界：完整本段词证据可恢复，否则untimed保全所有文字。覆盖倒数两段、多个尾段、中间异常和10001段；不整体平移或扩大旧250ms容差。普通导入与结构错误仍严格。
- 品牌整体左移9px、侧栏246/字号不变；会议自身时长；录音页最下方独立绑定本次录音的处理进度和结果；回听明确提示无时间文字。

## Pro特有合并与保护

没有整文件覆盖Pro转写服务。保留Qwen ImportAligned、Whisper输入帧数/混音hash/timelinePolicy、原始token evidence及Load验证。新增降级不能悄悄丢掉人物层所需的可追溯证据。

SpeakerAlignmentReader同步新Normalize签名，同时显式排除untimed及超真实EOF的词界，修复nullable比较可能让无时间段流出词界的问题；正文和已有人工稿保持。人物Mapper、原生聚类worker、模型、显示改名/文字编辑及纠正操作不改。

保留App中的YieldQwenToRecording、四个转写工作页、人物标签、草稿、回放版本选择和所有M4/M5入口。16个Pro专属App文件及11个Qwen/合同文件与前像hash一致；详见各同步审查记录。

## 验证准备与已知限制

- M5C前像实际Release构建0警告0错误，355默认回归PASS，证据`.local/M5D/baseline-*`及`pre-sync/.local/regression/`。
- M5D整合构建0警告0错误，396项默认自动回归PASS，其中4项为新增Pro音频/token/人物/Qwen绑定检查；证据`.local/M5D/regression-01/results.json`。
- 后续实际WPF、公开音频的原生说话人/Whisper组合链、包内依赖/中文解压/运行时与Lite保留均已完成，结果见下节。
- 用户随后明确提供并授权使用`H:\AI\Qwen`中的已有Qwen/ForcedAligner GGUF。本轮只读模型，测试输出仍在ProjectEcho，未下载或安装；真实CPU/Vulkan短链已完成。
- 已有M5自动人数、重叠/相似声音、自然长会质量失败与未测状态不升级。原生worker和模型仍为M5C原件，不宣称聚类算法改进。
- 没有授权新录音、mic、入会、上传、推送或关机；首选公开/合成测试，本轮不再处理用户私有会议音频。

## 最终包验证结果

实际环境：Windows 11 25H2 **26200.9457 / x64**，SDK **10.0.401**，随包运行时 **10.0.12**，NAudio **3.1.0**。本轮来源为文件导入、公开真人音频和合成文件，未启动硬件录音或麦克风。证据`.local/M5D/environment.json`。

| 检查 | 实际结果 | 证据 |
|---|---|---|
| M5C前像与M5D构建 | 两者Release均0警告0错误；前像355项通过 | `.local/M5D/baseline-build.log`、`baseline-tests.log`、`build-01.log` |
| 整合与最终包自动回归 | **396通过、0失败**，含37项Lite增量与4项Pro兼容增量；最终包DLL替换测试运行目录后复验 | `.local/M5D/regression-01/results.json`、`bundled-regression/results.json` |
| 最终包实际WPF | **13个入口全部PASS**，包含M5D布局/时长/录后进度、诊断与untimed、原M5B/人物/Qwen/暂停/设置/回放 | `.local/M5/m5d-portable-ui/summary.json` |
| 标识与录音页视觉 | 三语言均BrandLeft=5、VersionRight=230，侧栏246；100%/200%实际WPF渲染；简中100%截图已人工查看，版本与40%录后进度可见 | `.local/M5D/ui-141337160/report.json`及同目录PNG；实际OS DPI切换未做 |
| 自包含与中文目录 | 804项manifest加清单共**805个ZIP文件**逐项hash通过；中文路径App/Host在无效外部运行时根下成功启动 | `.local/M5/m5d-zip-verification.json`、`.local/diagnostics/portable-20261005-221334/portable-smoke.json` |
| Qwen真实CPU | 0.6B Q8_0＋ForcedAligner Q8_0；公开FLEURS中文短样本，实际CPU识别/CPU定位、保存/回听定位/导出/切会通过，UI全链18170ms、1句 | `.local/M4/real-ui-141708556/report.json` |
| Qwen真实Vulkan | 同模型，明确合成中英样本；实际Vulkan0识别/定位通过，UI全链9423ms、7句 | `.local/M4/real-ui-141730257/report.json` |
| 实际说话人＋Whisper | 公开四人样本CPU自动/已知均4组；实际Whisper证据绑定、应用已存人物分析、取消/切会/人工版本保留通过 | `.local/M5/real-ui-141743271/report.json` |
| 说话人完整短链 | 65秒A-B-A-C-A公开真人构造轨＋明确合成字幕；已知3组、返回身份、改名/指定/拆合、全文与导出/SQLite重建、原稿/人工稿/取消隔离通过，20143ms | `.local/M5D/speaker-short-final/summary.json` |
| 一小时混音性能 | 同一3600秒合成原轨，172800000帧；M3Hfix **439393ms → M5D 32739ms**，完整WAV hash逐字节相同、123个原件文件保持 | `.local/M3I/benchmark-old.json`、`benchmark-m5d.json` |

一小时测试仍使用30秒chunk、10ms包；提速来自减少重复路径检查，不通过加大录音块或跳过SHA/时钟校验实现。百分比来自每阶段实际处理的字节/帧，不是预计等待时间。各阶段重新计百分比，结束记录一次总耗时。WAV SHA-256为`b7cb3580dc7f8c846cbd6defcf4a06834a44650a252a912e6295fe2b989ec861`。这是合成文件性能测试，不等于真实一小时采集或双轨声学同步已验收。

Qwen模型使用用户指定的`H:\AI\Qwen\models\Qwen3-ASR-0.6B-Q8_0.gguf`和`H:\AI\Qwen\Qwen3-ForcedAligner-0.6B-GGUF\qwen3-forced-aligner-0.6b-q8_0.gguf`，前后SHA一致；不复制进发行包。CPU公开样本仍可见既有“铯→铀”错词，真实流程通过不代表文字准确率通过。1.7B未测。Qwen和说话人原生程序/模型/许可等162个依赖文件保持M5C hash，M5C聚类worker仍为`70587ae8…`，没有改算法或宣称质量提高。

WPF设置测试的独立合成CLI子进程使用项目SDK；这不用于证明用户程序无需SDK。自包含验证由独立App/Host检查和其他WPF入口以无效外部运行时根完成。本机测试不能替代真正干净Windows。

## 独立复审、保留与可追溯性

独立复审Core ASR/Diarization未发现新增阻断项：Pro原始token/hash/帧数/时间轴绑定、原稿和人物稿写入路径保持；untimed文字不获得推测时码/自动人物切点。20个应不动的核心服务/合同文件与前像一致。App另外保留16个Pro专属文件和既有资源；具体清单见`.local/M5D/app-port-review.json`、`capture-export-sync.json`及最终保留报告。

- 51个源码/测试/脚本迁移文件的前后hash：`.local/M5D/forward-port-files.json`；完整实现输入hash：`source-manifest.json`。
- M5C前像481文件继续保存；Lite跟踪的403文件未改，Lite HEAD仍`74b0392e5c3bc7494890a8db6d0860779eadd47e`。
- 旧M5C、M3I和M3J ZIP及发行目录未被替换；最终核对见`.local/M5D/preservation-final.json`。
- 最终包、实际UI/模型执行目录、自动测试和混音测试所用生产组件的hash一致性见`.local/M5D/tested-components-final.json`。
- 本轮没有提交或推送。Root原Git HEAD `dc26ea8…`仅是历史M3H，不冒充M5D源码提交；以实际前像、迁移文件及最终source manifest追溯当前未提交实现。

前像构建的无RID restore曾重写6份packages.lock.json；保存构建时实际锁文件到`baseline-restored-lockfiles/`后，从仍与初始SHA完全一致的Root锁文件恢复前像，481份原始前像最终逐项一致。过程记录`preimage-lock-restore.json`，无产品代码变更。最终`git diff --check`通过，验收表T197–T205无重复ID。

首次性能探针传入不受该旧探针允许的报告路径，在处理前被拒绝；改用项目内`.local/M3I/benchmark-m5d.json`后通过。失败日志`one-hour-mix.log`保留，成功为`one-hour-mix-02.log`；没有修改验证门槛。

## 发行与使用

交付：[`ProjectEcho-win-x64-20261005-M5D-Pro-Final.zip`](../artifacts/ProjectEcho-win-x64-20261005-M5D-Pro-Final.zip)，**461903400字节**。

- ZIP SHA-256：`aea31819bd31cf601c3a484ec5972a9df3bcf2fe3a1764ef69aeaa55bf4ddb4e`
- Manifest SHA-256：`d8f3026e518ad11676f9efc6264b1c2521d439db8644ba4d63ae1fd65d425a4c`
- 核心DLL SHA-256：`33eeea3995a07b999b52d22bceee53ca6ca92670fa7cf0bbc1a87b6b810b4fcb`

完整解压后运行`ProjectEcho.exe`，显示`Ver. M5D Pro`，选择原来的Data。保留已有Whisper/Qwen/人物配置与人工稿；外部Qwen模型仍指向现有目录。显式配置为旧包绝对程序路径的项目不会自动改写，应在设置中选择新包程序或随包预设。无模型也能录音、回听与导出。

封包后没有重写ZIP。包内本说明是封包前快照，本文件及同名`.package.json`补充最终验证结果。**M5D同步状态为APPLIED并完成本机交付；目标Win10新版录音/长会议/ASR仍待用户复测，clean Windows与旧M1/M2/M4/M5质量缺口不升级。** 无阻塞交付事项，不进入M6。
