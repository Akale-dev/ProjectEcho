# M0 实施、验证与下一步

日期：2026-09-28。工作目录：`H:\AI\ProjectEcho`。结论：**实际产品样机已构建；M0 音频可行性闸门未完成。** 本轮没有启动任何真实采集、麦克风、浏览器或声音播放，没有进入会议。环境准备与无采集 smoke 不冒充音频验收。

## 已实现

`ProjectEcho.slnx` 包含 App、Core、Capture.Windows、CaptureHost、Export 和 Tests 六个实际工程。App 是 WPF；录音在单一 CaptureHost 进程，UI 使用当前用户受限的随机 named pipe。短试录由用户点击开始，最长 10 秒；构造窗口、刷新和预检不调用 AudioClient.Start。

- 从活动输出端点的真实音频 sessions 枚举音频进程及祖先候选；选定 PID、启动时间、可执行路径和树关系重新核验。每次录完清空进程选择，不保存后复用旧 PID。排除系统、shell、本程序等过宽根。故障不自动退回端点、不追踪替代 PID。
- 进程树范围与“指定输出端点所有应用声音”分开显示。端点模式必须勾选明确范围同意，改模式/端点后重置同意。
- mic 默认关闭，不创建或启动 mic 采集流。用户明确开启时展示独立确认，必须手动选择活动设备；不替换缺失设备。每场结束后回到关闭。M0 不提供会中 mic 切换或 Pause；可停止整次短试录。未来 Paused 状态已在播放互斥规则中禁止播放，不能据此宣称暂停采集已实现。
- 两路电平和帧计数，区分未收到音频包与 SILENT；无包/异常不扩范围。默认标题为时间、项目“未分类”，无需填写后才能录。
- 独立 Chrome 启动按钮使用已安装浏览器和每次唯一的项目内 `user-data-dir`，只打开本地合成音页面，由用户点击才播放。启动 PID 只是提示，必须重新选择实际音频树。明确标为实例全部声音，不叫单标签页。未读取/复制日常 profile、未安装扩展、未打开远程调试。
- 录音、停止保存期间禁用本程序回放。开始前关闭已有播放器，原轨和混音完成后才提供回放。本轮没有实际播放，防回录端到端验收仍未完成。
- NAudio 3.1.0 真实低层 API 在独立 MTA 线程读取帧数、device position、QPC100ns 和 flags。有界内存队列交给 writer，SILENT 不解引用音频指针。原轨独立 WAV、packet 索引含哈希/格式/边界/checkpoint；数据只写 `.local/M0Recordings`，不入 Git。
- 正常 Stop 请求使采集线程逐路停止、读取剩余包并截取可确认完整的截止前帧；writer 排空、WAV 收尾后回传结果。CaptureHost 启动期间仍处理停止与失联；异常终态先停采，UI 确认旧 Host 退出才允许下一场。实现不等于真实末帧验收通过。
- 录后短试录混合 WAV 逐包按 QPC 放置并叠加同时刻样本，保留经过时间、缺口及最长尾段；单/双声道 PCM/float 原轨支持经过合成验证。错误时间戳或同轨时间重叠明确拒绝混音，保留原件。没有压缩导出、漂移修正或 M2 发行承诺。

## 实际环境与依赖

| 项目 | 实测结果 |
|---|---|
| 目录 | H:\AI\ProjectEcho，仅修改该授权项目 |
| Windows | build 26200.9457；DisplayVersion 25H2；OS/当前进程 X64 |
| 系统名称边界 | 注册表 ProductName 原值为 Windows 10 Pro；未凭品牌字段推断音频能力 |
| 权限 | 非提升管理员令牌；根目录、src、tests、docs、.local 写探针成功 |
| 存储 | NTFS，审计时约 946.85 GB 可用；Get-Volume 访问被拒后用普通权限 DriveInfo 读取，未提权 |
| SDK | 复用 .tools/dotnet 中 10.0.401，与 global.json 严格一致；运行时 10.0.12 |
| 其他 dotnet | PATH 中已有 x86 runtime-only 安装，无 SDK；未重复安装 |
| Git / PowerShell | Git 2.53.0.windows.1；PowerShell 7.6.5 |
| NuGet | NAudio.Core/Wasapi `[3.1.0]`，传递 System.Numerics.Tensors 9.0.0；六工程 lock 文件落地且 locked restore 成功 |
| 构建警告 | 6 个 NU1900：当前网络无法获取 NuGet 漏洞数据；没有编译警告或编译错误，但安全审计未完成 |
| 无采集枚举 | 最终 1 个活动输出端点、0 个活动麦克风、0 个合格音频会话/树候选；需用户先播放合成音再刷新，不保存标识到共享报告 |

环境证据和权限范围见 [22_M0_ENVIRONMENT.md](22_M0_ENVIRONMENT.md)。真实包 XML、程序集反射、哈希、NAudio/Microsoft 原始来源及高低层接口取舍见 [23_M0_AUDIO_API.md](23_M0_AUDIO_API.md)。音频 API 激活与硬件时间戳质量均为 NOT_RUN。没有增加 C++ 工具链、驱动、服务或系统配置。

## 实际命令与结果

以下在项目目录的 Windows PowerShell 7.6.5 执行；日志均在忽略的 `.local`。所有 dotnet CLI 经项目 wrapper，不改全局 PATH/代理/执行策略。

```powershell
.\scripts\Dev.ps1 -Action Restore
.\scripts\Dotnet.ps1 -Arguments @('restore','ProjectEcho.slnx','-r','win-x64','--locked-mode','--configfile','NuGet.Config')
.\scripts\Dev.ps1 -Action Build
.\scripts\Dev.ps1 -Action Test
.\scripts\Dotnet.ps1 -Arguments @('run','--project','src/ProjectEcho.App/ProjectEcho.App.csproj','--no-build','--no-restore','-c','Debug','--','--smoke-test')
.\scripts\Test-M0Host.ps1
```

