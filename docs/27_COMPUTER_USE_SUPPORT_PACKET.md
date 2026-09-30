# Windows Computer Use 故障交接与支持材料

2026-09-28。本文件仅保存到项目，未发送给任何外部服务。

最新补充：电脑重启后的 Chrome 复测仍失败，WPF 操作正常。后续 Playwright 全标签页枚举经用户明确授权及正常审批成功，但只有 about:blank，访问项目合成页被 file: 协议限制拒绝。没有跳过审批或继续换渠道打开被拒绝页面。旧试录的离线审计及源码修复见 [28](28_M0_OFFLINE_CAPTURE_REPAIR.md)；此处前序的“待复测”已由上述结果取代。

## 结论

Chrome 窗口枚举和绑定成功，读取窗口状态时工具无法可靠取得当前 URL，并以策略保护结束当轮。恢复窗口、保持地址栏可见、将合成页从文件地址改为本机 HTTP 后均未恢复。当前没有证据支持改 ProjectEcho 音频代码、安装扩展、开放调试端口或调整安全策略。

## 本机证据

| 项目 | 已观察值及限制 |
|---|---|
| Windows | build 26200.9457 / X64，环境审计见 22 |
| Chrome | 153.0.8010.53，之前的版本审计 |
| Codex 桌面包 | 日志路径标识 `26.924.2738.0`，不代表当前渠道最新版本 |
| app-server | 启动日志 `0.158.0-alpha.2.1` |
| Computer Use 插件 | manifest `26.924.22138` |
| 本地启用配置 | Computer Use、unified-computer-use、browser 均 enabled=true |
| 本地规则覆盖 | config.toml 未定义 computer_use/browser_use；未据此宣称组织策略或桌面许可允许 |
| 重现 | 实际窗口绑定后 `sky.get_window_state({window, include_screenshot:true, include_text:true})` |
| 返回 | `could not determine the current browser URL on Windows with enough confidence to enforce policy`，工具要求终止当轮 |
| 文件对照 | 正常独立 Chrome profile；项目合成 HTML，无自动播放或导航；截图显示地址栏可见 |
| HTTP 对照 | 同一合成 HTML；本地服务器 GET/HEAD/hash 与拒绝路径检查 6 PASS；Chrome 状态读取仍失败 |
| 当前采集 | 未启动；0 个 CaptureHost 进程，mic 未启用 |

未发现公开官方文档中该精确错误的对应修复版本。不得从组件版本号差异推断不兼容。

## 独立日志线索，勿误判因果

桌面日志在当前聊天的 2026-09-28T12:25:21Z 出现 8 条：

已脱敏概述：内置浏览器的信息查询未找到当前聊天可用的浏览器路由。原始日志未随源码提交。

这是内置浏览器信息查询的错误；不能把它当作原生 Chrome URL 检查的已证实根因。早先空日志现在已有内容；没有修改日志设置。

`.codex/logs_2.sqlite` 仅以 `mode=ro&immutable=1` 和 `query_only` 查询当前聊天的主数据库记录，未读取 WAL、未复制数据库、未写原目录。命中的记录没有提供可确认的 URL 解析内部失败码。该检查不能证明日志完备。

完整但脱敏的本地事实清单：`.local/diagnostics/computer-use-support-20260928.json`。不含聊天内容、会议地址、浏览器 profile/cookies、录音或原始日志全文。

## 下一步恢复

2026-09-28 补充：用户先重启 Codex，原生 Chrome 状态读取仍返回相同 URL 保护错误；随后用户已重启电脑。21:30 本轮重新启动现有 WPF 样机，窗口状态/界面读取及打开专用 Chrome 按钮均成功，看到 2 个输出端点、0 个 mic、未选来源及未采集状态。专用 Chrome 使用本轮新目录启动，浏览器状态复测待执行。WPF 操作成功不能代表浏览器故障已恢复。

1. 重启步骤已执行，不再重复要求。继续本轮一次正常的 Chrome 状态读取，记录是否仍为相同错误。
2. 重启后使用同一授权范围、正常安全检查验证一次。如果仍是相同错误，停止重复触发，保留错误及组件版本交给工具方定位。
3. 用户可只读核对 Settings → Computer Use 中插件/现有 Chrome 连接的状态与是否有待处理请求；本项目不自动安装扩展、开启 CDP 或放宽权限。
4. 如选择 `/feedback`，先检查将分享的数据。当前没有发送授权，也没有自动提交。本文件的版本/错误/复现部分可由用户自行选择附加，勿附浏览器资料或音频。

重启并非已验证修复。[官方连接排查](https://learn.chatgpt.com/docs/chrome-extension#troubleshooting)列出了桌面应用重启与反馈路径，但针对的是浏览器连接，不能当作本错误的确定解法。[Windows Computer Use 说明](https://learn.chatgpt.com/docs/computer-use)要求活动桌面可见。原生、扩展和内置浏览器的权限路径不同，见[本地安全说明](https://learn.chatgpt.com/docs/enterprise/chatgpt-work-local-security)。

Computer Use 本地技能（26.924.22138，安装路径已脱敏）引用的 guidance 禁止自动操作 ChatGPT/Codex 自身 UI：`Do not automate the ChatGPT desktop app UI or Codex CLI or Codex extensions within Windows apps.` 这一步由用户完成；不会用 shell 强制终止 Codex、修改运行时或清除策略状态替代。

## 本轮收尾

只读诊断和 Markdown/JSON 更新已完成；临时 HTTP 服务已通过原执行会话停止，7733 端口关闭。未改任何其他项目、浏览器 profile、插件或安全配置，未采集、上传、提交或 push。M0 音频闸门仍未完成，T03–T08 继续 NOT_RUN。
