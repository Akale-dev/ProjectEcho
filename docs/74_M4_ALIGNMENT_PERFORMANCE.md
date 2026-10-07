# 74｜M4 句子定位性能与官方 Python 对照

2026-10-04。用户授权在 M4 Final 上改进定位速度，并单独评估官方 Python `Qwen3-ForcedAligner-0.6B`。本文记录性能增量，不改变 M4 中文 ASR 的范围，不实施 M5/M6/M7。

## 状态

实现、目标机三小时复测、官方Python对照与同版候选包功能验证已完成。交付名称 `ProjectEcho-win-x64-20261004-M4-AlignFix`；旧 `M4-Final` 目录和ZIP保留。本文为最终封包前验证快照；最终ZIP逐文件核验、解压后结果及SHA以同名 `.package.json` 和 `STATUS.md` 的封包后记录为准。

## 已实现

- 原生 CPU 定位增加明确的 AVX2/FMA/F16C/SSE4.2/BMI2 编译，保留通用 CPU 版。只有随包的已知程序路径参与选择；自定义程序路径保留。随包 CPU 选择检查实际指令集，不在不兼容机器上自动选优化版。
- 用户追加的“兼容旧 CPU（关闭 AVX2/FMA 等加速）”保留所选方案/路径，实际 CPU 定位强制使用随包通用原生程序，避开优化版及 PyTorch。缺少相应指令集时也采用通用 CPU 路线。现有 ASR CPU 构建已核对为 x64/SSE2 基线，SSE4.2/AVX/AVX2/AVX512/FMA/F16C/BMI2 均关闭；不需要替换 ASR 程序。不能承诺32位CPU或没有SSE2的架构；旧CPU实机尚未测试。
- 原生 Vulkan 定位独立于 Qwen 识别的硬件选项。实际 backend/device 来自已初始化的 ggml 句柄；指定 Vulkan 而运行 CPU 会失败，不记为 GPU 成功。
- 新 `batch-v2` 能力版本每批最多 16 个 30 秒窗口，共享模型会话。每个请求仍有唯一 ID、完整原文、音频帧及哈希。每项成功落盘才报告完成；缺失、重复、未知 ID、非零退出、词文不匹配、非法时间或覆盖不足均不能保存完成稿。
- 仍使用 raw-v1 声学预测，不插值、不按字数分配时间、不借用 Whisper 文字时码。独立原文、人工稿和音频不覆盖；取消只结束本次拥有的进程，重试独立。
- 旧配置未包含定位硬件字段时为 CPU；旧 raw-v1 单窗定位程序仍可运行。新版设备证据、批次参数及实际资源写入 attempt；版本检查有独立 15 秒限时。
- 识别与定位分别选择 CPU/Vulkan；界面 GPU 编号用于二者。CPU/线程设置不改变当前可用 Whisper 引擎。Qwen/定位模型仍外置，Python 不成为正常录音、回听、导出的依赖。

## 可比较的基线与本机环境

Windows 11 Pro 25H2 **26200.9457 x64**；Ryzen 7 5800X3D（8 核16线程），32 GB；Radeon RX 6900 XT，驱动 **32.0.21043.10005**。开发 SDK **10.0.401**，运行时 **10.0.12**，NAudio **3.1.0**。

旧定位器是 audio.cpp v0.9.0 / `795c45fb` 的通用 CPU raw-v1 构建，显式关闭 AVX/AVX2/FMA/F16C/BMI2/SSE4.2。其 SHA-256 为 `eca956f4f15ae7c93ccc1b9cece89439233d735212f7ab35326753b96b86014f`。新 CPU/Vulkan 构建继续使用同一上游源及 raw-v1 补丁；记录精确编译参数、二进制哈希、系统 DLL 依赖和许可。

ASR 保持锁定 transcribe.cpp `077110eb00ca6880e13bef4ddebf766b3b76896c` / 0.6B Q8_0，不通过更换识别结果加速对齐。原生定位 GGUF SHA-256 `75209490b11cec2b0db749ca5f4ff92266f58efd30f7fd04d9eb2a3ac9cc929f`，1,129,966,496 B。

