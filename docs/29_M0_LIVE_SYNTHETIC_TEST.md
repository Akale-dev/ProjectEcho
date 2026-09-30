# M0 专用 Chrome 合成音实录

2026-09-28，Windows 26200.9457 / 25H2 / X64，项目 SDK 10.0.401，NAudio.Core/Wasapi 3.1.0。

## 本次实际操作

用户手动打开项目 `tests/audio_probe.html`，明确回复已播放 660Hz／60 秒；代理只操作 ProjectEcho WPF，没有操作被限制的浏览器页面。此前录音代操作授权持续有效，全程 mic 关闭。

重新枚举实际音频 session 后，选定项目专用 Chrome 根树。只读核对其启动时间及项目内独立 user-data-dir；未使用禁用 sandbox、远程调试端口或管道。选择的是整个实例进程树，不是单标签页。静态预检通过后，代理点击主动 10 秒试录。

启动期间来源选择、mic、回放和停止回放控件均禁用；停止保存控件可用。收尾后来源选择被清空、mic 仍关闭，未复用 PID。未使用全端点兼容模式。

## 第一轮实录结果

产物：`.local/M0Recordings/20260928-135814-1426238c72094bcb836c05dcec4fbfeb/`。

| 检查 | 实际结果 |
|---|---|
| 实际 process-loopback 激活与原轨写入 | 已执行，存在真实非零 PCM；仅本合成场景 |
| 原轨 | 48kHz 双声道 float32，479040 帧，998 包，PCM 9.980 秒 |
| 会话经过时间 | 10.0009258 秒；与 PCM 长度差不被剪掉或宣称为完整同步 |
| 660Hz | 十个分析窗均约 -32.04 dBFS，峰值约 0.025，RMS 约 0.01765 |
| API QPC | 没有无效标志；本数据相邻 packet 的 gap/overlap 统计为 0 |
| 设备帧位置 | 全为零；首包零合法，之后 997 包标为不可依赖 |
| checkpoint 修复 | 十个 checkpoint 全部一致；实际 data 起点 58 字节，偏移错误数 0 |
| mic | UI 关闭，产物无约定的 mic 轨/文件/索引；不能仅由磁盘证明全部内存行为 |
| 收尾 | 完成原轨 WAV，0 个 CaptureHost 运行；清单保留 Complete=false 和设备时钟警告 |
| 录后回放 | 第一版因 Complete=false 未生成混音，未实际播放 |

第一轮结构审计总结果 **FAIL**：`capture_incomplete`、997 次 `device_position_unreliable`、997 次 `device_overlap`、缺少混音，共 1996 项。没有 checkpoint 错误，不得把全局 FAIL 误解为 WAV 头修复未生效。没有修改审计阈值、删除错误、修改原件或把设备位置改成自造累计帧。

原轨有声与 660Hz 数值证据不能替代人工听感，亦不能证明普通浏览器 440Hz 被排除、同实例 880Hz 被包含或 Zoom/Webex 平台兼容。T03–T08 保持 NOT_RUN；本合成场景各子项单独列出。

本地证据：

- `.local/diagnostics/m0-live-synthetic-20260928.json`：授权、当次进程身份/范围布尔标志及 UI 观察。
- `.local/diagnostics/recording-audit-20260928-140009-928-55cdb172.json`：结构/时钟审计。
- `.local/diagnostics/tone-report-20260928-140010-245-02b2b850275b4beca5d7a319bdf5363e.json`：原轨数值测量。

## 带警告试听实现

录音质量失败与是否可以试听已收尾数据需要分开。保留原始 `Complete=false`、原事件及警告，新终态 `SavedIncomplete` 仅在以下步骤全部成功后开放试听：

1. 已确认原 CaptureHost 退出，且仍属当前会话。
2. 存在有效数据；remote/mic 路径为该会话的已收尾 WAV，无对应 `.partial`。
3. 每包 API QPC 有效；现有混音器通过 WAV 格式、长度、包帧偏移、时间不重叠和有限样本检查。
4. 异步生成唯一 `mixed-preview-incomplete-<guid>.wav` 后，再次核对 Host/会话状态。

期间仍是 Stopping，禁开始/回放；任一检查失败回到 Faulted 并禁播。普通 Faulted 未被放开，Recording/Paused/Stopping 也仍禁播。按钮显示“试听混音（有警告）”，状态说明完整性未通过；不把试听升级成时钟、末帧或平台验收。

