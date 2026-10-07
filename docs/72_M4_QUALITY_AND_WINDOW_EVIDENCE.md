# 72｜M4 分窗合同、固定识别质量与 VAD 候选实测

日期：2026-10-04（Asia/Hong_Kong）。此文仅记录下列实际范围；M4 整体验收与交付见 STATUS 和主实施报告。没有启动录音、启用麦克风、回放声音或上传任何资料。公开文本标注仅下载到本项目，Qwen 大模型不写入交付目录。

## 1. 环境与输入依据

Windows 11 专业版 25H2，版本 10.0.26200.9457（`.local/M4/environment.json`），.NET SDK 10.0.401 / runtime 10.0.12。AMD Ryzen 7 5800X3D，32 GiB 物理内存，AMD Radeon RX 6900 XT，驱动 32.0.21043.10005。CPU/Vulkan 引擎、实际设备及构建证据见 `.local/M4/runtime/`。

- Qwen3-ASR-0.6B-Q8_0 模型：`H:\AI\Qwen\models\Qwen3-ASR-0.6B-Q8_0.gguf`；SHA-256 `f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8`。
- transcribe.cpp 候选 commit `077110eb00ca6880e13bef4ddebf766b3b76896c`。本次质量测试 CPU EXE SHA-256 `41a849f1b1842f5ff69e086edaf175786bd9ccd081d4863ed814bb7d7d58e439`；Vulkan EXE `a9154d3a045222229f272c3957a270ebddf8f200d14d18356124a2527ca539d4`。实际日志分别报告 `CPU` 与 `Vulkan0`，Vulkan 枚举 RX 6900 XT。后续构建不继承这两个文件的测试结果。
- Whisper 对照为既有 b5130 Vulkan CLI，现有多语言 base 与 large-v3；实际 EXE/模型哈希及参数在逐项 `.result.json` 内。
- 自然中文来自 transcribe.cpp 自带 `zh-short.wav`（FLEURS ID `17639585860488007329.wav`，11 秒）与 `zh-long.wav`（`5655534691025010514.wav`，31.12 秒）。[google/fleurs 官方 test.tsv](https://huggingface.co/datasets/google/fleurs/blob/main/data/cmn_hans_cn/test.tsv)独立提供参考文字，CC-BY-4.0；下载 SHA-256 `5734461648f816181d7dab5fc79204b18c4b9bc2cd5138225b25c72d18385d21`。不以任何 ASR 输出反推参考稿。
- 中文/中英金融边界由预先固定文字及已安装的 Microsoft Huihui / Zira SAPI 声音写入 16 kHz mono PCM16 文件。另有固定种子的 12 dB 全文件 SNR 噪声副本、5 秒数字零及无语音随机噪声。它们均为合成语料，不能替代自然金融会议。

固定原文、数字/单位/否定/方向检查式、文件帧数和哈希见 `.local/M4/quality/fixed-03/fixtures.json`。质量结论冻结为有限样本比较；未设定代表性自然会议通过门槛，不凭本轮样本宣布某引擎普遍更准确。

## 2. 已实现的分窗及句子映射合同

`QwenWindowPlan` 以 16 kHz 整数帧建立半开区间，完整输入每帧的 ownership 恰好一次。上下文读取范围与 ownership 分开记录，不把重复上下文当成可自动删字的授权；时间轴策略原样保留。分窗本身没有句子定位能力。

`QwenWindowAudio` 对完整 WAV/RF64 进行哈希/格式/帧数核验，以读锁维持输入不可写，64 KiB 缓冲顺序裁出 PCM16 WAV；不重采样、不删静音、不改变暂停政策。尾窗不足整秒/整毫秒仍保留全部帧。新输出不覆盖现存路径；失败 partial 留在该次尝试。

`QwenWindowAlignment` 只接受 `forced-alignment` 或 `model-native` 证据，核对本窗口音频及 Qwen 原文哈希、程序/模型哈希与版本。句子 UTF-16 范围必须完整覆盖全部非空白原文，不切代理对，不接受重叠/越界/缺失边界。窗口内真实帧加一次 `InputStartFrame` 后成为完整录音时间，显示毫秒采用起点向下、终点向上取整，仍为 `approximate`。不得输入 VAD/窗口时间、按字符均分的时间或另一份 Whisper 原文的时码。

上下文句按 `Inside / ContextOnly / CrossesBoundary` 保留，不剪文字、不静默去重。覆盖闸门保留失败、取消、待定位及未解决的上下文；这些状态不能变成整场完成。`DigitalSilence` 仅表示实际 PCM 全零；`ModelNoText` 必须保留空文本 SHA-256，只表示模型处理后没有返回文字，不证明该区域无人说话。

八组独立合成回归覆盖帧计划/尾帧/哈希/RF64/取消重试/真实证据绑定/字符与时间缺口/上下文以及完成闸门。测试项目曾单独构建成功，零警告零错误；最终统一默认回归数量由主报告记录，不在此重复推定。

## 3. 三小时流式合成验证

通过程序测试入口 `--m4-window-long-probe .local/M4/windows-3h-01` 实际执行。不是模型识别、不是三小时自然会议或设备采集。

- 完整 PCM：172,800,017 帧 / 16 kHz，即 10,800.0010625 秒；按 30 秒分为 361 窗，最后 17 帧保留。
- 所有窗口输出逐 PCM 样本与源文件比对相同，覆盖帧数完全一致；首部、窗口接点、1 小时、2 小时及尾端哨兵均保留；源文件最终哈希不变。
- 实际流式写入/复核 6,231 ms；观测进程工作集约 90.8 MB；整个 probe 托管累计分配约 75.7 MB，非同时占用或模型峰值。生产读取缓冲固定 65,536 字节。
- 清单与所有窗口哈希：`.local/M4/windows-3h-01/synthetic-three-hours-summary.json`，明确 `isSynthetic=true / actualModelInference=false`。

真实模型三小时耗时、完整输出、峰值内存/显存由独立长会测试负责，此结果不能替代。

## 4. 固定质量对照

完整证据：`.local/M4/quality/fixed-03/quality-report.json`；可复现实验脚本：`scripts/Test-M4Quality.ps1`。以下为去标点、空白、小写与 NFKC 后的字符编辑率；**没有**把汉字/英文数字转换为阿拉伯数字，没有合并简繁或翻译。因此它衡量书写差异和识别差错的混合，不能直接当作语义错误率。

| 样本 | Qwen CPU | Qwen Vulkan | Whisper base Vulkan | Whisper large-v3 Vulkan |
|---|---:|---:|---:|---:|
| 自然中文短句 | 4.17% | 4.17% | 45.83% | 12.50% |
| 自然中文长句 | 0% | 0% | 25.33% | 98.67% |
| 合成中文金融 | 0% | 0% | 68.42% | 38.60% |
| 合成中英金融 | 56.96% | 56.96% | 64.56% | 56.96% |
| 合成中文金融 + 12 dB 噪声 | 0% | 0% | 77.19% | 43.86% |

Qwen 两硬件的这些结果文字一致。自然短句官方参考是“铯和碘”，Qwen 输出“铀和碘”，确有实质词错，不能记成全正确。自然长句的 Qwen 全文完整；Whisper large-v3 本次原始 CLI 返回无关广告话术，原始失败留存。

合成中英金融三种模型均可能把 “twelve point five percent / 三点二倍 / 二零二七年六月十五日” 写成 `12.5% / 3.2倍 / 2027年6月15日`。Qwen 与 large-v3 的 56.96% 主要来自这个已说明的书写形式差异：预先固定的 EBITDA、12.5%、否定20%、3.2倍及日期五项检查全部命中。检查式命中本身也不等于语义证明。中文金融的金额、单位、否定与从120扩大到150基点等五项，Qwen CPU/Vulkan全部命中；base缺四项，large-v3无噪声全部命中、加噪声缺基点方向词项（输出“克基点”）。

数字零和 noise-only：Qwen CPU/Vulkan、Whisper base均返回空文字；Whisper large-v3原始CLI分别返回 “Thank you.” 与 “Kansai International Airport”，记为已知无语音样本的幻觉失败。**这不是产品 Whisper 数字零保护失效**：本对照直接调用 CLI，特意不使用产品预先拦截数字零的逻辑；产品数字零路径继续单独验收。未用 VAD 给这些结果事后删字。

Qwen CPU整批七条耗时81,540ms，Vulkan4,454ms；进程峰值工作集采样分别约1.94GB/1.10GB。Whisper逐样本启动，总计base11,308ms、large-v3 44,553ms，模型加载次数不同，不能直接用总数宣布引擎速度比例。Qwen每条报告引用同一次batch运行，禁止把七行的batch耗时相加。以上是进程工作集，不是独立 GPU 显存测量。

## 5. Silero VAD 与全帧窗口对照

实际运行 audio.cpp v0.9.0 Windows原生CPU `audiocpp_cli --task vad --family silero_vad`，无 Python/Node。候选程序 SHA-256 `35271c7ec3bae6835fdd52a0f21771d6be357c0b37917f6eb86c5bff63758589`，其随源码 Silero 16k safetensors SHA-256 `c59271c284ae9c8335d795d60e0bfdb71aaaceec578d9bd9ffc1b8153c319ea1`，上游许可记录为 MIT。这里使用的是 VAD 路线，不据此证明同程序默认 forced-aligner 输出合同。

脚本 `scripts/Test-M4VadWindows.ps1` 对上述七个固定样本，以及明确标记的59.69秒串接测试语料，比较：

1. 不超过30秒的均衡完整窗口。
2. 在同样最长30秒、最短10秒约束内，优先选择附近不少于50ms的 Silero 非语音间隔中点。

两策略都处理完整原PCM，包括被VAD认为非语音的部分，裁片逐样本核对；没有丢弃非语音、压缩时间轴或将各语音窗口当句子。数字零/noise-only实际VAD输出空候选数组。上游空结果不写 `segments-out`，只在成功退出且明确 `vad-chunks-out=[]` 时识别为该次无检测；其它文件缺失仍失败。

自然长句：均衡切点15.560秒，Silero候选15.392秒；两者文本一样、完整CER0%。第一窗末尾模型补句号，不能因此证明真实自然句子已完整结束。59.69秒串接边界样本两策略选中同一29.845秒切点，输出文字均完整，CER25.12%主要为数字书写变化。其它短样本不需要切点，两策略相同。各VAD运行CPU数十至数百毫秒，原生结果/窗口计划/每个窗口文本及帧映射保留在 `.local/M4/vad/full-coverage-02/`。

**选择：本固定集没有证明 VAD 能改善本轮切窗结果，因此没有足够依据把 Silero 设为必装前提。** 保留完整覆盖的均衡窗口及 VAD 候选证据；句子定位继续依赖独立音文对齐。不能用“减少处理秒数”替代接缝/短插话/低声完整性的证据，也不将未检测区域自动视为无语音。

## 6. 失败与未测范围

- 首两次SAPI在沙箱内 `Speak` 返回 `0x80045040`；取得授权的正常本机只读语音访问后，仅向WAV合成成功，未改注册表/声音配置。`fixed-01/02`保留失败前产物，`fixed-03`为完整语料。
- VAD脚本首个运行在数字零未生成segments文件时明确停止，未产出假完成；按上游 `speech_segments.empty()` 的写文件行为和明确空chunks证据修复后，`full-coverage-02`完整通过；第一次产物保留。
- 自然中英切换/自然金融会议/电话音质/远场回声/短插话重叠以及代表性准确率仍 **NOT_RUN**。当前合成中英/金融/加噪声不能替代它们。
- 三小时真实模型识别、句子实际点击/回听、对齐声学精度、便携交付与干净Windows由其它M4证据负责，本报告不宣布这些已通过。
- 0.6B Q8_0候选已经测量；1.7B、其它量化、其它GPU/CPU和系统版本仍未由本次比较验收。不自动更换用户现有Whisper。
