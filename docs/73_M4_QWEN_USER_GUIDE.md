# 73｜Qwen 中文识别与句子回听

Qwen 是可选的会后引擎。现有 Whisper 配置与稿件保留，没有模型仍可录音、回听和导出。程序不自动下载模型，不上传声音或文字。

## 本次目标机部署的位置

| 用途 | 位置 |
|---|---|
| Qwen 0.6B 模型 | `H:\AI\Qwen\models\Qwen3-ASR-0.6B-Q8_0.gguf` |
| Qwen CPU 程序 | `H:\AI\Qwen\transcribe-077110e-ProjectEcho-20261004\cpu\transcribe-cli.exe` |
| Qwen Vulkan 程序 | `H:\AI\Qwen\transcribe-077110e-ProjectEcho-20261004\vulkan\transcribe-cli.exe` |
| 句子定位程序 | `H:\AI\Qwen\aligner-raw-v1\audiocpp_cli.exe` |
| 句子定位模型 | `H:\AI\Qwen\Qwen3-ForcedAligner-0.6B-GGUF\qwen3-forced-aligner-0.6b-q8_0.gguf` |

M4-AlignFix 便携包另附 Qwen CPU/Vulkan，以及通用 CPU、AVX2 CPU、Vulkan 三种句子定位程序和许可。上表的 `aligner-raw-v1` 是保留的旧通用 CPU 程序；使用新版加速时选择随包 `Tools/qwen-aligner-avx2/audiocpp_cli.exe` 或 `Tools/qwen-aligner-vulkan/audiocpp_cli.exe`。

Qwen ASR 和句子定位的两个 GGUF 模型共约 **1.98 GB（1.84 GiB）**，不在软件目录或 ZIP 内；可放在用户自行选择的本地模型目录。
其他电脑请按自己的模型目录重新选择文件；上表不是软件要求的固定盘符。

## 使用

1. 完整解压软件，选择或导入已保存会议，生成完整混音。
2. 在转写设置选择 **Qwen3-ASR + 句子定位**。便携包会填入随包程序；分别选择 Qwen 模型与句子定位模型，核对来源和许可。
3. 分别选择“识别硬件”和“句子定位硬件”。本机 RX 6900 XT 的两个 Vulkan 阶段均已独立实测，建议句子定位选择 **Vulkan**；GPU 编号一般为0，界面用于两个阶段。其他机器可先选 CPU。随包 CPU 定位会按实际指令集选择 AVX2 版或通用版；自选外部程序不会自动改路径。
4. 点击“开始识别和句子定位”。每次运行生成独立原稿；失败或取消保留原音频、旧稿、人工稿与本次输出，重试不覆盖。
5. 完成后单击一句字幕，从该句位置回听。仍可人工改字、切换版本、导出 SRT/VTT 或全文。

较老CPU可勾选 **“兼容旧 CPU（关闭 AVX2/FMA 等加速）”**。CPU定位会实际使用随包通用程序和原生GGUF，即使原先选择了官方Python也不会加载它；所选方案及两套模型路径仍保留，取消勾选后恢复。检测到不支持优化指令时也使用兼容路径。界面显示实际采用的程序，兼容模式下需配置原生定位GGUF。该选项不改变Vulkan选择；现有识别CPU程序本来就是通用x64/SSE2基线。旧CPU实机、其他Windows版本仍需各自验证。

本次新写入的Qwen设置为schema2，新版可读原schema1；旧软件会拒绝它不认识的新设置，避免忽略官方CPU/兼容控制后运行错误方案。旧软件目录、Data和原稿不会自动迁移或覆盖。

“保存为新会默认”仅影响以后新建的会议。提示词只属于当前会议，旧会议不会因为全局设置改变而换引擎。选择 Whisper 后保存其默认配置，可恢复新会默认使用 Whisper。错误设置可通过“重新配置引擎”保留备份后重新选择，音频和字幕不变。

开始下一场录音时，后台 Qwen 识别/定位会取消并保留输出；录音不等待识别结束。录音、暂停及收尾期间仍禁本程序回放。录音保存后可重新启动识别。

新版可以分别加速识别和句子定位；旧版本仅识别使用 Vulkan，通用 CPU 定位占主要时间。定位按最多16个窗口复用模型，界面进度来自真实完成的窗口。已有会议保留原 CPU 设置；要提速，请在该会议显式改选定位 Vulkan 和对应程序，再重试生成独立新稿。