实际构建 0 错误、6 个已有 NU1900；当时 29 项合成测试 PASS。新增回归验证 QPC 可用而设备位置不可用时，可生成不改原件的混音且保留失败/警告；无效 QPC 仍拒绝。日志：`.local/diagnostics/m0-preview-build-20260928.txt`、`m0-preview-tests-20260928.txt`；结果 `.local/TestResults/M0/m0-preview-20260928/results.json`。第二轮实测见下节。

```powershell
.\scripts\Dev.ps1 -Action Build
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--output','.local/TestResults/M0/m0-preview-20260928')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','src/ProjectEcho.App/ProjectEcho.App.csproj','--no-build','--no-restore','-c','Debug')
```

所有检查都在当前项目，未上传、未 push。M0 仍在进行；M1 的恢复、边界/两小时同步以及 M2 导出/便携包未由此次短试录替代。

## 第二轮实录与人工听感确认

用户再次播放合成音。代理重新枚举并选择本次活动的专用 Chrome 根树后，窗口输入工具反复返回 `window bounds changed` / `user input was detected`，未成功点击开始。用户随后明确回复“已点击开始试录”；因此本轮开始操作者记录为用户，不记为代理成功点击。

产物为 `.local/M0Recordings/20260928-141656-810e9c67d6194bd8a09c512f7dd6399b/`：

| 检查 | 实际结果 |
|---|---|
| 原轨 | 479520 帧、999 包、48kHz 双声道 float32；9.990 秒 PCM |
| 会话时间 | 10.0098824 秒 |
| 混音 | `mixed-preview-incomplete-a6c3444c4bf643eda8e92233604a1acc.wav`；480475 帧、10.0098958 秒，符合完整经过时间轴 |
| 频率/电平 | 十个整秒窗 660Hz 约 -32.05 dBFS；混音峰值约 0.024963、RMS 约 0.017616 |
| QPC / checkpoint | 无无效 QPC；相邻包 gap/overlap 统计为 0；10 个 checkpoint 均正确；混音帧长检查通过 |
| 时钟限制 | 998 包设备位置不可依赖；Complete=false；完整性审计仍 FAIL，1997 项错误仅为捕获未完整标记及设备位置错误 |
| mic / Host | mic=false，无 mic 轨；回放前 CaptureHost 数量 0 |
| UI 终态 | 来源选择清空、mic 关闭；显示完整性警告，“试听混音（有警告）”启用 |
| 回放 | 等原测试音结束后，代理实际点击试听按钮；用户明确确认“听到了，连续且正常” |

**本合成场景的有声采集、原轨与派生混音落盘、带警告回放已获得实际证据；设备时钟完整性与完整 M0 平台矩阵未因此通过。** 磁盘检查与用户听感分别记录。

新增证据：`.local/diagnostics/tone-report-20260928-141945-995-45e21f104c574bad942930f12c8b35cb.json`、`recording-audit-20260928-142107-931-e55530ed.json`、`m0-live-preview-confirmed-20260928.json`。

审计工具原先只接受 `mixed-preview.wav`，现已兼容不完整会话的唯一合法 `mixed-preview-incomplete-<Guid:N>.wav`。它仍拒绝多候选、非法名字、目录链接及残留 `.partial`；不会清除 `capture_incomplete` 或设备位置错误。新增选择器回归后测试工程与依赖实际构建通过，**30 PASS / 0 FAIL**，日志 `.local/diagnostics/m0-preview-audit-tests-20260928.txt`；结果 `.local/TestResults/M0/m0-preview-audit-20260928/results.json`。

## 桌面平台后续入口检查

回放确认后，代理查看当前 Zoom 桌面并打开设置。设置子窗口可在截图看到，但 `list_windows` 只返回 Zoom 主窗口，未提供设置窗口身份；对音频分类的点击被工具拒绝，理由为点击位于设置窗口而非绑定的主窗口。未以猜测句柄、其他输入通道或窗口注入处理；未播放 Zoom 测试音、未入会、未启动新的录音。需用户手动进入 Zoom 音频页，或工具恢复对子窗口的正常绑定后继续。当前未看到 Webex 桌面窗口；Zoom/Webex 平台验收仍 NOT_RUN，不从 Chrome 合成结果推断支持。
