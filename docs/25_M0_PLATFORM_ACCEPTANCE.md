# M0 平台验收现场记录

日期：2026-09-28。用户已打开 Zoom 桌面、Webex 桌面及普通 Chrome 中的 Webex 网页，要求继续 M0 验收。

**Webex 后续：**用户确认第一份录制时未播放测试音，近静音结果按静音对照通过记录；第二份有声，回放由用户确认正常。mic 关闭、源为 CiscoCollabHost 进程树，设备位置异常单独保留。完整会议 T04 不升级为 PASS，见 [31](31_M0_WEBEX_SPEAKER_TEST.md)。

**测试范围更新：**用户指示暂时跳过后续浏览器测试。已有 Chrome 合成音实测保留，T05–T08 的网页/隔离未执行部分保持 NOT_RUN、注明用户暂缓，不再要求现在播放浏览器测试音。

**Zoom 后续：**用户已自行操作扬声器测试并录制两份新产物，均通过有声 PCM、原轨/混音和 checkpoint 一致性检查；最新一份回放由用户确认相同且正常。实际 Zoom 版本 7.1.9.48550，mic 关闭，设备帧计数异常仍保留；完整真实会议 T03 不升级为 PASS。见 [30](30_M0_ZOOM_SPEAKER_TEST.md)。

**后续实录：**用户手动播放合成音，代理实际操作 WPF 完成 10 秒 mic-off 专用 Chrome 树试录；原轨 479040 帧、660Hz 明显，10 个 checkpoint 正确。设备计数器恒零仍导致时钟完整性失败。带警告试听已实现并经 29 项合成回归，实际回放待复测；见 [29](29_M0_LIVE_SYNTHETIC_TEST.md)。这不是 Zoom/Webex 平台或多浏览器隔离 PASS。

**最新补充：**本轮旧试录离线审计 FAIL，发现 WAV checkpoint 偏移和设备帧位置异常；修复后 28 项合成 PASS，修复版 WPF 启动已验证。旧产物未改，新版采集/回放未执行。原生 Chrome URL 问题仍在；Playwright 列表获准返回 about:blank，但项目 file: 页被限制拒绝。详见 [28_M0_OFFLINE_CAPTURE_REPAIR.md](28_M0_OFFLINE_CAPTURE_REPAIR.md)；下方保留早期记录。

**最新状态：用户已明确授权 Codex 代为执行录音验证，包括 `24_M0_REPORT.md` 的“用户主动验证的最小步骤”。用户目前没有 mic，本轮保持 mic 关闭，先验证其他 M0 内容。无需再次等待用户按录音按钮或重复授权。** 尚未完成真实录音、保存、回放及平台音频验收，T03–T08 仍为 NOT_RUN。原 M0 构建和合成测试结果见 `24_M0_REPORT.md`，不覆盖真实平台检查。

最近两轮 Computer Use 操作因不能可靠识别 Chrome 当前 URL 而被保护机制强制结束；专用 Chrome 已打开项目本地 `file:///H:/AI/ProjectEcho/tests/audio_probe.html` 合成音页面，页面仅在点击后播放，未自动启动采集。当前正在排查工具 URL 识别问题，尚未确认原因或修复完成。授权不是当前阻塞项；不更改安全策略、普通浏览器资料或采集范围来绕过问题。

## 历史现场预检（早于代为录音授权）

最新工具诊断补充：Chrome 窗口绑定成功；恢复窗口和 HTTP 对照均未解决 URL 识别保护错误。配置中相关插件均启用，根因未知；已保存 [诊断交接](27_COMPUTER_USE_SUPPORT_PACKET.md)，待用户重启 Codex 后正常复测。临时 HTTP 服务已停止，本条不构成音频 PASS。

下表是首次来源选择和无采集预检的当时状态，不是当前窗口/PID/浏览器实例的保证；每次试录前仍须重新核对实际来源。

| 项目 | 实际观察 / 结果 |
|---|---|
| 运行环境 | Windows build 26200.9457 / X64，普通用户桌面；SDK 复用 10.0.401 |
| Zoom | 6.6.6.19875；发现真实音频 session 及多个相关进程，选定其共同 Zoom 根树；静态预检 PASS |
| Webex | CiscoCollabHost 46.9.0.35800；从音频 session 定位来源，未靠窗口标题或固定 EXE 猜测；静态预检 PASS |
| 来源状态 | 观察时 Zoom / Webex 的音频 sessions 均为 Inactive；不等于当前正在播放 |
| 输出设备 | 1 个活动输出端点，系统显示“远程音频”；这只是观察，不推断本地扬声器硬件能力 |
| mic | 0 个活动输入端点；样机 mic 保持关闭，未创建采集流 |
| Chrome | 153.0.8010.53；主进程没有显式独立 user-data-dir，没有检测到项目专用实例；样机当时没有 Chrome 音频 session 候选 |
| UI 安全状态 | 默认无自动来源、mic 关闭、无录音可回放；实际手动选择来源后预检不会自动开始 |
| 当时等待状态 | 样机已选 Webex 来源并完成预检，当时等待用户按“主动开始 10 秒试录”；后续明确授权已解除这项等待 |

