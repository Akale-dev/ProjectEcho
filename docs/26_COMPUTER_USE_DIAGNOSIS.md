# Chrome 自动化 URL 识别问题

日期：2026-09-28。范围：诊断 M0 验收所用工具；不代表 Chrome 音频捕获失败。

最新补充：重启 Codex 和电脑均已执行，原生 Chrome 状态读取仍报相同错误；不再重复要求重启。用户明确授权枚举全部标签页后，Playwright 正常审批通过，返回其 about:blank；后续 file: 导航被协议限制拒绝，未换通道绕行。WPF 可正常操作；本轮另完成旧音频产物审计和实际代码修复，见 [28](28_M0_OFFLINE_CAPTURE_REPAIR.md)。下文保留前序诊断过程。

## 最新结论：文件页与 HTTP 页均失败，工具端尚未修复

用户将专用 Chrome 导航到已验证的 HTTP 对照页后，重新枚举、绑定窗口成功，但 `get_window_state` 仍返回相同的 URL 识别保护错误。本地文件协议不是充分解释；不再要求用户重复恢复窗口或更换地址，也不改名、重启 profile 或改用其他输入渠道规避检查。

本轮完成日志与配置只读诊断，已生成 [27_COMPUTER_USE_SUPPORT_PACKET.md](27_COMPUTER_USE_SUPPORT_PACKET.md) 和忽略目录中的脱敏 JSON。尚无确切根因、已知修复版本或可调用的安全修复接口。下一步是用户完全退出并重新打开 Codex，以正常方式恢复连接后验证一次；这是恢复尝试，不是已证实修复。

用户恢复窗口后，直接状态读取再次返回同样的 URL 识别保护错误。随后用户提供截图，地址栏明确显示文件标记和 `H:/AI/ProjectEcho/tests/audio_probe.html`。因此，之前检测到的最小化是当时的窗口状态，不能作为本错误的根因；“当前地址栏不可见”也不符合截图证据。

已只读核查插件可用文本：仅有文档及 manifest，没有可读的 URL 解析实现，也没有声明 `file:` 限制。此前曾提出“本地文件路径/协议的识别兼容问题”假设；HTTP 对照也失败后，该假设不能单独解释现象，不能据此要求安装扩展。

历史对照使用相同 HTML、正常访问检查验证地址形式。HTTP 服务的 6 项检查通过，Chrome 工具状态读取仍失败。临时服务器现已停止，原端口 7733 已确认关闭；正式产品浏览器启动器未改动。

## 已证实

- 前两轮 Computer Use 返回：`could not determine the current browser URL on Windows with enough confidence to enforce policy`，并终止当轮操作。录音授权没有解决此项工具识别问题。
- 用户已明确授权代为执行短录音验证，mic 仍关闭。授权不是当前阻塞项。
- 项目启动器 `src/ProjectEcho.App/DedicatedBrowserLauncher.cs` 使用正常 `--new-window`，每次独立 `--user-data-dir`；没有 app/kiosk/fullscreen、调试端口、禁用 sandbox 或隐藏地址栏参数。
- 目标地址为项目本地 `file:///H:/AI/ProjectEcho/tests/audio_probe.html`。页面没有自动播放、导航、弹窗或全屏代码。
- 本轮浏览器连接库存只返回 Codex In-app Browser，标签页为空；未返回已连接 Chrome。该事实只说明当前浏览器连接不可用，不证明原生 Windows 工具必须依赖该连接。
- 早先看到的当日日志为 0 字节；本轮再次读取时日志已出现内容。记录到当前聊天的 8 条内置浏览器 `getInfo` 路由错误，不能据此认定原生 Chrome URL 错误的因果关系。
- 日志所示桌面包版本 `26.924.2738.0`、app-server `0.158.0-alpha.2.1`；Computer Use manifest 为 `26.924.22138`。不同组件版本号不同不是版本不兼容的证据。
- 只读配置确认 `computer-use@openai-bundled`、`unified-computer-use@openai-bundled`、`browser@openai-bundled` 均 `enabled=true`。该配置无 `computer_use` 或 `browser_use` 覆盖段；不能由此推断组织策略、桌面许可或连接均正常。
- 本轮重新枚举仍找到唯一的 ProjectEcho 合成音 Chrome 窗口和 WPF 样机窗口。
- 分步诊断确认 Chrome `get_window` 成功；直接调用 `get_window_state`（省略激活）返回 `window is minimized; call activate_window, refresh with get_window, then retry get_window_state`。WPF 对照窗口返回相同的最小化提示。这是实际观察，不是从页面标题推测。