主速度对照使用旧三小时 `real-long-02` 中原来的三个完整 30 秒窗口（音频帧、文字逐项相同）；短公开/SAPI 样本与独立句界样本另列。固定清单 `.local/M4Perf/cpu/fixtures.json`。冷进程计时不等于清除 Windows 文件缓存。暖批复用会话，但不缓存或跳过推理。模型计时以排他锁串行，安装背景负载影响的初期短样本不作为主比值。

## 当前实测

| 同三段原 30 秒窗口 | 定位进程总耗时 | 范围 |
|---|---:|---|
| 旧通用 CPU，6线程，3次冷进程 | 60,034 ms | 同帧同文重跑 |
| AVX2 CPU，6线程，3次冷进程 | 16,906 ms | 约3.55倍；同线程比较 |
| AVX2 CPU，8线程，单次冷进程 | 4,905–5,264 ms/窗 | 两轮；不混入同6线程主比值 |

同长窗的 18 次 AVX2 线程/重复组合，与通用 CPU 的词文及每个词起止全部一致。独立五句边界样本仅有一个句内英文词起点变动 80 ms，五句起止仍完全一致；对独立样本真值最大起点误差56.5 ms、终点64.625 ms。不是自然会议人工标注准确率。

Vulkan 已独立验证实际 `Vulkan0 / AMD Radeon RX 6900 XT`：16窗/480秒音频单进程 **6,505 ms**；后续15窗实际session均值134.37 ms。逐窗文本/原始边界与对应冷单窗完全一致，未缓存重复推理结果。相对通用CPU，个别词起点最大差160 ms、终点80 ms；独立五句真值仍最大起点56.5 ms、终点64.625 ms，固定350/500 ms阈值全通过。PID、路径和启动时间归属下独立显存采样峰 dedicated1,769,328,640 B、shared317,616,128 B，属于采样下界，3次无效counter保留。

修正后的无效device63实际非零退出且没有CPU回退；定位中取消终止拥有的进程，之后独立重试成功。证据 `.local/M4Perf/vulkan/native-final-summary.json`、`run-02`。原始失败保留：首轮探针重复传 `--device`，CLI取首个0，该错误不记为已验证63；CPU暖批首轮脚本取线程数0，被CLI拒绝且没有推理，不混入性能结论。

## 官方 Python 评估

**正式三分钟结论：官方Python尚未稳定胜过本机优化原生CPU，没有满足“官方Python CPU更快才切换”的条件，不自动改默认。** Python作为明确可选的会后定位路线，正常录音、回听、导出不依赖它。下面优先列出修复中文迁移故障后的最终可移动runtime，未修改的官方venv基准另行保留。

双方使用旧长测相同的三个完整30秒窗口，按0/1/2/0/1/2共6窗、180秒；音频帧、Qwen原文及顺序完全相同。共享请求JSON SHA-256为 `11ccabcdde8f2818d0f5a5b766d808e45da751f8a89b71f5ff9c07f685846f8c`，不是用11秒新输入对比旧30秒窗口。每次实际推理，不缓存重复窗口结果；正式计时用排他锁串行。

| 真实生产CLI，同180秒输入 | 线程 | 冷进程第1轮 | 冷进程第2轮 | 平均 |
|---|---:|---:|---:|---:|
| 原生AVX2 raw / batch-v2 | 8 | 20,246 ms | 20,443 ms | 20,344.5 ms |
| 官方Python F32，未改venv生产bridge（早期基准） | 6 | 22,624 ms | 22,066 ms | 22,345 ms |
| 官方Python F32＋延迟日语导入补丁，外置base解释器 | 6 | 20,272 ms | 21,625 ms | 20,948.5 ms |

这里的**冷进程**包括解释器/程序启动、库导入、模型加载、6窗实际推理及结果落盘，**不代表清空Windows磁盘缓存**。最终Python本组平均多约0.60秒、耗时高约3.0%，两轮范围有重叠；不把这个小差距宣传成普遍的CPU优劣保证。暖调用仅指同一已加载模型进程中的后续推理，不包含此前的导入/加载，不能用暖单窗代替整次耗时。不同模型精度、加载次数和批量大小须保持可见。