长会耗时仍取决于语音密度、句长与机器配置，构造压力语料不等于自然会议速度保证。性能对照和实测范围见 [定位性能报告](74_M4_ALIGNMENT_PERFORMANCE.md)。没有合适 Vulkan 设备时使用 CPU；指定 Vulkan 失败不会静默换成 CPU 后宣称 GPU 成功。

## 自行下载和切换模型

Qwen 模型与转换程序的许可分别核对。只有 GGUF 文件适用于本次原生程序，不要直接选择官方 safetensors 权重目录。

- [Qwen 官方 ASR 项目与模型说明](https://github.com/QwenLM/Qwen3-ASR)
- [transcribe.cpp 官方模型目录](https://github.com/handy-computer/transcribe.cpp/tree/077110eb00ca6880e13bef4ddebf766b3b76896c/catalog)
- [本轮锁定的 Qwen3-ASR 0.6B Q8_0 GGUF 下载](https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/28c66fba077f1359a42b52c55b618a24cdb6e0e7/Qwen3-ASR-0.6B-Q8_0.gguf)
- [官方 Qwen3-ForcedAligner-0.6B 模型卡](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B)
- [audio.cpp 对齐模型转换文件](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/351dbab/Qwen3-ForcedAligner-0.6B-GGUF)

本轮实际配置和测试为 **Qwen3-ASR 0.6B Q8_0**。以后可在“Qwen 模型”中选择兼容的 1.7B GGUF，再保存本场或新会默认；0.6B 与 1.7B 的原稿独立保留。**1.7B 本轮未下载、未实测**，其速度、内存和准确率不能沿用 0.6B 结果。

## 如何理解结果

句子位置来自针对本次 Qwen 文字的独立声学对齐，模型内部边界量化为 80 ms；界面显示百分秒不等于 10 ms 准确。定位成功不证明文字正确，尤其应回听核对数字、否定、金额、专名和术语。

原生上游对齐程序的普通版本会自动修补部分时码；软件要求随包的 `raw-v1` 版本，避免把插值当成声学证据。对齐异常时会明确失败并保留输出，不用窗口起点或字数造时间。

已知边界情况：旧时间轴在语音中保留约17秒暂停时，本机Vulkan曾输出倒序词界，程序正确拒绝保存；显式选择原生CPU重试通过。遇到“原始声学对齐反序或越界”等错误，可手动改选CPU重试，已有音频和稿件仍保留。不会为了显示完成而插值补时间或暗中换后端。

官方 Python ForcedAligner 是**明确可选**的会后定位路线，不是软件必装项。本机同一三分钟输入的完整调用，优化原生CPU平均20.34秒、修复启动导入后的外置Python F32平均20.95秒；两轮范围有重叠，Python尚未稳定胜出，因此不会自动把CPU切换为Python。暖推理较快不等于整次任务较快，正常录音、回听和导出不依赖Python。

若显式选择官方Python路线，运行程序为 `H:\AI\Qwen\python-aligner-runtime\python.exe`，原始官方模型目录为 `H:\AI\Qwen\官方定位0.6B-c7cbfc2`；桥接脚本位于随包 `Tools/qwen-python/qwen_aligner_bridge.py`。外置runtime已部署，并通过本机最新桥的中文目录移位、隔离离线中英定位复测；这仍不代表物理旧CPU或真正干净Windows验收。运行环境和原始模型都在软件目录/ZIP之外；其他电脑须按本地位置重新配置。不要把官方safetensors目录填进原生GGUF模型选择框，也不要用GGUF文件替代官方Python模型目录。

Python路线固定使用本机实测合适的F32；BF16在本机明显更慢而未采用。外置环境对官方库作了明确的“延迟日语导入”小补丁，以修复中文安装目录故障；本次桥接只启用中文/English，不宣称日语可用，已验证中英raw结果与未改官方版本逐字节一致。原始预测与官方修补时码分别保存，产品只使用raw声学边界，原音频和稿件不覆盖。详细范围及未测项见[定位性能报告](74_M4_ALIGNMENT_PERFORMANCE.md)。

本轮质量测试中存在实际错词，不能宣称 Qwen 永远优于 Whisper。VAD 小样本对照没有证明额外收益，本轮不要求另配 Silero。完整实测和未测范围见 [实施报告](71_M4_QWEN_CHINESE_ASR.md) 与 [质量和窗口证据](72_M4_QUALITY_AND_WINDOW_EVIDENCE.md)。
