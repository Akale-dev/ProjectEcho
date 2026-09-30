# 22｜M0 环境审计与专用浏览器入口

日期：2026-09-28。本报告是本次实机重新检测，不把上轮环境小程序当作产品验收。只在授权项目写入临时探针和诊断；没有开启浏览器、入会、采集音频、启用麦克风或安装工具。

## 实际环境

| 项目 | 本次观测 |
|---|---|
| 当前工作目录 | `H:\AI\ProjectEcho` |
| Windows | build **26200.9457**，DisplayVersion **25H2**，OS/进程架构均 **X64** |
| 系统名称原始字段 | 注册表 `ProductName` 返回 `Windows 10 Pro`；该字段与新系统品牌可能不同，能力判断使用实际 build，未假定 Windows 11 |
| PowerShell / 权限 | **7.6.5**；当前 Windows 令牌不是提升的管理员令牌 |
| 项目 SDK | `.tools/dotnet/dotnet.exe` 实际选择 **10.0.401**；MSBuild **18.9.11+e34a38d2a** |
| SDK 锁定 | `global.json` 为 **10.0.401**，`rollForward=disable`，`allowPrerelease=false`；与实际选择完全一致 |
| 项目运行时 | .NET、ASP.NET Core、Windows Desktop **10.0.12**；RID `win-x64` |
| 已有系统 dotnet | PATH 找到 `C:\Program Files (x86)\dotnet\dotnet.exe`；`--list-sdks` 空，`--list-runtimes` 有 x86 .NET/Windows Desktop **5.0.17 / 6.0.36**。标准 x64/x86 SDK 目录均不存在；未搜索用户其他项目 |
| Git | 复用 `C:\Program Files\Git\cmd\git.exe`，**2.53.0.windows.1**；未改 Git 身份/全局配置、未 push |
| 可写验证 | 项目根、`src`、`tests`、`docs`、`.local`、`.local/diagnostics` 均实际创建并删除单个临时探针成功 |
| 所在卷 | `H:`，NTFS；审计时可用 **946,852,683,776 bytes**；仅作瞬时环境信息，不代表音频磁盘预算验收 |
| 已有浏览器 | 标准 `Program Files (x86)` 安装位置发现 Chrome 和 Edge；只检查文件存在，没有启动。浏览器版本/组织策略/实际隔离仍待测 |
| 进程回环 build 条件 | build 大于等于 20348；只满足 build 前置条件，不代表激活或采集成功 |

当前执行策略读取为 `RemoteSigned`（LocalMachine，其余 scope 未定义）；没有变更策略。`Get-Volume -DriveLetter H` 被拒绝访问，改用普通权限 `System.IO.DriveInfo` 读取卷格式/可用字节成功；没有提权或修改权限。沙箱只授权项目内写入，本次未探测其他项目可写性，也未调整代理、PATH、注册表策略或浏览器常规 profile。

## 实际命令与本地证据

所有 SDK 调用经过现有包装脚本，使用项目内临时文件和缓存，并恢复当前进程环境：

```powershell
.\scripts\Dotnet.ps1 -Arguments @('--info')
.\scripts\Dotnet.ps1 -Arguments @('--version')
.\scripts\Dotnet.ps1 -Arguments @('--list-sdks')
.\scripts\Dotnet.ps1 -DotnetPath 'C:\Program Files (x86)\dotnet\dotnet.exe' -Arguments @('--list-sdks')
.\scripts\Dotnet.ps1 -DotnetPath 'C:\Program Files (x86)\dotnet\dotnet.exe' -Arguments @('--list-runtimes')
git --version
```

另外使用 PowerShell/.NET 读取当前目录、注册表 OS build、RuntimeInformation 架构、WindowsPrincipal 管理员角色、Get-ExecutionPolicy、标准安装位置文件是否存在、DriveInfo；写入探针仅写项目中生成的 GUID 文件并立即删除。没有枚举用户名、浏览器命令行、会议链接或 profile 内容。

证据保存在忽略的 `.local/diagnostics/`：

- `m0-environment.json`：OS、权限、Git、锁定值、可写探针、浏览器位置存在性。
- `m0-environment-additional.json`：已有系统 dotnet 路径、SDK 目录、执行策略和卷空间。
- `m0-environment-dotnet-info.txt`、`m0-environment-sdk-version.txt`、`m0-environment-sdk-list.txt`：项目 SDK 原始输出。
- `m0-environment-system-sdk-list.txt`（空输出）、`m0-environment-system-runtime-list.txt`：已有 x86 dotnet 的 SDK/运行时清单。

## 专用 Chrome 入口

`DedicatedBrowserLauncher.LaunchChrome(projectRoot)` 仅供明确的按钮点击调用：寻找已有 Chrome，可用时打开仓库的 `tests/audio_probe.html`，否则打开 `about:blank`。每次调用建立 `.local/BrowserProfiles/Meetings/Chrome/<UTC时间-GUID>/`，使用独立 `--user-data-dir`；不读取或复制常规 profile，不接受/记录会议 URL，不自动播放、入会、启用 mic 或录音，不增加远程调试/禁用沙箱/证书绕过等参数。目录链接会被拒绝，避免把资料写入重定向到项目之外的目录。

返回 `BrowserLaunchResult` 的启动 PID、可读时的启动 UTC、资料目录和提示仅存在于调用结果。它们不是已验证音源，不能保存后复用旧 PID。后端仍须刷新实际音频会话、校验进程启动身份并让用户选择合适祖先树。每次新资料目录避免重用已有专用实例，但仍不能仅靠启动结果证明隔离；用户可能需要在新目录重新登录。所有资料目录都在 `.local` 忽略范围中，不应进入导出。

明确显示范围是 **该浏览器实例全部标签页的声音**。它不是单标签页录制。合成页面只在用户点击“播放10秒”后产生低音量测试音，且没有麦克风/录音/网络逻辑。

## 验证边界

环境审计完成，专用启动入口已实现；其编译结果以本轮主 M0 报告为准。**浏览器启动、440/660/880 Hz 隔离、Chrome 网页 Zoom/Webex、Edge、Zoom/Webex 桌面、WASAPI 激活、mic 权限和实际音频采集均为 NOT_RUN**。本次没有用户主动触发的音频采集，因此不宣称 M0 产品退出闸门通过。下一步由用户运行样机并主动启动合成音与短试录。
