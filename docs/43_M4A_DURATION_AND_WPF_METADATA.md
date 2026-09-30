# M4A 时长与 WPF/WMP 属性精度诊断

**后续实际渲染已验证：**在生产Host对新建WPF测试播放器的进程树录制中，WAV和M4A均检出源文件2.45–2.65秒的尾标记及2.300秒首尾间隔；M4A当时的WMP属性仍只报2秒。实际尾声未按这个属性截断。证据 `.local/WpfPlaybackIntegration/20260929-180253-4001d4910dae42cda7ca5f1dda916ac2/probe-report.json`，方法与边界见docs/44；不等于人耳听验或大RF64直接WPF兼容。

2026-09-30。**本次发现是正常用户环境中 WMP 返回整数时长、WPF 沿用该值的问题，不是该合成 M4A 被写成 1 秒或被证实截尾。** 已有音频和编码器写入逻辑不变，没有改 MP4 box 字节，也没有放宽解码 EOF、源尾帧或文件大小检查；随后在发布编码结果前补上独立只读容器检查。

## 实际文件证据

原始 WPF 诊断：`.local/diagnostics/m2-wpf-playback-20260929-163644-922.json`。

实际合成 fixture：`.local/UiPlaybackTests/20260929-163645-7a66934b1e1044bdba03b6cfc72301e2/synthetic-actual-aac.m4a`。原始内容为 84000 帧 / 1.75 秒；AAC 补齐后实际解码为 87040 帧 / 1.813333333 秒，48000 Hz mono。文件 22810 字节，SHA-256：`a3439661fb72ef489e1514d65c2bb848570e726bea210d538bb941791280f2fe`。

独立只读解析 ISO BMFF 的结果如下：

| 数据来源 | 数值 | 对应秒数 |
|---|---:|---:|
| mvhd | timescale 48000，duration 87039 | 1.8133125 |
| tkhd | duration 87039，沿用 movie timescale | 1.8133125 |
| mdhd | timescale 48000，duration 87039 | 1.8133125 |
| stts | 85 个 AAC sample，每个 delta 1024；合计 87040 | 1.813333333 |
| Media Foundation 元数据 | float32 PCM 长度 348156 字节 | 1.8133125 |
| Media Foundation 实际读至 EOF | 87040 帧 | 1.813333333 |
| WMP `newMedia(path).duration` | 1 | **1.0，精度不正确** |
| WMP `getItemInfo("Duration")` | 字符串 `"1"` | 同一错误精度 |

没有 `elst` 编辑时间表，没有任何上述时间字段声明 1 秒。三处容器 duration 与完整 sample table/解码只差 **1 帧（约 20.83 微秒）**，属于本次时基取整量级；没有约 0.813 秒的容器缺口。不得为了让 WPF 某属性好看而修改这些正确的媒体时间表。

只读解析证据：`.local/m4a-atoms/original-atoms.json`，解析脚本 `Inspect.ps1`。正常非管理员独立 MF/WMP 对比：`.local/m4a-atoms/mf-wmp-metadata.json`。后者只创建媒体信息对象，没有设置 URL/当前媒体、没有调用 Play、没有更改音量或任何播放器设置、没有改源音频。

## 已核对的官方实现

NAudio 锁定版本 3.1.0 的 `MediaFoundationEncoder` 使用 `long position` 和 `long duration`；字节数转换乘数为 `10000000L`，单位是 100 ns，写入 sample time/duration 后调用 sink Finalize。没有把输入时长先转成整数秒。`MediaFoundationReader` 从 `MF_PD_DURATION` 读取时长；微软对此属性的定义也是 100 ns，数据类型为 UINT64/LONGLONG。[NAudio 3.1.0 源码](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/MediaFoundationEncoder.cs)、[MF_PD_DURATION](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-pd-duration-attribute)

已根据本机 `Microsoft.WindowsDesktop.App.versions.txt` 的准确源码 commit `95017c711e6afc1085133d440e42b4bd78155701` 取得对应 WPF 实现。`CWmpStateEngine::UpdateMediaLength` 调用 `IWMPMedia::get_duration`，把得到的 double 秒数转换成 ticks，并没有修复上游精度；播放结束时 `GetPosition` 路径直接返回 `m_mediaLength`。所以 **NaturalDuration=1 与 Ended 时 Position=1 是同一上游错误值的两次呈现，不是两项独立截尾证据**。[对应 WPF 源码](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/wpf/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/av/wmpstateengine.cpp)

官方 WMP duration 属性类型是 double，并非标准承诺只能使用整数。因此准确表述是“**本机该 M4A 的 WMP 时长属性只返回整数秒**”，不能泛化为每个 Windows、每种文件或每个 WMP 版本都如此。当前产品没有用 NaturalDuration 计算裁剪、终止解码或丢弃尾帧。[IWMPMedia duration 文档](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/wmplibiwmpmedia-iwmpmedia-duration--vb-and-c)

## 如何判断完整性

此轮 WPF 实际从打开到结束约 1.8 秒；动态 Position 曾达到约 1.69 秒，明显超过其宣称的 1.0 秒。加上容器和全量解码证据，可排除“文件时间表本身只有一秒”的原先疑虑，**但仅有墙钟和状态属性仍不替代真实渲染尾声检查**。

后续验证应保留“WMP 时长属性不精确”这一独立结果，采用完整 sample table/实际解码帧作为文件长度依据，同时核对用户明确授权的合成回放首尾标记、动态进度、MediaEnded 和原文件哈希。不能使用 NaturalDuration 作裁剪长度、完整性唯一依据或 external-upload 截尾结论。原失败报告原样保留；生产回放的真实首尾音频验证仍由联合测试另外记录。

沙箱中出现过的两次原生 `0xC0000374` 崩溃原因仍未确认；这里的整数 duration 证据**不能解释或归因那些崩溃**。不要把正常非管理员属性诊断等同沙箱原生故障已修复。

## 新增发布前只读检查

`M4aTimelineProbe` 只处理本程序生成的单音轨、非 fragment、无 edit list/composition offset、单一 mp4a description。它流式定位 mvhd/tkhd/mdhd/stts/stsz，跳过 mdat 载荷，不把完整媒体或 sample-size 表读入内存；异常 box 长度、重复必要 box、表数量/大小不一致及未验证布局明确失败。返回 `AudioEncodingResult.ContainerTimeline`，只含时基/时长/包数量/载荷字节标量，没有文件路径。

每个 AAC 输出在完整解码后核对：stts 的有理数时长与实际 EOF 帧数严格相等；三个声明时长各自与 EOF 相差最多一输出帧。比较用 Int128 整数交叉相乘，避免先转 double 或整数秒引入误判。这个检查只读取，不修补、重封装或调整正确音频；仍须通过既有真峰值、padding、实际字节等检查才发布。

两组新增回归实际 PASS：真实 1.75 秒 AAC 与 EOF 比对；故意把 mvhd/tkhd/mdhd 改成 1 秒、破坏 timescale/stts/stsz 或出现未验证 ctts 都被拒绝；8 小时相当的 135 万项 stsz 表按流式解析，并验证超过 2^53 的整数一帧边界。后者是明确标记的**仅元数据合成 fixture，不可播放，不是八小时真实 AAC 编码**。

新 timeline 两组连同四组真峰值与四组真实 codec 检查，独立运行 10 组全部通过。证据：`.local/TestResults/M2/m4a-timeline-final/m4a-timeline/actual-container-evidence.json`、`large-table-evidence.json`；整工程最终验收由主报告记录。
