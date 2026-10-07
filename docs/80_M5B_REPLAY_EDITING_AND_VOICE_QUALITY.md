# 80｜M5B：人物字幕回听、正文编辑与声音分组复查

2026-10-04。M5B在M5-IntervalFix上修订用户提出的四项问题；不推进M6。没有录音、启mic、播放私人音频或上传；实际播放测试使用生成的静音并静音输出。

## 三项界面修订

1. “回听”页选择带人物版本后，列表与当前播放字幕均显示本场人物标签，与“字幕回放与校订”一致。标签独立呈现，不写入正文。
2. 人物版本在“字幕回放与校订”可以直接修改正文，点击保存另存新人物版。人物ID/显示名称、时间、来源及父版本保留；草稿、跨会搜索结果、索引、重开与标签导出按实际版本处理。
3. “同场说话人”页单击一条字幕或按Enter可以定位回听，重复点击同一条也能回到其开头。普通选择、界面同步和播放自动跟随不会触发跳播。Recording/Paused/Stopping仍禁播。

## 人物正文新合同

首次改字写入人物稿schema2：保存父稿SHA、修改前后正文与明确的原文字来源组；原始转写及人物v1保持可读，不覆盖。改字后的新字符没有自动获得旧token时间。先改后切/先切后改仍由用户明确选择文字切点与已有声音候选时间，不按字长造时码；校字前后的时间为沿用近似回听位置。

打开新版校订结果需使用M5B；旧程序不理解schema2，但原稿和旧人物版本都保留。数据字段见[77](77_M5_DATA_CONTRACT.md)。

## 两人样本为什么失败

用户提供的约31.756秒英语对话原任务**已经设置人数2**。固定程序/音频/模型下，旧ERes2Net在2线程与4线程均把20.924秒主要声音归一组，另一组仅118ms。因此不是忘设人数、显示名串了或上次EOF问题；这里发生了声音特征/全场聚类的退化。声音分组并未按性别分类，也没有推断姓名。

已知人数模式使用`cutree_k`，聚类阈值不参与该分支；单改0.9不能修复这个已知人数案例。未知人数阈值0.9/0.7/0.5/0.3在此例分别产生4/5/8/12组，不能用“更多编号”冒充分辨正确。[锁定聚类源码](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/fast-clustering.cc)。官方文档的0.9只是针对特定公开例子的提示，不是普适质量保证。[模型文档](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html)。

### 对照结果与人工参照

保持同一输入SHA/分段模型/CPU/人数2，仅换NVIDIA TitaNet-Large声音特征模型，得到11.222秒与9.820秒两组主要发言。它在此例改善，但不能仅凭“正好两组”宣布准确。

用户截图只明确标出了前7条。按这7条的主要区间覆盖、允许全场A/B置换，旧模型最多匹配5/7，TitaNet为7/7；旧模型只是给全部cue同一个多数组，所以简单重命名会得到虚高数字。时长优化置换下旧模型2/7、TitaNet7/7，两种指标在本地原报告分列，不混用。

原产品前7条为4条待确认、3条错归。TitaNet接入完整生产字幕映射后，进一步发现并修复：

- 118ms叠声导致一整条5秒声音区间标记重叠，错误波及不相交字幕；现在按当前cue里的真实跨人交集判断。
- Whisper BPE token可能在一个英文词或否定词内部切开；不能把这种边界当可靠换人点。自动拆分也不再生成不足300ms的确定人物碎段；这些情况保留全部文字待确认，人工纠正仍可用。
- “首个可靠声音”和人数退化提示按实际连续非重叠片段计算，不让一小段插话抹掉整段可用声音。

最终生产结果，截图7条为 **4条自动正确、0条确定错归、3条待确认**；余下7条没有用户人工真值，**NOT_VERIFIED**。完整14条原字幕/419字符映为15条人物字幕，全文保留，现有10个分析/人工稿JSON哈希保持。没有覆盖用户截图中的正确人工修订。该结果只证明局部改善，不是整场准确率或自然会议质量验收通过。

证据：`.local/M5B/quality-case/诊断报告.md`、`partial-gold-score.json`、`.local/M5B/production-case-02/report.json`。报告只保存必要局部时间、计数和hash；私人声音、正文、截图不进入包或Git。原会议日志在用户编辑期间变化，不能将它误报为代理写入；原音频和既有不可变版本保持。

