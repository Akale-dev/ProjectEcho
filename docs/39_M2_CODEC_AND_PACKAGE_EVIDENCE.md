# M2 编码器与便携发布证据

更新：2026-09-29。此页只报告实际 codec 检查与发布实现；整阶段状态以 STATUS 为准。

**2026-09-30最终包更新：**当前包为 `artifacts/ProjectEcho-win-x64-20260930-014055`，616文件、230571321字节；最终清单SHA256 `84419efd2ec0789284959c4a8b20afae187019b7831eab7a18e4677fe06ca331`。仅使用说明最终更新后重新生成清单并实际重跑无采集包检查，证据 `.local/diagnostics/portable-20260930-021413/portable-smoke.json`。当前功能、91组自动测试和真实长文件/回放/受限外网验收见docs/41–44；下方较早目录和数字是历史快照，未删除。

## 已实现

`MediaFoundationM4aEncoder` 接收已经真正混合完成的 mono PCM16 WAV，通过项目已锁定的 NAudio.Wasapi 3.1.0 调用 Windows Media Foundation，输出 AAC/M4A。API 先在本地包 XML 中核对，再通过实际 Windows 构建与生成信号验证；未引入 FFmpeg、额外 NuGet 包或运行时下载。每个编码结果完整解码到 EOF，记录实际帧数/格式/字节、内容帧数和尾 padding。目标已存在时拒绝覆盖；临时输出只有全部校验后才发布；取消及异常删除本次唯一命名的临时派生文件，原始 WAV 不变。

默认 48 kHz/96 kbps；另实测 32 kHz/64 kbps 及 48→32 kHz 重采样。编码器严格核对本机是否支持指定码率，拒绝静默选择近似参数。为避免极短分片无有效帧，并保证最后内容完整，编码输入末尾增加 2048 帧零值，容器块另补 0–1023 帧；实际解码尾 padding 全部登记。此补齐只在派生文件末尾，不改变原会话内容时间轴。

`Publish-Portable.ps1` / Dev Publish 将 App 和 CaptureHost 各自发布为完整 win-x64 self-contained 文件夹，不裁剪、不 single-file，不包含开发 SDK。发布目录包含 portable 标记、主动测试页面、中文指南、许可、依赖来源/版本/包哈希及逐文件 SHA-256。签名 nupkg 的 SHA-512 对照缓存 `.nupkg.sha512`，另单独记录 NuGet 排除签名的 contentHash 并比对存在的项目锁文件。每轮使用新的 artifacts 子目录，不删除/覆盖旧包。

## 已执行的 codec 测试

- Windows 11 25H2 build 26200.9457 / x64；SDK 10.0.401，运行时 10.0.12；NAudio.Core/Wasapi 3.1.0。
- `mfaacenc.dll` / `mfplat.dll`：10.0.26100.9278；`msmpeg2adec.dll`：10.0.26100.8737。这是本机系统文件版本，不随包复制。
- 测试全部为新生成的伪随机滤波 PCM，**未播放、未枚举/激活采集设备、未录 mic、未读取真实会议音频**。
- 每个采样率验证 1 帧、20 ms、137 ms、3.217 s、30.125 s；完整解码到 EOF，源内容帧数保持，尾 padding 2048–3071 帧。
- 大于 100 ms 的样本检查前后 4096 帧的归一化相关性 >0.8，在 0–1536 帧候选偏移中最佳位置为 0；尾内容没有因另一来源/编码块结束而截断。
- 48→32 kHz 输入 154416 帧得到 102944 内容帧（允许最多 1 帧取整），没有可见时长损失。
- 提前取消、编码中取消、临时文件清理、目标覆盖拒绝、原 WAV SHA-256 不变等通过。
- 初始整合运行中上述两组正式 codec 测试 PASS；原始 JSON：`.local/TestResults/M2/initial/m2-codec/codec-evidence.json`，控制台：`.local/diagnostics/m2-20260929/tests-initial.txt`。后续最终运行路径见整阶段报告。

## 保留的失败和限制

首次完整 publish 后的脚本校验错把原始签名包 SHA-512 与 NuGet metadata contentHash 比较，导致 NAudio.Core 被误报 hash mismatch，脚本停止且未生成发布清单。经核对 NuGet 官方规范，metadata 的 contentHash 排除包签名，而缓存 `.nupkg.sha512` 对应整个签名包；原始包实际哈希与 `.sha512` 一致，metadata 也与项目锁一致。现已分别核对/登记，失败目录不作为交付，失败日志保留在 `.local/diagnostics/m2-20260929/publish-portable.txt`。