| 检查 | 结果 | 本地证据 |
|---|---|---|
| 正式产品 Build | PASS；0 错误，6 NU1900 | `.local/diagnostics/m0-build.txt` |
| 精确声明与 locked restore | PASS；漏洞查询警告保留 | `.local/diagnostics/m0-locked-restore.txt` |
| 合成算法及真实 NAudio WAV 写读 | 21 PASS / 0 FAIL；不采集 | `.local/diagnostics/m0-test.txt`、`.local/TestResults/M0/20260928-064328-406/results.json` |
| 实际 WPF 启动/枚举 | PASS；默认 mic 关、无自动来源选择、未启动 Host | `.local/diagnostics/m0-wpf-smoke.json` |
| Host 无采集 IPC | 2 PASS：空闲 Stop、无心跳退出；从未发送 Start | `.local/diagnostics/m0-host-smoke.json` |
| 人工 Windows 音频/回放 | NOT_RUN | 尚无用户主动录音证据 |

21 项测试包括生产使用的 QPC 单位/大整数换算、完整截止前帧数算法、磁盘预算、播放状态互斥、同时双频混音/最长尾/缺口、44.1kHz 不规则包边界、无包与静音区别、原件防覆盖和坏数据拒绝。另用锁定 NAudio 的 WaveFileWriter 实际生成 Float32、Extensible float/24-in-32 PCM、奇数 data 长度 PCM24，再由 NAudio 与混音器回读；测试确认其 RIFF pad 兼容。PID 测试只读当前测试进程元数据并合成错误身份；不等于真实 PID 复用场景通过。

## 平台验收与未测试项目

| 场景 | 选定模式 | 状态 |
|---|---|---|
| Zoom 桌面 | 尚未选择；目标进程树 | NOT_RUN |
| Webex 桌面 | 尚未选择；目标进程树 | NOT_RUN |
| Zoom / Chrome 网页 | 尚未启动专用实例；目标实例进程树 | NOT_RUN |
| Webex / Chrome 网页 | 同上 | NOT_RUN |
| Chrome 440/660/880Hz 隔离/范围 | 尚未播放或采集 | NOT_RUN |
| Edge 网页 | 后续独立验收 | NOT_RUN |
| 端点兼容 | 尚未授权并选定端点录音 | NOT_RUN |
| 独立 mic | 未枚举到活动设备，未启用 | NOT_RUN |

T01 环境与 T02 实际 API 编译闸门记 PASS；T03–T57 保持 NOT_RUN。有关合成测试只作为辅助证据列入验收矩阵，不将其升级成真实采集、恢复或 M2 通过。

M1 尚需做真实 Stop 末帧/跨块/重复 Stop、MicOff/Pause 队列边界、30 秒块、从磁盘离线重建/恢复、实时 health、写失败和两小时漂移，以及录音中 UI 失联保护。M0 索引虽保存时间证据，未实现 M1 recovery reader；原轨 `.partial` 不代表已验证崩溃恢复。M2 尚无压缩文件、大小分段、完整 self-contained 包或干净 Windows 检查。

## 审查修复与证据边界

独立代码审查发现并修复：启动连接/激活期间停止意图丢失；Host 异常终态早于实际停采；本程序实际进程名被误列为可选祖先；锁文件虽解析 3.1.0、声明仍允许更高版本的问题。NAudio 首包 DATA_DISCONTINUITY 原值保留并警告，首包没有前包可供检验，不凭该标志单独判定结束失败；后续 discontinuity 会报告不完整。这是可见的样机策略，未经硬件验收。

仓库本轮开始时没有已跟踪文件/提交基线，因此普通 `git diff` 不能代表完整改动审查。已对新增源码/脚本做独立审查、编译及对应测试；最终文件清单/哈希和文本空白检查放在 `.local/diagnostics/m0-review.json`。未提交、未设身份、未 push。合成 WAV、SDK、缓存和未来录音均为忽略项。

## 用户主动验证的最小步骤

1. 运行 `scripts/Dev.ps1 -Action Run`。这只打开界面，不启动采集。直接双击开发 EXE 可能因本机没有全局 x64 运行时而无法启动；本轮交付使用项目 SDK wrapper，M2 才交付完整便携包。
2. 先保持 mic 关闭。点击“打开专用 Chrome 合成音页”，选择 660Hz 并由你点击播放；回到样机刷新音频会话，按启动提示选择正确 Chrome 根树，核对范围，点击预检。合成音只持续 10 秒，需要时再次主动播放后立即开始短试录。
3. 点击“主动开始 10 秒试录”，观察远端帧数和电平，等待停止并保存；有完整混合 WAV 才可点击回放。错误会显示原件路径和原因，不自动更换来源。
4. 另在日常 Chrome 主动播放 440Hz 作对照，选定专用树只应录入 660Hz；再在同一专用实例打开第二个 880Hz 页，应承认会一起录入。此操作需由你执行，本轮没有代为启动。
5. 如需双轨，先接好/启用你选择的麦克风并刷新，明确勾选独立 mic、确认提示、选择设备，再进行另一场短试录。不要把会议软件 mute 当作本程序 mic 关闭。
6. 合成试验确认后再由你决定实际 Zoom/Webex 试会；每个场景独立记录 OS、来源模式、mic 状态、结果和本地 evidence path。无需上传音频或会议链接。

这一步完成后才能决定 M0 是否通过及进入 M1；本报告不将未执行步骤计为成功。