首次从沙箱启动的窗口没有出现在用户交互桌面，已停止该开发进程。随后通过项目 Dotnet wrapper 的 `run --no-build --no-restore` 在普通用户桌面启动，Windows computer-use 已实际观察到界面。未提升为管理员、未修改系统设置。进程元数据的沙箱 WMI 读取被拒后，改在普通用户上下文只读查询 Chrome 并仅保留布尔范围结果；未保存完整命令行、会议 URL 或日常 profile 内容。

## 当前验收状态

| 追踪 ID | 状态 | 缺少的证据 |
|---|---|---|
| T03 Zoom 桌面 | NOT_RUN（真实会议）；客户端测试音录制/混音/回放已验证 | 真实会议远端音频、设备时钟质量；mic 因无设备另列 NOT_RUN |
| T04 Webex 桌面 | NOT_RUN（真实会议）；第二份客户端测试音录制/混音/回放已验证 | 真实会议远端音频、设备时钟质量；mic 因无设备另列 NOT_RUN |
| T05 Zoom / Chrome | NOT_RUN（用户暂缓） | 真实网页会议音频未验证 |
| T06 Webex / Chrome | NOT_RUN（用户暂缓） | 已有 Chrome 合成音证据不替代真实 Webex 网页电脑音频 |
| T07 / T08 Chrome 范围 | NOT_RUN（用户暂缓） | 普通 440Hz / 专用 660Hz 与同专用实例 880Hz 的同时对照试验 |

普通 Chrome 中已打开 Webex 网页，不满足专用浏览器前置条件。不能将整个普通 Chrome 根树的声音称为网页或单标签页隔离。后续已打开项目专用 Chrome 合成音页，但打开页面本身不证明音频隔离。保留普通窗口，不迁移 cookies、不复制登录资料、不自动入会。

## 本地证据

- `.local/diagnostics/m0-acceptance-20260928/source-environment.json`：观察时版本、来源身份、session state、Chrome 范围标志和端点数量；PID 仅为当次证据，不能据此复用到以后录音。
- `.local/diagnostics/m0-acceptance-20260928/ui-preflight.json`：实际 UI 中两次预检文本；均明确不采集。
- 经用户授权触发的录音将位于 `.local/M0Recordings/<本次会话>/`。未有产物前没有音频证据，不伪造 WAV/索引。

后续对产物只做本地结构、帧数、时间戳、哈希和音量/频率数值检查，不上传、转写或推断谈话内容。能够解码或含非零样本不能单独证明来源正确；没有实际回放证据就继续标记回放 NOT_RUN，未确认的人耳听感须单列。

## 历史离线验证记录

已在 `tests/ProjectEcho.Tests` 加入 `--audit-recording`。工具只读取项目内已存在的 M0 产物，拒绝越界/目录链接，不改原轨。核对 WAV 结构、帧数、索引中原始 data JSON 的哈希、sequence/checkpoint 水位、mic 关闭时是否有 mic 文件或索引，以及混音帧数；输出聚合值，不输出音频内容。索引哈希不是录制时的 WAV PCM 签名。

```powershell
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\<本次会话>')
```

该次测试工程实际构建成功，0 错误、4 个已有 NU1900；原 21 项合成测试复跑仍全部通过，日志为 `.local/diagnostics/m0-acceptance-20260928/regression-tests.txt`，结果位于 `.local/TestResults/M0/20260928-071856-283/results.json`。这不是本轮 URL 识别排查的新测试结果。

审计工具以明确标注 `TEST_FIXTURE_NOT_CAPTURE` 的合成夹具验证了四条路径：完整数据返回 `PASS_WITH_LIMITATIONS`；保留旧哈希的 packet 篡改被拒绝；缺少混音文件被拒绝；mic 关闭但存在 mic.wav 也被拒绝。前三份证据分别为 `.local/diagnostics/recording-audit-20260928-071959-477-dfb0aee5.json`、`recording-audit-20260928-072000-190-a93c5994.json`、`recording-audit-20260928-072000-907-4de99f67.json`。mic 关闭负向证据为 `.local/diagnostics/recording-audit-20260928-072052-026-4b489f4d.json`。这些文件不是采集结果。工具始终保留 `audiblePlayback=NOT_RUN`、`expectedSignalUnverified=true`；真实产物审计当前也为 NOT_RUN。

## 下一步

先诊断并在工具允许的范围内恢复 Chrome URL 识别，重新确认窗口、实际音频会话和进程树。根据现有明确授权，Codex 可以代为点击合成音或客户端扬声器测试音、ProjectEcho 的“主动开始 10 秒试录”、保存后回放等步骤；全程 mic 关闭，不进入私人会议。对可用路径继续取得原轨、混音和时间索引，再进行本地结构/频率审计。工具实际播放状态与用户听感须分开记录；没有实际证据的项目继续 NOT_RUN。

原先“必须等待用户本人点击试录”的安排属于明确授权前的历史记录，已被本轮授权更新。该授权不包含自动上传、开启 mic、扩大为全端点录音或绕过浏览器/工具保护。
