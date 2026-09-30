# T27 真峰值保护修复与证据

2026-09-30。原先仅对样本值留 headroom/限幅并不能满足真峰值要求。审计反例中，两路同相 `[1,1,-1,-1]` 各乘 0.4 后样本峰值只有 0.8，但样本间重建峰值可超过满刻度。此前版本不能因此被记作 T27 通过；当前变更生成新派生结果，不覆盖原轨或已有混音。

## 实现

`TruePeakMeter` 使用 ITU-R BS.1770-5 Annex 2 提供的 4 相、每相 12 系数 FIR。在浮点域计算，保留 DC 和跨读取块的滤波状态；结束时补零排空滤波尾部，但这些零不计入内容帧数。计量取四相绝对峰值和原始样本峰值的较大者。静音的线性峰值为 0，dBTP 写 null，避免 JSON 中出现 Infinity/NaN。[ITU-R BS.1770-5 Annex 2](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.1770-5-202311-I!!PDF-E.pdf)

规范附件给出 4× 采样网格在 fs/2 条件下最大约 0.688 dB 的低估；因此实际处理目标为 **-2 dBTP**，较产品要求的 **-1 dBTP** 留 1 dB 余量。WAV 若需衰减再加 0.01 dB 量化余量，并在写盘后全文件复核。采用固定全文件衰减，不做响度归一化、剪切、暂停删除或用户音量编辑器。完整 WAV 及分片切口的保护由导出服务分别调用，源文件只读、目标必须全新，帧数与 RIFF/RF64 容器长度不变。[ITU-R 采样误差表](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.1770-5-202311-I!!PDF-E.pdf)

AAC 编码后请求 Media Foundation **float32** 解码，避免转换成 PCM16 时先裁掉编码器过冲而掩盖峰值。逐帧解码至 EOF 后执行同一检测；超出 -2 dBTP 时从不变的原 WAV 衰减并重新编码，最多四次。每次仍检查内容帧数、padding、格式和实际字节，失败不会发布；结果记录 `TruePeak`、`AppliedGain`、`EncodingAttempts`。源 WAV/RF64 通过新的 `M2WaveReader` 和 NAudio `RawSourceWaveStream` 读取，不依赖会溢出的 32 位 WAV chunk 解析。

## 实际测试

Windows 11 25H2 build 26200.9457 / x64，项目 SDK 10.0.401，.NET 10.0.12，NAudio 3.1.0。独立测试入口只处理新生成的合成 PCM，未录音、播放或触碰正在运行的两小时 CaptureHost。

- 4 组 `TruePeakTests`：EBU 15–19 型频率/相位解析参考、跨块/EOF/静音/无效值、WAV 新副本保护、60 秒有界流式处理。
- 4 组 `M2CodecTests`：既有完整时长/短片/重采样、取消/覆盖保护，以及新增 AAC 解码后真峰值、有限重编码与原件哈希、RF64 实际 codec 链路。
- 解析频率/相位样本按 EBU Tech 3341 对应容差核对；这是自行合成的测试，**不是已下载并执行完整官方 EBU 测试集，也不是 EBU 认证**。[EBU Tech 3341](https://tech.ebu.ch/docs/tech/tech3341.pdf)
- 独立参考使用 **32 相、128 样本 Blackman-windowed sinc**，没有复用生产 FIR 系数、状态环或计量实现。覆盖相位偏移、fs/4、fs/3、0.45fs、0.49fs、首尾脉冲、静音；保护后 WAV/AAC 都不超过 -1 dBTP。
- 样本峰值约 0.8 的反例经保护后，生产检测约 -2.0099 dBTP，独立参考约 -2.0036 dBTP。AAC 同类反例实际触发第二轮编码，最终生产检测约 -2.0778 dBTP，独立参考约 -2.1180 dBTP。
- 所有保护过程源文件 SHA-256 不变；内容帧数及 AAC 尾 padding 不变。静音增益为 1。取消信号在首个有界读取块后生效，不继续读取剩余 PCM。
- 额外实际用 4096 帧小 RF64 文件经过保护和 Media Foundation 编码/浮点解码，ds64 与 80 字节容器头保持、源哈希不变、内容帧完整。大 RF64 全文件压缩尚未执行，不能用小文件结果冒充；跨 4 GiB WAV 读取/分片另有测试证据。
- 60 秒 / 2,880,000 帧合成流实际处理约 0.1304 秒，线程分配 33,184 字节；读取缓冲 32 KB，FIR 历史 24 个 double。该小基准证明本次处理使用有界缓冲，**不是已经执行两小时文件验证**；真正长文件结果由后续联合验收记录。

上述最终 8 组独立运行全部 PASS。证据（`truepeak-final` 保留第一次 7 组运行，`truepeak-rf64-final` 为含 RF64 的最终 8 组运行）：

- `.local/TestResults/M2/truepeak-final/truepeak/truepeak-evidence.json`
- `.local/TestResults/M2/truepeak-final/truepeak/streaming-performance.json`
- `.local/TestResults/M2/truepeak-final/m2-codec-truepeak/aac-truepeak-evidence.json`
- `.local/TestResults/M2/truepeak-rf64-final/m2-codec-rf64/rf64-codec-evidence.json`

最终整工程构建/全部测试/新便携包与长文件验收由 docs/37、STATUS 和 acceptance.csv 汇总；本页不把尚未执行项目标为通过。这里的真峰值仍是指定重建滤波器的估计值，未承诺任意播放设备、任意外部有损再编码或所有病态无限带限序列的数学精确上限。
