# Convenience scripts
M3C：`--m3c-library-smoke` 和 `--m3c-transcript-smoke` 是 App 内的实际 WPF 测试入口，覆盖 SQLite 会库/筛选/删除确认和互斥、三语言、真实静音播放器更新频率、固定底栏、全文导出和复制；不用 computer-use。发布版可直接 `ProjectEcho.exe <测试开关>`；开发版经 `Dotnet.ps1 -Arguments @('exec',<App DLL>,<测试开关>)`。证据在 `.local/diagnostics/m3c-*`，只使用合成文件。
`Test-MicrophoneEndurance.ps1`：用户显式授权后，固定 `-MicrophoneId`、可选固定 `-OutputDeviceId`；`-Mode short` 运行40秒跨块检查，`-Mode long -QualificationReport <报告>` 运行真实7200秒并校验完整WAV/MP3和分片。mic原轨只留本地，不回放、不转写、不上传。设备保存/编解码资格与连续性质量分开报告，声明的缺口不能被当作同步或连续性通过。
`Test-SyntheticSpeech.ps1`：用已安装的 Windows SAPI 中文/英文声音生成固定本地合成语料，再调用明确提供的本地 CLI/model；只写 WAV 文件，不发声、不录音、不联网。报告生产识别链路、真实百分比和有限样本差错，不代表自然会议准确率。沙箱可能限制读取语音注册项，失败必须保留，不能修改注册表来绕过。
`Update-TraditionalUi.ps1`：仅开发维护时，把 `Localization/zh-CN.json` 的界面模板通过 Windows NLS 转为繁体资源；不会处理用户会议内容。运行产品只读取嵌入资源，不依赖此脚本。
`Install-WhisperLocal.ps1`: 仅供用户显式授权的本地环境配置，固定写入 `H:\AI\Whisper`；下载官方 b5130 CPU x64、多语言 base、公开 JFK 测试样本及许可，核对官方 ZIP SHA-256 与固定模型 SHA-256，生成来源和配置清单。产品不调用此脚本；不自动改 PATH/代理/运行时/应用数据目录，不上传内容。本轮已获用户授权并执行，见 docs/47。
M3 文件检查：默认 `Dev.ps1 -Action Test` 包含录音导入与 SRT/VTT 导出边界测试。显式真实模型 probe 为 `scripts/Dotnet.ps1 -Arguments @('tests/ProjectEcho.Tests/bin/Release/net10.0-windows/ProjectEcho.Tests.dll','--m3-real-whisper-probe','H:\AI\Whisper\whisper-b5130\Release\whisper-cli.exe','H:\AI\Whisper\models\ggml-base.bin','H:\AI\Whisper\samples\jfk.wav','--output','.local/TestResults/M3Whisper/new-run')`，输出目录须为新的项目内目录。没有录音/上传。
M3 WPF 集成：通过项目本地 Dotnet 包装器启动 App DLL，`--m3-whisper-smoke --audio <公开样本WAV>` 使用已保存配置执行真实UI识别；`--m3-replay-smoke --audio <公开样本WAV> --subtitles <对应SRT/VTT>` 导入文件并用实际WPF路由事件测试字幕定位，播放器静音，不改变系统音量，不采集。结果写 `.local/diagnostics/m3-*`。开发版直接 exe 需要可定位的.NET；便携版自带运行时。
`Test-MicrophoneIntegration.ps1`: `-List`或无ID仅枚举；显式`-MicrophoneId`启动约20秒真实mic短测（mic累计约10秒、录音上限30秒），远端限自建测试进程，不回放/转写/上传。默认Release。设备和mix采样率须匹配；跨速率时钟映射尚未验证。详情见docs/36。
`Test-M1Integration.ps1`: 默认Release，显式选择Recording（40秒）、Disconnect（2秒失联）、Crash（32秒Host故障）或Silence（真实15分钟数字静音）。只录本次新建测试进程、mic固定关闭，全部探针共享项目内互斥锁。错误返回非零并保留原件；数据保存与设备时钟限制分别报告。先构建对应配置，普通Dev Test不执行实采。
环境准备在Windows PowerShell 7.6.5实跑；其他宿主版本未验证。普通构建/测试不录音；显式 `Test-AudioIntegration.ps1` 会录制其自己创建的合成音进程，mic固定关闭。脚本不修改全局PATH/代理/执行策略、不安装驱动、不上传文件。实际证据见STATUS、docs/21及docs/33。
`Test-AudioIntegration.ps1`: 无参数、无GUI的固定受控实机录音检查，先等待Host started再播放6秒测试音；原件与JSON结果保存在.local/AudioIntegration。仅自建进程树，不接受外部PID/mic/端点fallback；任何时钟/信号失败返回非零。普通Dev Test仍不录放音。
`Dotnet.ps1`: 使用global.json指定的SDK，优先项目.tools；所有CLI缓存和临时目录留在.local，仅设置当前进程环境并在退出恢复。直接参数使用 `-Arguments @('--info')`。不自动安装SDK。
`Inspect-Environment.ps1`: 已实跑；记录build/UBR/架构/SDK，仍不代表激活或录音成功。
`Dev.ps1`: 已接入M0–M2实际解决方案。Test运行有显式断言的可执行套件，失败返回非零码；默认不录放音。Run启动WPF但不开始采集。Publish调用`Publish-Portable.ps1`生成完整App+Host+运行时+许可/哈希文件夹，仍不等于干净Windows验收。
`Publish-Portable.ps1` / `Test-Portable.ps1`: 分别在新的artifacts子目录发布完整win-x64包、验证包哈希及本机WPF/Host无采集运行时；拒绝路径越界/重解析点，不覆盖或删除旧包。实际结果见docs/39。
`Test-LongRunSynchronization.ps1`: 固定mic、测试进程及局部输出端点；先短声学资格测试，成功后才允许两小时双轨同步测试。另有测试运行器`--m2-endurance-probe`进行mic始终关闭的远端长录/导出检查；不替代双设备同步。
`Test-M0Host.ps1`: 构建后验证同用户named pipe握手、空闲Stop和心跳超时退出；从不发送Start，不创建音频采集客户端。不能替代真实录音中失联停采验收。
`Start-AudioProbeServer.ps1`: 开发诊断专用，复用已有 PowerShell 7，随机端口仅监听 127.0.0.1；只提供内存中的 tests/audio_probe.html。默认 15 分钟自动退出，可用 -DurationMinutes 1..60。启动输出 URL 和 .local/diagnostics 下的 ready JSON；不打开浏览器、不自动播放、不采集。用于 file URL 识别失败时的地址形式对照，不是工具权限替代方案，也不是录音运行依赖。其他文件/路径/方法及非本机 Host 会拒绝，禁止 mic/camera 和页面联网。
`Verify-Planning.ps1`: PowerShell 7下校验需求引用、提示词一致、JSON/Schema样例及脚本语法，只写.local诊断；不运行产品测试。
`Update-Guide.ps1`: PowerShell 7下从当前docs和FIRST_PROMPT重建离线指南，避免旧HTML仍显示过期需求。
`Open-MeetingBrowser.ps1`: explicitly user-invoked dedicated user-data-dir launcher. Creates a profile only on actual launch; -WhatIf previews. Does not identify a proven audio process root or prove tab isolation. Logs no meeting URL, although URLs necessarily exist in the browser launch command and should not be included in process diagnostic dumps.
`Verify-Handoff.ps1`: validates original delivered hashes only. Files change during development; that is expected. It is not a build/integration test.
Follow organization policies if scripts or profiles are restricted. Do not change execution policy to make them run.