## 模型与使用方式

M5B附NVIDIA TitaNet-Large和原ERes2Net，原生sherpa CPU程序不变。预设下拉可以明确切换“TitaNet-Large（M5B）”“ERes2Net（M5）”或自选文件；无既有配置且新模型存在时使用新预设，原会议/用户配置不静默替换。切换预设不触发下载或识别。

对本例建议明确选择TitaNet-Large和已知人数2后另存分析；原人工稿继续独立可选。自动人数模式在此例仍为4而应2，**FAIL保留**。未知人数、短插话和重叠仍需回听核对；无需建立跨会身份库，也不要将remote/mic当人物。

NVIDIA模型卡固定commit `0dc382f40121a5fbd34db10a2bb04d826c2be6a8`，模型CC-BY-4.0，已附完整许可、归属、ONNX转换说明及源许可；不是把程序Apache许可套给模型。ONNX大小101405493B，SHA-256 `d51abcf31717ef28162f26acb9d44dd4127c3d44c9b8624f699f3425daca8e77`。[官方模型卡](https://huggingface.co/nvidia/speakerverification_en_titanet_large/tree/0dc382f40121a5fbd34db10a2bb04d826c2be6a8)。

`Prepare-M5BModels.ps1` 默认离线校验，显式`-Download`才取公开依赖；生产程序不下载。29文件overlay两次重新生成逐文件hash一致，证据`.local/M5B/sherpa-bundle-02-repro-check.json`。

## 验证与未测

- Windows11 25H2 build26200.9457/x64，5800X3D、SDK10.0.401/runtime10.0.12；Release零警告零错误，当前351默认回归PASS，含编辑、旧版/父链篡改、SQLite、取消、局部重叠及词内拒绝自动切分。
- 实际WPF三语、回听标签、人物正文保存/草稿/跨会归属/回听/索引/导出/重开、新旧模型配置保护通过；新增`--m5b-ui-smoke`与旧M5烟测分别执行。
- TitaNet公开4人原音auto/known均4；A-B-A-C-A auto/known与20分钟返回known模式均3组、主要身份拆0/误合0。20分钟实跑38.205秒/峰363216896B。原冻结裁片覆盖90%门槛仍未达，失败保留。
- TitaNet三小时资源、跨语言同人、自然会议全面准确率、其他机器及真正干净Windows均NOT_RUN；不继承ERes2Net三小时性能。

最终完整包、中文解压逐文件hash、自带Host及包内复测在封包后追加到STATUS和同名`.package.json`。旧M5/M5-IntervalFix、用户Data与人工版本保留，未推送远端。

### 封包后结果

`artifacts/ProjectEcho-win-x64-20261004-M5B-Final.zip`，453020470B，SHA-256 `8e55724d5268e472a7cb68b41975e9a1aba0a0394f4a1773fe025b7829776dd8`。782项清单+manifest共783文件实际解压hash通过，manifest SHA `c7d71caccc6f670826c92998618e306ea8c3b1ab023f3b215fdaa1e432ffef68`。中文最终目录`artifacts/E80/最终M5B`的App/Host自带运行时及M5B实际WPF通过；11项候选WPF通过，623程序/模型文件与最终包一致。

最终包内Core/原生TitaNet再次对用户样本在隔离副本运行：7条人工参照仍4匹配/0确定错归/3待确认，14→15段且419字符完整，原有10个人工/分析JSON保持。其余未标注7条仍NOT_VERIFIED，没有改写用户正确人工版。最终Core SHA `b3646c5e66b3a2b05ef782616ad6b1139bbab022ca57c14bd8f503fbec012c2d`与351回归和包内探针相同。

证据`.local/M5/m5b-final-zip-verified.json`、`m5b-final-ui/summary.json`、`.local/M5B/ui-091525394/report.json`、`bundled-case-final/report.json`、`final-components.json`、`.local/diagnostics/portable-20261004-171522/portable-smoke.json`。三语界面、真实静音播放器位置、编辑草稿及已保存版本、预设/旧配置保持均由实际控件测试。正常使用无需SDK/.NET/Python/Node预装；真正干净Windows仍NOT_RUN。