初步 `.local/codec-spike` 探索 20 ms MP3 时 Media Foundation 返回 0xC00D36C4（不支持该字节流），同时较长 MP3 测得 673 帧首端延迟。独立 spike 最初未捕获异常，导致用户看到 Windows 应用错误弹窗；随后补顶层异常捕获并以非零退出码记录，未更改系统策略。另一极短 1 帧相关性辅助计算曾越界，修复后通过；这些探索失败没有被计为通过。最终产品不提供 MP3 选项，仅 WAV/M4A。

一帧样本只证明编码/解码容器和帧边界处理，不证明有损编码能保留一个孤立样本的感知保真度；极短片不被算作首尾相关性验证通过。相关性仅对超过 100 ms、有足够非零内容的样本执行。

本机 Media Foundation M4A 解码首端偏移为 0；外部播放器/上传服务的 priming 处理未逐一验证，清单与界面保留时间映射近似警告，不承诺任意外部时间戳精确。原录音 QPC-only 能力、真实长时双轨同步、mic/系统状态未完成项不会因 codec 通过而变为通过。

完整便携发布不等于干净 Windows 验收。无预装 SDK/.NET 环境的启动、用户明确启动的录音、回放、压缩导出仍为 NOT_RUN；具体步骤见 [便携指南](38_PORTABLE_GUIDE.md)。本机未发现已可用的 Windows Sandbox/常用虚拟机入口，未为测试安装或改动系统功能。没有在当前 Windows 重新安装/禁用运行时来伪装干净环境。

## 首轮便携包结果（2026-09-29，本机，无采集）

该轮目录：`artifacts/ProjectEcho-win-x64-20260929-234631`；主程序 `ProjectEcho.exe`。App + CaptureHost 均为 win-x64 self-contained，包含 616 个 payload 文件，230433579 字节（约 219.76 MiB），另有 release-manifest.json。该清单 SHA-256：`0cd90de24b402b565307dff701ada4bf94c0c49ed888dd199ccf50c692749f64`；哈希另存相邻 `.manifest.sha256`。旧包保留作为历史候选，后续修复见下一节。

`Test-Portable.ps1` 实际 PASS：全部 payload 的大小/哈希、清单自身哈希、WPF 启动与默认隐私/来源枚举/合成 UI 控制状态、Host ready+idle-stop。子进程故意设置不存在的 DOTNET_ROOT，并检查 Host 的 coreclr.dll / hostfxr.dll 实际来自便携包 CaptureHost 目录。没有开始录音、启用 mic 或播放音频；本机结果不替代干净 Windows。证据：`.local/diagnostics/portable-20260929-234703/app-smoke.json`、`portable-smoke.json`；发布日志 `.local/diagnostics/m2-20260929/publish-portable-final.txt`。

## 新候选（2026-09-30，仍在联合验收）

在整合 Release 构建 0 警告/0 错误、89 组默认测试通过后，重新发布到 **`artifacts/ProjectEcho-win-x64-20260930-010447`**。包含真峰值保护、RF64 读取/分片、M4A 只读容器时间表核对及关窗相关修复；旧包与进行中的旧两小时测试 snapshot 均保持不变。

新包 616 个 payload 文件，230571032 字节，另有 release-manifest.json。manifest SHA-256：`003c72609812d29c4ed51320a302fad977f3bb30d90d15e4ed89394dc2d25ffb`。正常非管理员 `Test-Portable.ps1` 再次 PASS：哈希/默认隐私与 UI 状态、Host 空闲 IPC、包内运行时加载。没有启动采集或回放。证据 `.local/diagnostics/portable-20260930-010523`；发布与检查日志 `.local/diagnostics/m2-20260929/publish-portable-hardened.txt`、`test-portable-hardened.txt`。

新验证工具 snapshot 位于 `.local/M2ValidationTools/20260930-010618-12d6f3fe58874c5993a4bcb084aebc10`，先复制本轮 Tests Release，再覆盖新包 payload，616 项逐一比对哈希一致；身份记录在 `VALIDATION_SNAPSHOT.json`。它供后续 T34/T36/WPF/两小时新导出检查使用，**不是已经通过这些后续测试的证据**。

随包指南已说明普通 RIFF 首选回放、RF64 使用通过校验的完整 M4A、WMP 在本机对 M4A 返回整秒时长的限制。M4A 物理渲染首尾、两小时完整导出和干净 Windows 尚按联合验收进度记录；完整文件夹交付不表示 M2 全部退出闸门完成。详细算法与诊断见 docs/41、docs/43。

## 官方依据

- [NAudio 3.1.0](https://www.nuget.org/packages/NAudio/3.1.0)：Wasapi 分包包含 Media Foundation 编解码 API。
- [NAudio 官方 MIT 许可](https://raw.githubusercontent.com/naudio/NAudio/v3.1.0/LICENSE)：完整许可存于 tools/licenses。
- [Microsoft AAC Encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/aac-encoder)：PCM16 输入与 1024 PCM 帧/AAC 块。可用参数仍由本机运行时枚举/实测，不把历史文档表格当作本机支持保证。