## 尚未证实

原生 URL 提取失败的内部原因、是否有客户端连接状态或版本兼容问题。文件和 HTTP 都失败，截图显示地址栏未被遮挡。没有证据要求扩展安装、放宽网站权限或全局更改。

## 当前恢复尝试

历史步骤：重新枚举并取得窗口对象，分别执行窗口绑定和状态观察，省略先前组合调用中的 `activate_window`，观察到两个窗口均最小化。用户恢复后，Chrome 的 `get_window_state(include_screenshot: true, include_text: true)` 仍触发 URL 拦截，因此该恢复措施无效。用户授权以后由代理自行恢复最小化窗口；工具若阻止恢复，仍不能用其他输入渠道绕过。

未修改插件、工具安全检查、浏览器权限、用户日常 profile、Windows 策略或全局配置；未安装扩展。未尝试使用截图坐标盲点、调试端口或另一自动化渠道操作被拒绝的 Chrome 页面。

## 官方资料与可选后续

[官方浏览器连接排查](https://learn.chatgpt.com/docs/chrome-extension#troubleshooting)建议核对连接和对应 profile，必要时重启浏览器或桌面应用。该文档描述浏览器扩展连接，不直接解释这里的原生 URL 识别错误。本项目不自动安装扩展，也不自动关闭用户正在使用的会议/浏览器或重启 Codex。

[官方内置浏览器说明](https://learn.chatgpt.com/docs/browser#computer-use-in-the-browser)提供本地页面预览能力；即使内置浏览器可工作，也不能替代 Chrome 专用实例的音频验收。

地址栏和窗口恢复已核对，不再重复要求。当前可执行的恢复步骤及无敏感数据的支持说明见 27；不自动修改插件、权限、全局配置或上传诊断。Computer Use 本地 Computer Use 技能（26.924.22138，安装路径已脱敏）所引用 guidance 明确规定：`Do not automate the ChatGPT desktop app UI or Codex CLI or Codex extensions within Windows apps.` 因此 Codex 自身的退出、重启与设置核对由用户完成。

## M0 边界

本轮诊断不启动采集。T03–T08 继续 NOT_RUN；已有未审计录音目录不作为成功证据。任何后续 UI 可操作性恢复也不能自动升级音频验收结果。

## 已准备并验证的 HTTP 对照

新增 `scripts/Start-AudioProbeServer.ps1`，仅为开发诊断提供固定合成音 HTML：绑定 127.0.0.1 随机端口；拒绝任意其他文件、路径、POST 和非本机 Host；请求头限制 8KiB、读写超时 2 秒。页面使用内联脚本/样式 SHA-256 CSP，禁止网络连接与麦克风/摄像头；不自动打开浏览器、播放或采集。不是 M5 浏览器音频桥接，不加入产品运行依赖。

本轮实际命令：

```powershell
.\scripts\Start-AudioProbeServer.ps1 -DurationMinutes 30
```

本轮返回 `http://127.0.0.1:7733/audio_probe.html`。此地址临时有效，过期后重新执行脚本会生成新端口，不能把示例端口长期复用。

实际用 .NET HttpClient（仅该客户端禁用代理以访问 loopback，不改系统代理）完成 6 项检查：GET 精确页 200 且 3133 字节 SHA-256 与源文件一致；HEAD 200 无响应体；其他文件 404；附查询字符串路径 404；POST 405；非本机 Host 400，全部 PASS。源码 hash：`675cac139cafee86fa5d9d047d3ee10805efb3f8aa5145059c829857aca1c06d`。

证据：`.local/diagnostics/audio-probe-ready-b9040c4d039447bd9996c1a192a67f9b.json`、`.local/diagnostics/audio-probe-http-test-20260928.json`。PowerShell 语法检查 PASS；没有修改 C# 产品源码，未因此重跑产品 build。

用户已确认导航 HTTP 页；随后本工具的状态读取仍失败，因此对照没有修复问题。服务器已通过原执行会话停止，端口关闭；无 CaptureHost 进程。Chrome 音频 T07/T08 继续 NOT_RUN。
