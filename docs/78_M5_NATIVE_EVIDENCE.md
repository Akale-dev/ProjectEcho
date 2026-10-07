# 78｜M5 原生 CPU 说话人模型、资源与质量证据

日期：2026-10-04。本轮明确启动的是同场说话人 M5。这里记录已执行的原生模型测试及局限；产品字幕、人工修订、索引、实际 WPF 与最终包证据另见 M5 总交付报告和 STATUS。没有开启录音、麦克风、会议或上传。

## 真实基线与所选路线

M4-AlignFix 已交付 Qwen CPU/Vulkan、原生音文定位与完整帧窗口，Whisper 仍是默认。内部定位约 80ms 粒度、时间为 approximate；不是逐字声学真值。旧 elapsed 长暂停样本的 GPU raw 反序失败和显式 CPU 成功并存；自然金融/电话/重叠/自然三小时会议、1.7B、物理旧 CPU、其它硬件及 clean Windows 未验。M5 不提升这些结果。

本轮使用 **sherpa-onnx 1.13.8 / commit 11afbd00 + ONNX Runtime 1.28.2，Windows x64 shared-MT/no-tts CPU**。实际 `sherpa-onnx-version.exe` 输出和 release 元数据分别保存在 `.local/M5/provenance/version.txt`、`release.json`。上游 release 发布于 2026-09-10。选择独立原生 CLI 作为 worker，没有在 WPF 进程内装载 ONNX，也没有增加 Python、Node、网络服务或通用 LLM。

官方证据：

- [1.13.8 发布及 Windows 资产](https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8)。
- [官方分段/embedding 组合和自动、已知人数说明](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html)。
- [锁定 CLI 源码](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/sherpa-onnx-offline-speaker-diarization.cc)。
- [全场管线源码](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/offline-speaker-diarization-pyannote-impl.h)先计算各窗分段及 embedding，再调用一次整场 `Cluster`；最后把窗口内声源映射回同场聚类。不是每次换人递增编号，也没有把各窗 A/B/C 直接拼接。
- [聚类实现](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/fast-clustering.cc)为归一化 embedding 的余弦距离、complete-link 层次聚类；未知人数按距离阈值，已知人数按 cutree_k。距离不是身份正确率。

选用 pyannote segmentation 3.0 `model.onnx` 与 3D-Speaker ERes2Net base zh-cn 16k ONNX。segmentation window shift ratio 固定 0.1；两模型强制 `provider=cpu`，各 2 线程；自动 threshold 固定 0.9，已知人数单独运行。`min-duration-on=0`、`min-duration-off=0`：不沿用默认丢弃 <300ms 片段，也不先用外部 VAD 删除待分析音频。模型自身仍可能漏检。

## 程序、模型与许可

