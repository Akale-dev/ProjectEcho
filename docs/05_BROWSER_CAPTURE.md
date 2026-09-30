# 05｜网页 Zoom / Webex

## 首版默认：独立会议浏览器实例
Chrome 用专门的 `--user-data-dir`，只在该实例中开会议；平时 Chrome 用原数据目录。启动器只读取已安装的浏览器路径，不安装/下载浏览器，不复制默认 profile/cookies，不禁用安全策略。用户自行在新 profile 登录。Chromium 官方支持该参数；audio service 可在独立 utility process 运行，所以捕获需要验证实际进程树而不只抓 tab renderer。[S04,S05]

单纯新窗口、无痕窗口、换 `--profile-directory` 不算本方案的隔离证明。`Process.Start()` 返回 PID 也不是永远可靠的浏览器根：既有实例可能复用。验证 user-data-dir、进程启动时间、实际音频会话和 probe；命令行含秘密 URL，不能写日志或诊断包。

网页会议导航/加入可能转入桌面 app；此时要重新选音源。别静默录空浏览器。一个专用实例内再开有声标签页，会一起被录；界面写明“该浏览器实例全部声音”。本方案不屏蔽其他页，而是清楚说明范围。

## 对照验收
普通 Chrome A 播440Hz，专用会议 Chrome B 播660Hz；只录B应有660无440。再在B开880Hz页，应明确承认会录到880；禁止把这个模式称为单页。测试两个 Chrome 先后启动次序、重启、最小化、麦克风权限、耳机切换、实际 Zoom/Webex 网站。

## 公司管理限制
组织策略可能限制独立 profile、扩展或音频访问。不修改注册表策略、不带禁用 sandbox 参数、不绕开限制。不支持时清楚报告；可选全端点兼容必须用户同意，不能偷偷退化。

## 可选后续：精确 tabCapture（M5）
经用户手动安装/许可的 Manifest V3 扩展，最小 `tabCapture` / offscreen 权限，用户点击扩展选择当前会议标签页。不扫浏览历史、不全站内容注入，不用 DevTools 强行抓媒体。
Chrome 官方说明 capture 后标签页原音频会停止直接输出；扩展必须将捕获流接回 AudioContext destination 保证用户继续听会，并测试输出路由/重复播放。API 必须由用户操作触发；不是后台全自动监听。[S03]

本机桥接是独立的后续工程。可选 localhost websocket 只绑定 loopback、临时配对码/短时token、严格 Origin 白名单、协议/大小检查与单连接绑定；不开放 CORS *，不把 bearer 写URL/日志，不监听0.0.0.0。可能存在浏览器权限/企业策略限制，先 spike，不承诺免配置总能用。原生 messaging 可能涉及注册，不能变成基础版依赖。

音频通过 AudioWorklet 输出 sequence/帧数/浏览器 clock，桌面桥接校准到会话时钟；不能直接把 JS timestamp 当 QPC。PCM 背压/断连显式记缺口。MediaRecorder 分片不是每片都可独立解码；如采用它，必须定义 init/header与恢复协议，不直接当标准 WAV 块。

M5 不允许同时再捕获同一浏览器进程/同输出端点，避免双录。麦克风仍由 Windows 引擎独立采集；不在浏览器再录一次同一 mic。