6/8线程均已先测：原生6线程23,383 ms、8线程20,689 ms；官方F32基准helper为23,602.8/24,031.3 ms，其中import+load+6窗推理分别20,858.0/21,326.4 ms，其余含测试专用哈希/审计开销。**没有删除这些开销后宣称胜出**。随后未改官方venv的生产bridge两轮为22,624/22,066 ms，import约8.55–8.69秒、模型加载约1.37–1.39秒；这些原结果不覆盖。最终可移动runtime按既有较快的6线程重新跑同序两轮，import降为6.71/6.59秒、模型加载1.34/1.33秒、6窗推理11.49/12.93秒，结果见上表。最终环境未另重复8线程矩阵，较长任务也未据暖调用外推强换默认。

这项启动补丁针对真实中文路径FAIL：`qwen_asr`顶层导入`nagisa`时会立即加载其日语DyNet模型，而旧C++文件API不能读取中文目录。只在外置runtime把`import nagisa`移入实际日语分词函数，保留原文件、diff及前后哈希；中文/English计算不变。桥接明确只接受这两种语言，不宣称日语便携可用。它应称为**官方模型/计算路线＋本地延迟日语导入补丁**，不冒称完全未修改上游。修后12份真实raw输出与未改官方venv逐字节哈希一致，生产C#句子合同再次通过。

标准官方API另有一组4输入、first/warm各一次：三个原30秒窗的暖调用为1868.7/1760.6/1945.0 ms。该helper把部分Qwen模块延迟导入计入from_pretrained阶段，故import约3679 ms、该阶段6442 ms；阶段边界与生产bridge不同，须看合计，不能把分类改变看成加载加速。原解释器自身采样RSS峰 **6,093,455,360 B**、private峰 **7,599,235,072 B**。初期外部wrapper只观测到venv启动器约4.3MB，**那不是模型内存**，不用于资源结论；总壁钟仍覆盖实际子进程。最终可移动runtime直接调用base Python，两轮实际解释器工作集峰最高 **5,942,579,200 B**、private峰 **7,399,419,904 B**。

实际环境为 **Python3.12.10、qwen-asr0.0.6、Transformers4.57.6、PyTorch2.14.1+cpu、NumPy2.5.3**，CPU实际AVX2，模型全部F32。官方Qwen源码锁 `7c6daf77a2421100f5fb066495372c00129d39ff`。只加载ForcedAligner checkpoint，没有另载ASR模型；对照venv在项目内，最终可移动环境在 `H:\AI\Qwen\python-aligner-runtime`，未修改全局Python包、PATH、注册表或代理。

原始官方模型目录为 `H:\AI\Qwen\官方定位0.6B-c7cbfc2`，revision `c7cbfc2048c462b0d63a45797104fc9db3ad62b7`，Apache-2.0；safetensors **1,835,544,544 B**，LFS SHA-256 **`47831d0e82f96b20e9034dba01a075ee06436654719f6a68289e49f1b65ce0e7`**，已实下载校验。可移动环境 `H:\AI\Qwen\python-aligner-runtime` **已部署并通过本机中文路径移位、隔离离线运行复测**；retry-03使用最新`c943...`桥及`9d0a14...`处理器，真实CPU中英定位通过，Python/第三方包路径均在外置runtime，所需VC DLL实际从其本地目录加载。证据 `.local/M4Perf/python-runtime/中文路径离线复测-retry-03/runtime-audit.json`及`bridge-manifest.json`。它与模型均不进入软件ZIP，不替换现有GGUF；物理旧CPU、真正干净Windows仍 **NOT_RUN**。

