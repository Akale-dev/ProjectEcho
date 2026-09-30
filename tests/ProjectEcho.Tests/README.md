# M0–M2 自动测试与显式Windows实录探针

2026-09-29：默认78组通过，包含M1控制/恢复/周期校验隔离、M2磁盘重建混音、安全分片及真实Windows M4A编解码。当前证据 `.local/TestResults/M2/release-final/results.json`。默认测试不打开音频设备、不播放、不录mic；需要Windows Media Foundation才能验证压缩格式。

`scripts/Test-LongRunSynchronization.ps1` 是显式声学同步入口：先short核验公共标记，long强制要求同mic、同输出端点近期合格报告。`--m2-endurance-probe` 是独立远端耐久入口，mic始终关闭，不能替代双设备同步。实录须经用户授权；原件和失败证据均保留在`.local`，无上传。

以下保留旧入口与历史测试说明；当前阶段以docs/37为准。

当前默认套件覆盖M0与M1，输出到`.local/TestResults/Current`；显式`--output`保持兼容。M1控制/恢复、实际junction和时钟能力检查均有合成测试；这些不冒充真实mic/两小时同步。实机入口及边界见 [35_M1_IMPLEMENTATION_REPORT.md](../../docs/35_M1_IMPLEMENTATION_REPORT.md)，使用 `scripts/Test-M1Integration.ps1 -Scenario Recording|Disconnect|Crash|Silence`。Silence实际等待15分钟，其他短探针不依赖GUI。

此工程不使用外部测试框架。**默认测试**不枚举、激活或读取音频设备；输入WAV由测试代码生成。引用已锁定的NAudio 3.1.0，不增加包版本或工具。默认执行成功只代表相应合成检查通过。

另有显式实机入口 `scripts/Test-AudioIntegration.ps1`（底层 `--synthetic-audio-probe`）：自动创建测试音进程，经生产Host录制，仅ProcessTree、mic固定关闭。必须等started才播放，自动验证并保留失败；普通Test不执行它。使用和本轮真实结果见 [33_AUTOMATED_AUDIO_PROBE.md](../../docs/33_AUTOMATED_AUDIO_PROBE.md)。

在项目根目录运行：

```powershell
.\scripts\Dev.ps1 -Action Test
```

直接调用已构建工程：

```powershell
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug')
```

默认在 `.local/TestResults/M0/<UTC 时间戳>/` 生成合成原轨、派生 WAV 和 `results.json`。可在 `dotnet run` 参数末尾附加 `-- --output <本项目内的全新目录>` 指定证据目录。任何测试失败时进程返回非零代码；重复使用已有文件的证据目录会失败，避免覆盖测试证据。

覆盖播放状态互斥、默认命名、QPC 单位和大整数转换、完整帧边界计算、按原轨格式计算磁盘需求，以及逐包混音的同刻叠加、较长尾部、真实时间空缺、重采样、数据异常与原件保护。Core 的 QPC 和边界算法同时供 Windows CaptureSession 调用。

Windows 合成集成用例使用与真实 TrackWriter 相同的 NAudio WaveFileWriter 构造/Write/Dispose 路径，覆盖 Float32、WAVEFORMATEXTENSIBLE Float32/24-in-32 PCM、奇数字节 PCM24 data 及 RIFF pad，随后用 NAudio 回读和生产 TimelineWaveRenderer 混音验证。身份用例读取当前测试进程自身元数据，验证改变启动时间/路径和不存在 PID 被拒绝，并检查 ProjectEcho 实际程序集名与 shell 不可作为录音根；不启动来源进程或音频客户端。

这些结果不是 T11/T18/T24/T51/T52/T55 的 Windows 设备端到端验收：未触发任何真实录音，未验证 mic 关闭时系统实际不采集、客户端 Stop 剩余缓存、两小时漂移或播放器与采集设备互斥。对应硬件项仍须用户主动试录，并单独保留 NOT_RUN/PASS/FAIL 证据。

`TimelineWaveRenderer` 是 M0 短试录的录后回放工具，最多 60 秒，支持单/双声道 PCM16/24/32 和 Float32 原轨，输出 48 kHz 双声道 PCM16 WAV。两路使用固定各 0.5 增益留混音余量，一路使用单位增益；不修改原轨。API 时间戳无效、同轨包时间重叠、格式不符和不完整 PCM 都会明确拒绝生成。M1 的崩溃恢复和 M2 的长录音漂移处理、完整导出/压缩能力不由此测试替代。

## 离线 M0 产物审计

2026-09-28 修复后共 28 项合成用例：新增直接调用生产 `CaptureWaveFile` 的五种 WAV checkpoint/payload 回归，以及原始位置健康检查和新旧 packet JSON 兼容性检查。旧逻辑的五种格式均已重现偏移错误，修复后通过；不是新录音、浏览器或 M1 恢复验收。证据与命令见 `docs/28_M0_OFFLINE_CAPTURE_REPAIR.md`。

在已构建运行命令后附加 `-- --audit-recording <项目内会话目录>` 可读取已有 `capture-result.json`、原轨、混音及 JSONL 索引，输出聚合报告到 `.local/diagnostics/recording-audit-*.json`。不修改原件、不采集/播放/联网。缺少文件或结构不一致返回非零退出码。

`PASS_WITH_LIMITATIONS` 仅表示本地文件结构检查通过，不能证明来源正确、隔离、实际试听或 mic 内存隐私。报告始终保留 `audiblePlayback=NOT_RUN` 和 `expectedSignalUnverified=true`。本次完整、篡改哈希、缺混音、mic 关闭但有 mic 文件四条检查器夹具验证记录见 `docs/25_M0_PLATFORM_ACCEPTANCE.md`；真实平台结果仍须独立验收。
