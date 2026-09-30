# M2 实现与验收记录（2026-09-29）

## 当前结论

**最终更新：2026-09-30。** 当前91组默认自动测试、两小时远端耐久、真实WPF首尾、受限外网并行导出、最新包长文件复验均通过。当前包 `artifacts/ProjectEcho-win-x64-20260930-014055`；后续新代码/证据以docs/41–44及STATUS为准。下方78/71等数字和“正在运行”保留为当时过程记录，不覆盖本节最终状态。M2现场退出闸门仍受干净Windows/双设备声学条件等限制，未标全部完成。

M2 的完整混音、WAV/M4A、大小/时长限制分段、本地安全导出、后台任务及便携发布流程已实现，验收仍在进行。不得将本报告的实现项等同于干净 Windows、真实平台及双设备长期同步已经通过。

用户已确认 M1 约30秒说话/界面核对通过；设备失联/拔插、锁屏/睡眠及关闭两项留待 M2 实现后联合测试。随后用户授权必要录音、固定 mic、环境配置与可行的长期验收；测试只使用受控合成进程，不入会、不解释 mic 内容、不转写、不上传。

## 已实现

- M1 恢复修复：损坏清单中的超范围数字不再中断有效检查点恢复；写入及重建均拒绝早于会话起点的有效 QPC。新回归覆盖四种坏数字、恢复副本和原件哈希不变。
- M2 从磁盘 journal、检查点和哈希重建时间轴。按来源分组多块、多段与格式，逐包使用实际 QPC 锚点，有限缓冲生成 48kHz mono PCM16 真混音。暂停/缺口保留时长，末尾不因较短来源消失；不依赖进程内对齐数据。
- remote/mic 固定各0.4增益，单/双声道平均降混，样本峰值保护。四倍窗函数 sinc 的合成峰值检查单独记录；没有将样本峰值或这一夹具检查冒充全部真实内容的 ITU 真峰值认证。
- Windows Media Foundation AAC/M4A，复用已锁定的 NAudio 3.1.0。只支持经验证的 WAV 与 M4A；MP3不进入用户选项。压缩后真实解码至 EOF，记录源帧、实际解码帧、补齐、格式及字节。详见 `39_M2_CODEC_AND_PACKAGE_EVIDENCE.md`。
- 确定性分段、零或指定重叠、首尾覆盖和时间映射。实际字节及包含 AAC 补齐的解码时长均须符合上限，否则有限缩短重试；非法参数、不可推进重叠和过多分片明确拒绝。WAV分片逐帧保持完整混音PCM。
- 导出只包含所选混音派生分片；时间清单默认不附，须显式选择。清单不含标题、实际录音日期、原件路径、mic原轨、会议URL、浏览器profile或诊断。原件与混音有只读锁及哈希校验；拒绝路径越界、ADS、重解析点和网络导出。
- 保存后自动排队混音，单个可取消/重试后台任务；下一场录音不等待导出。录音及Paused/Stopping期间回放始终禁用，异步旧会议结果不替换新会话的播放目标。
- 历史会议选择及录后归档；`filing.json` 单独保存标题/项目，不改原始 `meeting.json`、PCM或journal。修复“旧历史选择与新保存会议显示不同”时错写旧会议归档信息的问题。
- 便携入口由 `portable.json` 标识，默认数据在旁边 `Data`，可选择本地目录。专用Chrome仍独立user-data-dir，无远程调试、扩展安装或沙箱弱化。
- 完整 App + CaptureHost + 两者实际所需 self-contained runtime、许可证、官方依赖来源及全部文件哈希。开发机测试不能替代干净Windows验收。

## 实际验证与证据

Windows 11 25H2 / 26200.9457 / x64，项目 SDK10.0.401，运行时10.0.12，NAudio.Core/Wasapi3.1.0。本轮离线测试不访问采集或播放设备；真实 mic 预检独立列出。

| 检查 | 当前实际结果 | 证据 |
|---|---|---|
| Release整合构建 | 0警告、0错误 | `.local/diagnostics/m2-20260929/build-release-final.txt` |
| 默认自动测试 | 78组PASS；含9组M2导出、2组真实Windows离线codec、5组慢校验隔离、M1取消/恢复回归和声学检测器 | `.local/TestResults/M2/release-final/results.json` |
| 65.013秒多块双源混音 | 3,120,624帧；不同采样率、暂停、末段、原件哈希检查通过 | 上述目录 `m2-export/long-timeline/` |
| 已知合成时钟漂移 | 100秒、±1500ppm、首中末标记20ms窗口通过；不是真实两小时同步 | `m2-export/synthetic-drift-evidence.json` |
| Windows codec及生产导出服务 | 1帧至30.125秒、32/48kHz、首尾相关性、零首偏移、EOF、真实分段预算通过 | `m2-codec/codec-evidence.json` 及 `m2-export/real-m4a-export/` |
| WPF启动与状态守卫 | PASS，无Host/采集启动；含过期回执、过渡状态、Paused回放锁、后台任务与归档目标守卫 | `.local/diagnostics/m2-20260929/ui-candidate.json` |
| NuGet官方已知漏洞查询 | 六工程含传递依赖查询成功，无列出的已知漏洞；不是完整安全认证 | `.local/diagnostics/m2-20260929/dependency-audit-normal-user.json` |
| M1人工讲话/UI | PASS_USER_REPORTED，未虚构本轮重跑或精确设备元数据 | `.local/diagnostics/m2-20260929/environment-and-user-report.json` |
| 完整便携包 | 616文件，230,433,579字节；全部哈希、WPF启动、Host IPC、包内运行时解析通过；本机无采集 | `artifacts/ProjectEcho-win-x64-20260929-234631/`；`.local/diagnostics/portable-20260929-234703/` |