官方 `fix_timestamp` / `_fix_timestamps` 会做邻近或线性修补；英文同窗实际有一个内部边界 **1600→1680ms**。标准API测试保存raw与官方修补结果；生产 `qwen_aligner_bridge.py`只把原始声学预测写入供Core读取的words，repair仅对raw副本计算并留诊断，绝不用于定位。最终同180秒实测bridge SHA-256为 `c943feed095a23ed706c05b8eae35872bd9378d123eccb49d73d878ae078adb4`，外置补丁处理器SHA为 `9d0a14ed5c1cb9100fe6c52e5dd3c174923e934be3565657d67d4ae865516085`；未改venv先前bridge `bfdd1e...`的证据另存。生产Core对Python程序、bridge、官方模型目录文件逐个持只读锁并保留哈希；模型文件名/哈希组成稳定身份摘要。每批请求先按确定bytes写入，再读锁、核对原始bytes哈希，锁贯穿进程和输出验证；RequestsSha256、有效JSON运行manifest及其SHA进入每窗证据，manifest保留实际库版本及处理器源码哈希。`-I`隔离调用、桥接禁写bytecode，缓存限本次attempt，取消只结束本次拥有的进程树。

**raw→生产C#句子合同已实测：**20组检查全部通过（8份标准API raw及12份生产CLI raw），使用M4 Final冻结Core，未使用repair文件。与Q8原生逐词边界最大差80–160ms，不声称全部相同；独立五句SAPI边界起点最大绝对误差56.5ms、末点64.625ms，预定350/500ms阈值通过。它不是自然会议普遍精度证明。

BF16在本机首窗first/warm为28.171/27.442秒，明显慢于F32；最终保留5次实际完成、约27.4–28.5秒/窗，其余3次 **NOT_RUN**，已确认停止owned进程，不宣称完整矩阵/资源通过。新版官方HF模型也已锁定另行下载，但其第二环境安装按优先级主动停止，**HF推理、torch.compile、完整三小时Python定位均NOT_RUN**；不拿模型卡A100加速例子或原生GPU长测代替它们。最新Core三分钟/原生GPU三小时生产链另由后续章节记录。

本机官方Python环境仅CPU，`cuda_available=false / torch.version.hip=None`；RX6900XT没有本轮受支持且实测的Windows PyTorch GPU闭包，CUDA不适用于该AMD卡，DirectML亦未验。原生Vulkan成功不等于官方Python GPU成功，不安装非官方绕过或更改驱动。

