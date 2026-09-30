# 48｜M3b 会后本地 ASR 后端接口

本轮为未来更换本地识别引擎准备边界。当前默认并且唯一能运行的实现仍为 **whisper.cpp**；**Qwen3-ASR 未部署、未实现适配器、未验收**。没有下载模型、引入 Python/Node、增加网络服务、密钥、上传或数据库。本接口不进入 CaptureHost 或录音回调。

## 调用合同

`ILocalTranscriber` 定义轻量 `Inspect`、能力描述 `Descriptor` 和可取消 `RunAsync`。`LocalTranscriptionBackends.Default` 返回 `WhisperCppLocalTranscriber`。只有实现且验收后的适配器才能作为可运行实例；`Qwen3Asr` 是单独的 `FutureLocalTranscriberPreparation` 说明对象，不能调用识别，不能把“已预留接口”显示成“已支持 Qwen”。

`LocalTranscriptionConfiguration` 带后端标识、本地程序/模型位置、语言、CPU线程、模型名称、预期模型 SHA-256 和许可确认。不接受 URL、密钥、任意命令模板或附加 CLI 参数。引擎配置不能交叉使用；whisper.cpp 收到其他 backendId 会返回 `NotConfigured`，不会自动回退、下载或启动程序。

现有 `WhisperConfiguration`、`whisper.json`、`WhisperTranscriber.Inspect/RunAsync` 保持兼容。`FromWhisper` 无损映射旧配置，`-ng`、SRT/TXT/JSON输出和原件保护沿用。M3b反馈修复取消`-ml 48`长度强切，改为`-ojf`保留完整token时间，并使用句末标点和可核对token边界规范化；缺少可用时间时保留原段，不按字数造时间。详见[45](45_M3_TRANSCRIPTION_CONTRACT.md)。

`LocalTranscriptionRequest` 只接收已保存会议中的完整派生 WAV、预期 SHA-256、会议身份和完整经过时长。格式转换在会后准备层完成。当前 whisper 适配器明确要求 **16 kHz / mono / PCM16**；这个要求不等于麦克风必须16k或48k，也不预设未来 Qwen 的输入规格。准备与识别在后台任务执行，文件校验和失败不占用采集回调。

能力描述区分输入规格、取消、自动/指定语言、说话人和时间质量。当前字幕是 `approximate / segment`，并且不提供说话人识别。支持指定语言参数不是每种语言准确率验收。能力字段必须由具体适配器根据已验证行为声明，不能把 whisper 的能力复制给 Qwen。

## 输出、取消和版本

统一结果状态为 `NotConfigured / Succeeded / NoSpeech / Cancelled / Failed`，带独立尝试目录、耗时、说明及 `TranscriptRevision`。成功必须有已保存规范化版本；失败/取消不对 UI 返回一个成功版本。取消后保留已有原件、原稿和尝试输出，重试写入新的尝试目录。配置/请求验证等异常仍可抛给调用方，由现有任务状态显示，不伪造成功。

规范化继续使用 `TranscriptService.Import` 和 transcript v2：时间以完整会议的毫秒起止表达，保留暂停、静音、重叠与重复，不按文字推测时间或说话人。原始模型输出、导入原稿、规范化版本与人工修订分别保留，原稿 SHA-256 在重新加载时复核；运行 ASR 不覆盖人工修订。只有文字而没有时间的未来引擎输出必须保持 `untimed`，不能由句长造 SRT。

当前独立 whisper 进程取消会结束其进程树；不停止录音或其他引擎/用户进程。数字零 PCM 先返回 `NoSpeech` 而不启动模型；该保护不是一般噪声/静音幻觉验收。模型返回空字幕同样不是“会议无人讲话”的证明。

## 未来 Qwen3-ASR 实施清单（均未执行）

1. 从官方来源确认选定版本的本地运行方式、Windows 支持、模型文件、依赖、硬件资源和许可。不能假定直接运行 whisper CLI，也不能自动安装一个依赖环境。
2. 另建实现 `ILocalTranscriber` 的具体适配器，在其 `Inspect` 中验证本地配置。若运行时需要多模型/目录/对齐组件，再按真实接口增加类型，不提前塞入任意可执行命令或通用插件框架。
3. 核验实际输入采样规格、语言表达和时间能力。若只返回全文，则规范化为无时间文字；若额外对齐才得到时间，必须保留独立来源及近似质量说明，不把时间强制对齐当成识别引擎天然能力。
4. 为每次尝试留存模型/程序/输入哈希、版本/许可、参数和原始输出；使用当前的不可覆盖规范化/修订流程。完整音频时间轴保持不变，不因模型分段、最大长度或内部拼接而丢失末尾。
5. 验证故障隔离、真实取消/进程退出、失败重试、无模型录音、长会议、中文/中英、数字/专业词、噪声/静音与时间映射；全部通过后才在界面提供可运行选择。

## 本轮验证边界

新增 `M3LocalTranscriberTests` 的4组测试覆盖配置映射/引擎拒绝、统一输出与原稿/毫秒时间、数字零/输入哈希、合成 CLI 取消/失败/重试。这些使用明确标记的本机合成 CLI 夹具，**不是 Qwen 或真实模型准确率验收**。实际构建、测试命令与最终结果由本轮总报告/STATUS记录；此文不预先记为通过。

此前真实 whisper.cpp/base 的公开英文11秒证据见 [47](47_M3_WHISPER_AND_SUBTITLE_REPLAY.md)。中文、混合语言与长会议仍不能据此标为通过。