周期健康检查隔离后，首次固定Realtek mic短边界测试通过（501,600帧）；第二次mic自身无声明缺口，但测试来源播放进程exit1导致远端失效，整轮仍FAIL。随后仅将测试程序输出固定到已经枚举的Realtek数字端点，复用便携包内Host与导出DLL，mic短边界再次PASS（506,400帧）。未改变系统默认路由、音量或mic端点。两次成功只证明本轮短测未复现mic缺口，不能证明之前缺口的驱动/调度根因。证据分别为 `.local/M1MicrophoneIntegration/20260929-154514-0b34b7b49cd34004b54726eda8604610/`、`20260929-154600-a4fb5472e20c426184266115992675f9/`、`20260929-155047-086b48f061f544fbbf0368ed6a1cdcc5/`。

## 两小时耐久检查正在运行（尚未PASS）

便携包实际Host和导出DLL的远端短耐久测试已PASS，含全WAV/M4A与分片逐份EOF/标记/hash检查；mic从未开启。证据 `.local/M2LongRunIntegration/remote-only-short-20260929-155126-48813f52085e4fe5a06cef7824ef1478/probe-report.json`。

随后在2026-09-29 15:52:21 UTC启动两小时远端耐久，来源仅本次新建标记进程树，测试程序固定输出到Realtek数字端点。mic=false且帧数0，不修改系统音量/默认路由/电源策略。预计采集于17:52:23 UTC（香港9月30日01:52）之后结束，随后自动全长混音、M4A和20MB分片解码核验。运行目录 `.local/M2LongRunIntegration/remote-only-long-20260929-155221-b422ac2dea5b45abb39587895686c39a/`；完成前只看 `progress.json`，最终结果以 `probe-report.json` 为准。

测试副本位于 `.local/M2LongRunTools/20260929-235011-portable-payload-0743b9e205384fec893e18daadc8fd7f/`，顶层产品/依赖和完整CaptureHost逐文件与发布包哈希相同；实际Host模块来自该自包含副本。测试驱动仍运行在开发机，**不作为干净Windows或双设备同步通过**。

## 失败及限制保留

1. 初次探索20ms MP3时 Windows 拒绝解码，独立 `codec-spike.exe` 未捕获异常导致用户看到弹窗；另有极短相关性夹具越界。测试程序已补异常处理，MP3不作为交付格式；不得把这两次失败删除或记成通过。
2. 初次 M2 长时间轴夹具把double乘积向上取整，错误期望多一帧。实际磁盘时长650,130,000×100ns、输出3,120,624帧正确；改为精确已知值后通过，没有放宽尾帧标准。
3. 尝试添加 WPF `RenderTargetBitmap` 自绘诊断截图时进程返回 `0xC0000374`，底层原因未确认，未生成有效截图。已移除非必要截图检测，原无采集启动及状态检查重跑PASS；不声称视觉截图验收通过。见 `wpf-screenshot-attempt.json`。
4. 沙箱内NuGet查询被socket权限拒绝；用户授权的正常非管理员环境查询成功。构建可从项目缓存恢复并显式关闭构建时审计，单独成功的漏洞查询不等同全面安全保证。
5. 第一次26秒声学预检：选定进程远端三组标记可识别，Realtek mic没有可靠同标记。实际输出是AMD显示器端点，未改变系统音量/默认路由。mic有一次非首包不连续及7.9931ms声明缺口，完整性未通过；原件不变且进程退出。见 `.local/M2LongRunIntegration/short-20260929-152620-a46f0261921e40cb8aaae03d68ee378b/probe-report.json`。正在调查，不能把缺口隐藏或把QPC自洽当声学同步。

## 尚未通过的交付闸门

- 双设备两小时公共声学标记同步：须先取得可靠共同参考；不把单轨长录或合成漂移测试替代这一项。
- 设备物理拔插、锁屏/睡眠/关闭人工场景：按用户安排联合验收，未执行部分继续NOT_RUN。
- Zoom/Webex桌面真实短会、Chrome网页真实会及同时隔离对照：保留历史有限测试音证据，未自动加入私人会议。
- 无预装SDK/.NET的干净Windows：本机未发现已可用Windows Sandbox/虚拟机入口，不启用系统功能或假造干净环境。T46未通过；本机便携包启动/运行时隔离另记。
- 已校验但有明确缺口的final WAV会话可生成全时段混音并保留SourceStorageComplete=false，缺口填静音，不冒充全部声音录到。损坏、未确认或缺少正式manifest的崩溃恢复副本仍不能自动包装为完整录音。
- 原件校验、逐包渲染、哈希、编码和分片均传递取消令牌；校验按索引行/64KiB读取段检查取消，不把取消当作文件损坏。系统底层阻塞I/O仍需等该调用返回。

本报告随本轮后续真实测试和发布证据继续更新；最终阶段状态以 `STATUS.md`、`tests/acceptance.csv` 为准。