证据：`.local/M4Perf/python/REPORT.md`、`legacy-f32-01/report.json`、未改venv的`production-three-minute-t6/summary.json`、最终`portable-lazy-three-minute-t6/summary.json`、`portable-lazy-raw-equivalence.json`、`portable-lazy-raw-sentence-comparison.json`；原生最终两轮为 `.local/M4Perf/cpu/three-minute-native-t8-r2/summary.json`、`three-minute-native-t8-r3/summary.json`。官方来源：[锁定Qwen源码](https://github.com/QwenLM/Qwen3-ASR/tree/7c6daf77a2421100f5fb066495372c00129d39ff)、[原始ForcedAligner模型](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B/tree/c7cbfc2048c462b0d63a45797104fc9db3ad62b7)、[新版HF模型](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B-hf)、[AMD当前支持矩阵](https://rocm.docs.amd.com/en/latest/compatibility/compatibility-matrix.html)。来源支持方案存在；本机结论来自上述实际证据。

## 验证与限制

最终Core/App Release构建 **0警告/0错误**；**311默认回归 PASS**，含17窗拆16+1、末批错文拒绝、缺失/重复/未知完成ID、错误实际设备、定位中取消/独立重试、旧CPU兼容及设置迁移、官方目录/桥接只读锁、请求清单主动试写拒绝、缺失/损坏运行清单拒绝。证据 `.local/M4Perf/build-04.log`、`.local/M4Perf/regression-03`。这些是程序/合成夹具验证，不替代真实模型或硬件测量。

真实程序接口短链通过：`.local/M4/perf-real-short-01/report.json`（公开中文/英文及合成中英、取消/错误设备/重试、90秒末窗基线）。修后官方Python三分钟完整ASR/定位生产链通过，适配器28,268 ms，6窗、原稿/模型/音频哈希与重开通过；`.local/M4/perf-official-three-minute-02/report.json`。这包含GPU ASR和前后处理，不能当作“Python定位单项”的速度与20.9秒CLI直接混比。

## 三小时完整生产链

`.local/M4/perf-real-long-01/final-validation-summary.json`：11:24:28–11:31:09（香港），**360窗、172,800,000帧、600句全部完成**。22批×16窗加末批8窗，共23次定位进程，实际设备均Vulkan0/RX6900XT；每个请求ID、音频路径、原文、raw词界和最终句界独立重核通过。

| 阶段 | 旧M4 Final | 本次AlignFix |
|---|---:|---:|
| Qwen识别 | 189,958 ms | 190,548 ms |
| 句子定位 | 6,907,243 ms，通用CPU逐窗 | **120,082 ms，Vulkan有界批处理** |
| 识别适配器含校验/保存 | 7,105,787 ms | **318,196 ms（5分18.196秒）** |

本次包含构造/导入/混音/16k准备与最终审计的探针总时为401,322.49 ms（6分41.322秒）。定位部分约57.5倍提速，同时换用了加速后端和批处理，不能单独归因于任一项；不是自然三小时会议的速度/质量保证。

本场每个窗口与本场完整ASR输入的切片逐字节一致。跨独立短参考仍有357窗SHA不同、225,041个样本有差，最大1 PCM16 LSB、RMS0.03608767936 LSB，每窗±8帧最佳lag均0；文字及raw整体起止一致。保留 `DIFFERENCES_RECORDED` 和PCM审计，不把独立重采样说成逐字节相同。

独立原生进程观测：定位进程OS lifetime工作集峰1,539,141,632 B、采样private峰2,923,257,856 B；独立GPU计数352条经PID/时间归属通过，dedicated峰1,769,328,640 B/shared峰611,766,272 B，19次无效counter错误保留。调度父进程只在末段采到2次，最后观测的OS lifetime工作集峰124,731,392 B，**NOT_FULL_SAMPLING**，不能当作全程连续采样。记录中额外出现一次短 `--version` 能力探测PID，推理进程数仍为1个ASR+23个定位。

## 便携与界面

同版Candidate的749清单文件、App/Host自带运行时及idle IPC通过；九项WPF全部通过，测后749项哈希仍一致。真实按钮链路分别运行Vulkan定位、AVX2 CPU定位、官方Python CPU定位、兼容通用CPU定位，均成功并验证句子点击/静音回听、切会、重开、导出和原件保留。完全不含模型和ASR程序的Base实际完成WAV/MP3导出。新UI三语及最小窗口图片无实质布局问题；真实“兼容/官方”截图是字幕页，其配置路径验证来自程序断言与attempt哈希，不能说图片展示了配置页。

证据 `.local/M4/perf-candidate-functional-review.json`、`.local/M4Perf/real-ui-candidate/runs.json`、`.local/M4Perf/final-audit/REPORT.md`。7个Core/Capture/Export/Host DLL从三小时冻结目录复用并逐字节核对，最终发布不另换未经长测的DLL。Qwen权重、Python环境、测试音频及私人Data不进入ZIP；保留原有Whisper CPU/Vulkan/base。

## 已知限制与失败保留

补充暂停链路中，新去暂停11秒在Vulkan通过；旧elapsed28.345秒在语音中保留17.345秒暂停时，GPU对`your`预测起点22.960秒、终点5.520秒，发生反序。严格校验正确拒绝，**该GPU样本FAIL保留**，没有交换/插值边界或冒充成功。显式改选AVX2 CPU，11秒新时间轴和28.345秒旧时间轴均通过完整处理和SRT/VTT往返；CPU该词22.960–23.200秒，与旧通用CPU相同。三组混音/16k输入哈希和ASR原文相同，未见时间轴/丢帧差异；仅凭此例不能确定模型歧义还是Vulkan数值/端口根因。遇到此类GPU原始时码错误，可保留本次输出并手动选择CPU重试；软件不静默切后端。证据 `.local/M4Perf/pause-vulkan-01/summary.json`（FAIL）和 `pause-avx2-01/summary.json`（PASS）。

旧CPU物理实机、真正干净Windows、其他GPU/CPU/Windows版本、1.7B、自然长会质量、官方Python三小时、HF新版以及原M1/M2现场缺口保持NOT_RUN。现有“铯→铀”“复核→复合”等识别错误仍保留，定位成功不证明文字正确。未采集音频/mic、未上传、未推送、未关机、未改全局策略。
