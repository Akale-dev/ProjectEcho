# 54｜麦克风启动缺口调查与修复

2026-10-01。本轮调查M3C真实麦克风启动缺口与中途断续，修复相关采集和混音保护，不进入M4。用户已授权必要mic测试，本轮继续使用测试接口，不使用computer-use，不改设备设置/驱动/系统策略，也不播放、识别或上传mic内容。

## 事实与原因边界

旧Jabra路径：设备报告16 kHz / mono / PCM16，默认处理模式的共享格式为48 kHz / stereo / Float32。旧策略为让设备计数与PCM帧同域，在48k共享引擎上请求16k，并开启`AutoConvertPcm | SrcDefaultQuality`。

原始API包已反复给出首包124帧、下一包160帧、device起点0→160、QPC约10ms。`rawFrameCount=frameCount`且`retainedFrameOffset=0`，证明不是文件写入或停采裁切丢掉首包末36帧。NAudio3.1的GetBuffer/ReleaseBuffer直接转发原始COM返回。

这种路径下的额外采样率转换与启动短包有明确实测关联。没有据此宣称Windows内部SRC/驱动的完整根因已被证明；也没有把36帧用补零、丢弃首包、改QPC或提高jitter阈值伪装成连续。

旧两小时记录在545.499804秒还出现36帧、`data_discontinuity`的原包，随后缺124帧/7.7478ms。这属于已有真实未观察区间，旧FAIL和原件保持不变。修复后的短测不能自动证明旧中段问题已彻底根除。

## 同一设备的实际格式对照

两份显式metadata-only接口实验直接调用GetBuffer，记录原帧数、device、QPC、flags及IAudioClock；返回的PCM指针不读取、不复制、不保存。每段3秒、fresh client，Stop/Dispose后才落盘元数据；没有提前预热或隐式录音。

| 路径 | 次数 | 首包→后包 | 实际结论 |
|---|---:|---|---|
| 原生产16k+高质量转换 | 9 | 124→160，deviceΔ160 | 启动短包稳定复现 |
| 默认共享48k，无转换标志 | 9 | 463→480，deviceΔ160 | 仍短包，且PCM/设备计数跨域，不能直接作为修复 |
| 去掉`SrcDefaultQuality` | 3 | 149→160 | 仍短包，不通过降低转换质量解决 |
| 默认16k、无转换标志 | 3 | 初始化拒绝0x88890008 | 未启动采集，保留不支持结果 |
| IAudioClient3默认周期、48k | 3 | 463→480，deviceΔ160 | 未消除问题 |
| Other+RAW，再读取共享格式 | 3 | 160→160，deviceΔ160 | RAW共享格式实际变为16k mono Float32，无额外SRC |
| Other+RAW，直接请求设备16k PCM16 | 3 | 160→160，deviceΔ160 | 同样完整；正式路径采用GetMixFormat给出的共享格式 |

RAW模式必须在**新client第一次读取MixFormat之前**设置；NAudio会缓存MixFormat，设置RAW后再读旧缓存可能得到错误的旧48k结果。只使用`AudioStreamCategory.Other`，不使用可能触发ducking的Communications，不使用exclusive或变更系统格式。