| 文件 | SHA-256 / 许可 |
|---|---|
| 官方运行时归档 | `4b0a94f7b5c606b1b64a19a831c2127559e4b3d34e195465ebc7be73d9ed4783`，与 GitHub asset digest 一致 |
| 官方原 CLI | `acc7dd36a3a3e14d396b54a41192f4a7db4df3883d0bc5fa68f7d2851ac83066` |
| 便携 UTF-8 CLI | `0843e7b90ed405aa7a5aa3a8ab0357ddca9b937ff956433bb88ff25044121698` |
| pyannote ONNX | `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079`；下载归档自带 MIT LICENSE |
| ERes2Net ONNX | `1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b`；[原模型卡](https://modelscope.cn/models/iic/speech_eres2net_base_sv_zh-cn_3dspeaker_16k)明确 Apache License 2.0 |

模型源为 sherpa 官方模型 release 的公开转换件；没有代用户接受 gated 条款或使用私有账号。[3D-Speaker 仓库](https://github.com/modelscope/3D-Speaker)也提供 Apache-2.0 许可。pyannote 转换归档内 MIT 文件与原模型来源一并保留，不把 sherpa 自身 Apache 许可代替模型许可。

便携 overlay 为 `.local/M5/sherpa-bundle-03/`，仅含：

- `Tools/Sherpa/`：CLI、`onnxruntime.dll`、`onnxruntime_providers_shared.dll`。
- `Models/Sherpa/`：`pyannote-segmentation-3.0.onnx`、`3dspeaker-eres2net.onnx`。
- `LICENSES/Sherpa/`：sherpa Apache-2.0、ONNX Runtime MIT/完整 ThirdPartyNotices、pyannote MIT、3D-Speaker Apache-2.0/模型卡、hclust-cpp BSD、kaldi-native-fbank Apache-2.0、kissfft BSD-3-Clause/COPYING、Eigen MPL-2.0、nlohmann/json 3.12.0 MIT、所改 manifest。
- `bundled-sherpa.json` 与 `THIRD_PARTY.sherpa.json`：版本、路径、原/修改 exe、模型及实际打包文件哈希。

版本来源分别为 hclust-cpp `2026-02-25`、kaldi-native-fbank `1.22.3`、kissfft `febd4caeed32e33ad8b2e0bb5ea77542c40f18ec`、Eigen `5.0.1`、json `3.12.0`。锁定 sherpa 和 fbank 的 CMake 文件已保存在本地 provenance 中；没有只凭最新主分支猜依赖版本。

实际 objdump：CLI 直接依赖 ONNX Runtime 和 KERNEL32；ONNX DLL 的剩余导入为 Windows 系统 DLL，没有 `VCRUNTIME`/`MSVCP` 预装依赖。两模型合计约 45MB；原生 exe/DLL 合计约 18.6MB。公开测试音频不分发到产品。

官方原 CLI 的中文 WAV 路径真实失败，argv 中变成 `????`，见 `.local/M5/中文路径/stderr.txt`。只给复制后的 PE 增加进程内 UTF-8 activeCodePage、longPathAware、asInvoker manifest 后，同一中文 WAV CPU 推理通过，见 `fixed.stdout.txt`/`fixed.stderr.txt`。没有改系统区域、注册表、PATH 或原始下载件。重复准备 overlay 的修改 exe 哈希相同。

## 适配合同与故障隔离

`SherpaDiarizationRuntime` 使用一整份已保存、hash 绑定、16k mono PCM16 音频。首版上限三小时；保存实际 `file-v1` / `elapsed-v1` / `pause-excluded-v1`，不删静音或再加暂停偏移。原文件、exe、两模型及同目录 DLL 在推理期间持只读锁并记 hash；仅允许上述已验证 exe 的两个 SHA，不能把未知程序声明为 1.13.8。

原生进程 BelowNormal，取消 kill 其独立进程树；原 stdout/stderr、尝试状态、CPU/内存、参数和 hash 保留。真实进度是 embedding 的 chunk 百分比，不能当整个任务匀速进度。每次成功独立保存分析；失败或取消不返回可用 Analysis、不发布完成版本。无跨会 embedding 库，特征留在原生进程生命周期内。

原生区间按固定三位小数格式解析；不接受反序、负时刻、零时长、越过原输入尾部或格式错误，不静默裁掉越界。全场数值聚类 ID 映射为随机稳定 speakerId，并在 attempt 中保存 `rawClusterToStableSpeakerId`。A/B/C 按各组首次 ≥300ms 且非模型重叠区间的出现顺序分配；无此类区间的组放后，全部 uncertain。该“可用时长”规则不是正确率证明。短区间保留为 uncertain，模型同时返回不同组重叠时才设 overlap；不造独立两份文字。

重叠检测使用两个线性扫描，每 256 区间检查取消，避免最多 50000 段时进行两两比较。独立单测用随机小集与穷举真值对照，并检查 50000 个区间与取消。

上游 ≤10 秒特殊路径不运行 embedding/global clustering；适配器只对这一情况产生尾部补零至 11 秒的独立推理副本，记录原帧数、补帧数与执行副本 hash。原音频和发布的时间轴不变。`.local/M5/boundary-real-01/summary.json` 中 **9.999 / 10 / 10.001 秒真实生产适配器全部 PASS**，补帧分别 16016 / 16000 / 0，源 hash 保持、结果无越界。

## 真实声音与固定质量门槛

来源是官方公开 `0-four-speakers-zh.wav`，原长 56.861 秒。原声天然多人，受控测试把其中各人的相同片段按 A-B-A-C-A 排列，以“同一段真实声音必须回到同一组”为直接真值；没有用合成文字代替模型运行。长间隔/噪声/电话带宽/回声/重叠均为明确构造变换，不能冒充自然会议质量。未主动播放或采集用户音频。

在运行前固定未知人数阈值 0.9、目标 3 组、同人拆组 0、不同人主要归属合并 0、全裁片未覆盖 ≤20%。首轮基础样本额外要求每裁片主要分组覆盖 ≥90%。稳健性样本固定已分配区间标签竞争 ≤5%。结果出来后没有为通过而改阈值或人数。

评分有两次必须保留的纠正：

1. 初版 `Pass` 布尔漏用已冻结的 90% 最小覆盖条件，另存 `quality-gate-correction.json`。
2. PowerShell `Math.Max(0, double)` 选择整数重载，错误四舍五入了区间交集；从相同原始 stdout 以显式 double 重新评分，另存 **`scores-corrected.json`**。原 summary/json/stdout 没有覆盖。最终只引用此修正评分。

全裁片范围包含原声内部停顿；“未覆盖裁片比例”不能当漏语音率/DER。标签竞争是该已知人物裁片范围内与主要聚类不同的预测时长；构造重叠裁片也计入，不能当自然会议 DER。字幕错归率由独立产品字幕探针统计，纯 runtime 不伪造 ASR 字幕来声称识别正确。

| 样本（每项 auto / known 独立执行） | 组数 / 拆人 / 主要误合 | 片区未覆盖 | 已分配标签竞争 | 固定门槛 |
|---|---:|---:|---:|---|
| A-B-A-C-A，65 秒 | 3 / 0 / 0 | 7.105% | 0 | 分组返回通过；最小裁片覆盖88.914% <90%，质量门槛 FAIL |
| 20 分钟间隔，A 最后半音量返回 | 3 / 0 / 0 | 7.029% | 0 | 分组返回通过；同一90%覆盖门槛 FAIL |
| 8k采样/8bit量化/带宽限制的电话近似 | 3 / 0 / 0 | 6.992% | 0 | 构造稳健性门槛 PASS；不代表真实电话编解码 |
| 固定种子10dB白噪声 | 3 / 0 / 0 | 1.665% | 0 | 构造门槛 PASS |
| 120ms、0.45增益回声 | 3 / 0 / 0 | 1.481% | 4.711% | 构造门槛 PASS，错误仍保留 |
| 220ms B 插话 | 3 / 0 / 0 | 6.557%（全样本） | 0 | 构造门槛 PASS；短片自身覆盖80.909%，产品应待确认 |
| 2.4秒 A+B 叠声 | 3 / 1 / 0 | 6.156% | 9.724% | auto/known 均 FAIL；不能声称已解决叠声 |

对应 `.local/M5/native-real-02/scores-corrected.json` 和 `.local/M5/robustness-01/scores-corrected.json`。原官方56.861秒样本直接 CLI auto0.9得到4组；实际产品导入→48k混音→16k副本的 WPF 自动运行得到5组，**人数质量失败保留**，见 `.local/M5/real-ui-061553749/`。输入发生重采样与模型结果不同可以观察到，但没有证据把它唯一归因于某个处理步骤。已知4人提示是另一项测试，不替代自动人数失败。

相似音色的独立说话人真值、同人跨语言、自然重叠会议、真实电话/声学回声及自然三小时会议质量仍 **NOT_RUN**。不靠将所有字幕标未知通过；模型失败和后续人工纠正分开报告。

## 三小时资源和尾部

`.local/M5/native-real-02/three-hours-auto.json`：完整 **10800 秒、172800000 帧** WAV，361 个真实声音裁片，包含明确尾部 10796–10799.5 秒 B；没有跳过 VAD 外区间，整个音频一次交给原生模型。裁片实际时长约1724.3秒，其余为构造静音，因此不是连续三小时讲话负载或自然会议准确率。

- 自动全场 3 组；片段主要身份拆0、误合0、已分配标签竞争0，尾部 B 返回一致。
- wall **530152ms（8分50.152秒）**；原生峰工作集 **1,462,239,232B**，每200ms采样 OS lifetime peak；原exe/hash、原stdout/stderr和父脚本保留。
- 修正后的全裁片未覆盖7.260%；最小主要组覆盖88.886%，仍低于预冻结90%门槛。分组返回/资源执行与声学质量门槛分开。
- CPU Ryzen 7 5800X3D，本机 Windows 11 25H2 26200.9457/x64；共享开发机有其它工作，耗时不作目标硬件保证。没有 GPU 推理或继承 Qwen/Whisper Vulkan结论。

父任务另冻结生产适配器/新UTF8 exe 执行三小时完整版本、导出、索引链，`.local/M5/pipeline-three-hours-01/summary.json`已PASS：推理565365ms，峰1462222848B，整个探针643983ms。最终托管代码重读同一全场raw与361段结果也通过，见75。三小时密集讲话/更多人物可能使全场距离矩阵显著增大，当前数据不能替代其资源验收。

## 复现入口与未测

开发者显式依赖配置：`scripts/Setup-M5SherpaDependencies.ps1 -Download`。来源/hash 锁在 `scripts/m5-sherpa-sources.json`；不加 `-Download` 只校验已有文件。此校验已实际执行 PASS；从空目录全量脚本下载流程未重跑，最初下载是本轮逐项官方获取。生产程序没有下载入口或录音时自动下载。

随后 `scripts/Prepare-M5SherpaBundle.ps1 -OutputDirectory <新的项目内目录>` 准备overlay，不覆盖已有包。实际执行了03版，并保留前两版作为许可补强前的历史。

模型测试：`Test-M5NativeRuntime.ps1 -LongRun`、`Test-M5Robustness.ps1`。评分：`Evaluate-M5NativeEvidence.ps1 -EvidenceDirectory <目录>`，只写新的修正评分。边界入口 `ProjectEcho.Tests.dll --m5-runtime-boundary-probe <fixtures> <exe> <seg> <emb> <output>`，通过 `scripts/Dotnet.ps1 -Arguments @(...)` 调用。

真正干净 Windows、旧 CPU/其它 Windows/GPU、自然质量项目均不由这些测试升级。旧 M1/M2 录音/mic/同步/隔离缺口保持。没有访问或改动外部 `H:\AI\Qwen`，没有上传、远端推送或启动 M6。
