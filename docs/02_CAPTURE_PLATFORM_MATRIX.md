# 02｜平台与捕获能力矩阵

下表是设计支持目标，**全部尚待目标 Windows 环境实测**，不是认证列表。

| 场景 | 首选后端 | 录制边界 | 必测重点 |
|---|---|---|---|
| Zoom 桌面 | ProcessTreeLoopback | 已选进程及子进程的输出音频 | 音频进程定位、子进程重建、共享媒体 |
| Webex 桌面 | ProcessTreeLoopback | 已选进程及子进程的输出音频 | 实际客户端/会议子程序和音频会话；不写死一个 EXE |
| Zoom 网页 / Chrome | DedicatedBrowserProcess | 专用 user-data-dir 的浏览器进程树 | 普通 Chrome 声音不得进入；重新入会重新校验 |
| Webex 网页 / Chrome | DedicatedBrowserProcess | 同上 | 页面实际使用电脑音频、媒体服务源变化 |
| 上述网页 / Edge | DedicatedBrowserProcess | 同上，独立测试 | 企业策略、进程复用、音频会话 |
| 已有混用浏览器 | ExistingBrowserProcess | 整个选定浏览器进程树 | 明示可能录入其他标签页，不称为 tab isolation |
| 能力不支持或失败 | EndpointLoopback（用户确认） | 指定输出设备上所有应用的声音 | 同设备提示音/其他媒体、端点切换 |
| 后续扩展 | BrowserTabCapture | 用户明确选定的标签页输出 | 播放回接、断连、页面切换、浏览器策略 |

## Windows 能力而不是版本名称
微软 sample 指明 process-tree loopback 需要 build 20348+，API 激活还须实际成功；仅 build 大不代表客户端/权限/设备都能正常工作。[S01]
常见旧 Windows 10 环境可能没有这项能力。不要简单给整个 Windows 10 标签打勾，也不要擅自升级用户系统。端点 loopback 是明确同意后的兼容路径，其范围更宽。[S02]
.NET 支持的 OS/CPU 与音频 API 能力分开验证；Windows x64 是首要发行目标，ARM64 不在本版承诺里。[S08,S09]

## 音频来源与发现
枚举活跃 render endpoints 的音频 sessions → 会话 PID → 可访问的进程元数据/树 → 提供候选 → 用户做短试录。Windows 音频会话和进程树可能存在多对多关系，不能用窗口标题当音频源唯一证据。
平台名只做提示。配置可保存选择偏好，不保存长期 PID；绑定以 PID + process start time + exe identity 表示。音源退出后停止该来源/显示缺口，请用户重新确认新来源，不把复用 PID 当同一进程。
如确需多个互不重叠根树采集，要先证明没有父子重叠/重复信号；首版优先一个已校验根树。不能捕获 explorer.exe 等过宽根树来“包住 Webex”。

## 明确不支持
只在手机/电话/会议室设备播放的音频；任意不经 Windows 可捕获渲染路径的音频；受平台/组织限制而不能合规捕获的情况；用录音工具自动获取参会者实名/每人独立轨。

## 不是屏幕共享
不需要分享屏幕或录视频；记录当前被明确选择的音频。用户点击会议链接后跳转桌面客户端，必须更新来源，不能继续录浏览器静音。