依据：[GetBuffer的原始帧和时间戳合同](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)、[共享格式合同](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getmixformat)、[转换标志](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants)、[RAW语义](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-audclnt_streamoptions)、[NAudio3.1实现](https://raw.githubusercontent.com/naudio/NAudio/v3.1.0/src/NAudio.Wasapi/CoreAudioApi/AudioClient.cs)。

证据：`.local/MicrophoneFormatAb/20261001-020052-e82843d5b31c4ddc9ab49a5fc9c70a54/report.json`、`20261001-020803-9628a7c6097d4c69a4f33f5315ea6d03/report.json`；脱敏摘要`.local/MicStartupFix/ab-diagnostics-summary.json`。这些是采集API诊断，不等同正式Host保存或隐私边界验收。

## 正式实现

- 仅在默认shared mix与设备报告采样率不同时，尝试同一固定设备的fresh RAW client；设置RAW后读取格式，确认PCM采样率仍与设备计数的预期域一致，再直接初始化共享流，不加额外采样率转换。
- 同速率设备保持原路径。RAW明确不支持或格式被拒时，在未Start的前提下释放client，并在同一设备新client重试既有兼容方案。权限、设备失效、资源失效、设备占用和一般无效参数不触发回退；没有换mic或扩大音源范围。
- M0试录、M1正式录音与预检共用协商。预检不Initialize/Start，预算按实际Float32字节率计算；正式来源初始化一次，事件/采集接口建立后仍先等待writer就绪才Start。
- segment新增可选`processingMode`（`shared-default` / `shared-raw` / `shared-converted`），磁盘读取与恢复副本保留。缺字段/空值保持旧记录未知模式，不能称为RAW。该字段不影响时间戳或完整性判定。
- 原始QPC/device/frame/flags、Stop/MicOff/Pause实际截止和原有2ms/25%有设备连续证据的jitter规则均未放宽。

RAW只作用于此采集流：它不应用可关闭的Windows软件音效，设备自身/始终开启的处理仍可能存在，声音可能与默认处理模式不同。预检说明实际选择；没有修改Windows设备属性或系统音量。

## 微小真实缺口的混音保护

复审发现既有renderer将≤2输入帧的QPC差当量化误差时，未要求mic设备计数连续，可能拉伸前包填掉真实两帧缺口。现对mic额外要求设备帧连续、后包无断续/时间错误；前包的初始断续标志只描述其前方，不错误阻断它与下一正常包的连续关系。remote既有QPC-only规则保持。

新混音版本为`m2-qpc-mono-3-mic-gap-preserving`。旧混音不再自动按新规则复用；需用户主动点击“生成 / 重试完整混音”，旧原件和旧混音均保留，不在后台删除或覆盖。不能通过这项修复找回旧录音缺失PCM。

导出入口独立复核磁盘上的版本与时间轴，不仅依赖UI缓存；旧版、未知、缺失版本或不匹配时间轴会在创建输出目录前拒绝。导入录音继续使用`m3-audio-import-1`及`imported_decoded_media_from_zero`，导出清单和本地回执沿用实际来源版本，不误标成新mic renderer。

## 中途断续复现与采集线程加固

首次RAW正式20分钟复测实际录制1201.174205秒，启动首包正常，但109–110秒附近出现两处remote缺口（20ms/10ms）与两处mic缺口（27.9953ms/18.7551ms）。mic原包32帧并带data_discontinuity，下一包device起点跨过未观察帧；不能称为单纯QPC抖动。两路各41块，原件hash和磁盘读取均验证，`SourceStorageComplete=false`保留，严格连续性**FAIL**。测试期间该时点与开发构建/默认回归活动重合；原版未记录包到达间隔/持包时间，不能从重合直接证明CPU、GC或驱动的因果。

为减少应用自身导致丢包的机会，正式采集线程增加以下防护，实际复测只证明各自明确记录的范围：

- 真实M0/M1采集线程在writer就绪后、Start前请求Windows MMCSS的`Audio`任务和`NORMAL`优先级；在同一线程停止/排空/释放来源后注销。失败记录诊断并保留普通调度，不改注册表、服务、系统电源或进程优先级。默认纯合成测试不访问原生调度API。
- M1每个来源Start前按已协商BufferSize分配私有scratch（上限1MiB，无法查询时有同样有界fallback）。持有WASAPI包期间仅拷贝原截止规则允许的前缀，ReleaseBuffer后才分配精确PCM/结果对象；成功、异常及Dispose清零，禁存尾部不复制。
- 移除采集轮询循环中重复生成的来源/等待句柄数组；只在来源变化时重建。共享事件模式Initialize仍用0/0，没有不合法放大WASAPI缓冲时长。
- `recording-result.json`增加固定计数诊断：原生buffer/latency、最大轮询和持包时长、采集线程分配量、进程GC次数及总暂停差值。计数不影响API时钟或完整性判断，进程GC差值不冒充采集线程专属GC。

证据：`.local/MicStartupFix/medium-raw-results.json`与`medium-live-discontinuity/`；原报告`.local/M3cMicrophoneEndurance/medium-20261001-022815-edbb5724117649f99bbd54c75f1d6b78/probe-report.json`。依据：[MMCSS](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)、[共享事件Initialize合同](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)。这不是更改用户音频设置，也不保证操作系统/设备永不丢包。

## 加固版实际边界与受控负载对照

最新冻结Release二进制再次完成20秒晚开/关麦/再开/暂停恢复和40秒跨30秒块录音，均PASS；40秒完整WAV/MP3及分片实际解码EOF通过。MMCSS的Audio注册、NORMAL请求、同线程释放全部成功且错误码为空。mic实际BufferSize为352帧（16k下约22ms），私有scratch1408字节；remote报告480帧，但不能仅凭该属性推断process-loopback实际排队容量。

同一二进制、同一固定RAW mic与自有进程树先关闭再开启MMCSS（顺序未随机），各90秒。在20/50秒各运行10秒普通优先级CPU子进程：本机16个逻辑处理器，仅4个worker，名义8ms工作/2ms休息，**不是全机饱和压力测试**。remote在2/25/45/55/82秒各发一段已知信号，其中25/55秒位于负载内；mic不回放或分析内容。

| 结果 | MMCSS关闭 | MMCSS开启 |
|---|---:|---:|
| 实际录音秒数 | 90.663555 | 90.666941 |
| mic / remote声明缺口 | 0 / 0 | 0 / 0 |
| mic最大轮询间隔 | 10.1122ms | 13.9264ms |
| mic最大持包时间 | 0.0078ms | 0.0047ms |
| remote最大轮询间隔 | 21.8591ms | 24.2457ms |
| remote最大持包时间 | 0.5791ms | 0.5792ms |
| 进程GC累计暂停差值 | 4.692ms | 4.580ms |
| 负载内及负载外远端信号 | 5 / 5检出 | 5 / 5检出 |

两组原件hash、磁盘索引、source完整性及进程退出均通过。两组都通过意味着本次受控负载没有复现旧缺口，**不能以此证明MMCSS是旧断续的因果修复**，也不外推到任意CPU/IO/GC压力。运行诊断最大值包含开启/停采相关边界，不能由单个最大值判断整个流实际丢失。

证据：`.local/M1MicrophoneIntegration/20261001-030743-74cb0a179c7f41539d2cc1dae2145646/probe-report.json`；`.local/M3cMicrophoneEndurance/short-20261001-030925-0e0fa8bd305041e690f91c06d6d41837/probe-report.json`；`.local/MicSchedulingStress/20261001-031129-d0befd6db6764d0592c650f0e2a16eda/report.json`；脱敏摘要`.local/MicStartupFix/stress-summary.json`。冻结组件指纹在`.local/MicStartupFix/validation-driver-scheduling-20261001-030719/`。

## 加固版20分钟完整复测

香港时间2026-10-01 11:17:06–11:37:07，固定RAW mic+自有合成进程树，实际**1201.166629秒**。采集期间无并行构建或另一次采集；结束后才发布和进行UI验证。最终`PASS_TWENTY_MINUTE_MICROPHONE_ENDURANCE / PASS_NO_DECLARED_GAPS`，时长、存储、真实截止边界、编解码四项均通过。

- 两轨各41块，mic19,216,480帧/120,103包，remote57,651,840帧/120,108包；声明缺口与错误均为空，原件hash未变，磁盘独立恢复干净结束。mic原始QPC/device有效，首包160、次包160、deviceΔ160；remote仍保留QPC-only能力说明。
- MMCSS Audio/NORMAL注册与释放成功。mic最大轮询12.0985ms、最大持包0.0149ms；remote对应17.8340ms/0.5645ms，无>20ms轮询或>2ms持包。采集线程分配633,582,288字节；进程GC计数29/6/1，累计暂停差值20.457ms，后者不代表单次最大暂停。
- 完整WAV **57,655,999帧**，完整MP3实际解码57,661,680帧（差额为已登记编码延迟/填充），3个MP3分片覆盖全程，最大7,199,424字节、599.941秒。原/混音/分片hash、实际EOF、覆盖与重叠通过；18个内容比较窗全部通过，最小非静音相关性0.892205，最大延迟偏差0帧；3个合成远端标记均检出。未回放/识别/解释mic内容。

这是修复版在本机的一次20分钟通过，**没有把旧两小时FAIL改为PASS，也没有重新完成新版两小时或双设备声学同步验收**。中途断续的确切因果仍未由这次无缺口运行证明，其他设备和更强负载仍需各自证据。

报告：`.local/M3cMicrophoneEndurance/medium-20261001-031705-9a7af8d8f10f48e8b02c03f58e86fbc3/probe-report.json`；脱敏摘要`.local/MicStartupFix/scheduling-medium-summary.json`。

## 当前验证状态

- Release构建0错误/0警告；179组默认无设备回归通过，含RAW先设属性/缓存顺序、失败释放、受限fallback、实际格式预算、旧profile缺字段、恢复保留、RAW标签不能抹去缺口，以及真实两帧缺口/跨块/初包断续/停止边界保护、MMCSS同线程成功/失败回滚、scratch精确前缀/异常清理/热路径零分配、导出版本与导入时间轴保护。
- 正式生产Host三次约20秒晚开mic/关mic/再开/暂停恢复均通过：共9个mic段全部`shared-raw`、16k mono Float32，首/次包均160、deviceΔ160；remote与mic均无声明缺口，实际ACK/停采边界/磁盘原件哈希通过。
- 40秒真实跨30秒块资格通过：两轨各两块，mic4003包QPC/device有效，无声明缺口，完整WAV/MP3与分片EOF通过。
- RAW单独修复的20分钟中程严格连续性FAIL保留；调度与scratch加固后的短边界、跨块、两组受控负载和新版20分钟完整复测PASS。更长时段与其他设备的结果仍须按对应实证报告。

当前证据：`.local/MicStartupFix/build-01.log`、`.local/TestResults/MicStartupFix/default-01/results.json`、`.local/MicStartupFix/three-late-mic-results.json`、`.local/M3cMicrophoneEndurance/short-20261001-022635-6ff99a908cd6479ebf873cee6b2b2b2c/probe-report.json`。旧44.1k硬件、双设备声学同步、干净Windows及平台现场闸门不因本修复自动通过。

加固版构建与默认回归：`.local/MicStartupFix/build-scheduling-02.log`、`.local/TestResults/MicStartupFix/scheduling-02/results.json`。环境Windows11 25H2 26200.9457/x64、.NET SDK10.0.401/runtime10.0.12、NAudio3.1.0，来源为固定16k Jabra mic及本次新建的合成测试进程树。

## 便携交付与界面复验

完整自包含包：`artifacts/ProjectEcho-win-x64-20261001-M3C-MicFix/ProjectEcho.exe`。630个发布文件、236,057,213字节；release-manifest SHA-256为`ecfd7227c57836cfdb1aac40442300f738df47891b14e3750b1e605d6cafa8d7`。完整性、默认WPF启动、包内Host IPC及自带运行时通过，证据`.local/diagnostics/portable-20261001-113823/portable-smoke.json`，不等于干净Windows验收。

包内实际WPF的会库、转写/字幕/复制/语言/平滑静音回听、原生会议混音重开、导出关窗四流程最终通过。第一次隔离测试把cwd嵌套过深：回听WAV293字符、转写WAV297字符、搜索数据库路径264字符，导致播放不推进与搜索无结果。保留首轮FAIL；仅把测试cwd改短，同一二进制、同一默认权限重跑失败两项均PASS，没有更改产品或放宽断言。**深路径下媒体/SQLite兼容性仍是已观察到的限制**，选择较短的数据目录；本轮没有声称修复长路径支持，也未改Windows注册表。

首轮会库/关窗PASS与两个FAIL：`.local/MicStartupFix/portable-wpf-20261001-113907-346-e5e0a17afc9f41829fafbc8c0627160a/summary.json`。短路径两项PASS：`.local/MicStartupFix/portable-wpf-20261001-114314-519-7dc5e931d35c4dc4a38758b6cec1e996/summary.json`。

最终包原样生产组件/完整Host另建开发测试驱动，214个相应文件hash验证不变；Capture.Windows/Core/Export与20分钟测试驱动相同，最终Host DLL不同，故另做实际Host复验。20秒mic晚开/关麦/暂停恢复PASS：RAW16k Float32、1041包/166,560帧、真实截止/原件hash/干净结束/MMCSS均通过。

同驱动M0无mic短试录总状态仍为**FAIL**：1321项严格审计错误分别为`capture_incomplete=1`、`remote:device_position_unreliable=660`、`remote:device_overlap=660`，对应本机已知process-loopback设备计数恒零限制。目标660Hz→尾音880Hz、对照440Hz不入选、原件hash、QPC结构、mic文件不存在、M0 MMCSS注册与释放和三个进程退出分别通过；没有放宽设备时钟断言以换取总PASS。

最终包采集汇总`.local/MicStartupFix/published-capture-verification.json`；真实mic报告`.local/M1MicrophoneIntegration/20261001-034436-7c2c9c7a70454e95b7f96cdde2347634/probe-report.json`；M0报告`.local/AudioIntegration/20261001-034537-9b763f6774af4ebe81164f8f0d11c5ae/probe-report.json`。本机Whisper设置在验包后复制至可变`Data/settings`，与原配置hash一致，无模型/会议文件进入发布清单。全部自有采集与测试进程已退出。
