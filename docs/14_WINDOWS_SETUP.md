# 14｜Windows / ChatGPT Desktop 从零启动

## 1. 解压并打开
建议解压到 `D:\AI\ProjectEcho\`（盘符自选）。打开包含 `AGENTS.md` 的那一级，不要把ZIP作为普通聊天附件后期待代理能自动修改你的电脑项目。
双击 `START_HERE.html` 阅读。你已有ChatGPT Desktop/Codex时直接使用，不重复安装CLI/Node/Python。官方桌面文档提供“打开文件夹/选择工作位置”与Codex入口；具体按钮随版本可能变化，以能读取/修改授权目录为准。[S15]

## 2. 在Codex里打开本地项目
选择本地文件夹、进入Codex任务；只授权该项目所需权限。把 `prompts/01_START_M0.txt` 内容复制进对话。第一条输出应报告实际工作目录、AGENTS读取和环境检查，而不是直接自称软件完成。[S16]
不要给全盘权限来解决音频问题；如果普通聊天不能执行文件/命令，应切到该桌面应用的Codex项目工作入口。

## 3. 开发依赖与运行依赖不同
开发需要.NET 10 SDK。2026-09-28已按用户“配置工程环境”的授权，将微软官方 Windows x64 SDK **10.0.401** 校验SHA-512后解压到本项目 `.tools\dotnet\`，并以 `global.json` 固定版本。已有Git继续复用，无须安装整套Visual Studio。[S08,S09]
使用 `scripts/Dotnet.ps1` 调用本地SDK；它只为当前命令设置项目内缓存/临时目录，退出后恢复环境，不修改系统PATH。运行版用self-contained文件夹分发，不需要用户装SDK/.NET；M2必须实际验证完整便携包。[S09,S17]
FFmpeg/whisper模型先不装也能开始M0；M2才处理编码器，M3才处理模型。每个二进制核对来源/hash/许可。

## 4. 可选预检
在PowerShell进入根目录后：
```powershell
.\scripts\Inspect-Environment.ps1
```
脚本只写 `.local\diagnostics`；不录音、不改变系统设置。如果组织策略阻止脚本，不修改ExecutionPolicy/注册表绕过；让Codex在获准工具中执行对应只读检查，或遵循IT允许流程。

## 5. 已配置环境与开发入口
```powershell
.\scripts\Dotnet.ps1 -Arguments @('--info')
```
本机已验证项目内小程序的WPF/NAudio构建、运行和self-contained发布；小程序位于忽略的 `.local/engineering-smoke`，不访问音频设备，不是录音应用。版本、来源、验证边界见 `21_ENGINEERING_SETUP.md`。

M0创建真实工程后：
`Restore`读取项目 `NuGet.Config`，仅配置官方nuget.org，依赖缓存留在 `.local`；本次工程环境配置已授权相关下载。以后在已授权开发范围内按需恢复，录音运行时不下载。后续构建不隐式下载。
```powershell
.\scripts\Dev.ps1 -Action Restore
.\scripts\Dev.ps1 -Action Build
.\scripts\Dev.ps1 -Action Test
.\scripts\Dev.ps1 -Action Run
.\scripts\Dev.ps1 -Action Publish
```
如果没有工程文件，会明确提示“还没有实现项目”，不应出现伪成功。脚本无需全局PATH修改，可指定 `-DotnetPath 'D:\Tools\dotnet\dotnet.exe'`。M2 的 Publish 已改为 `Publish-Portable.ps1`：在 artifacts 下新建完整 App + CaptureHost 自包含文件夹，包含许可、来源与文件哈希，不覆盖既有包。编码使用 Windows 自带 Media Foundation AAC。干净 Windows 验收仍必须实际执行，不能只凭该命令判定通过；详见 `38_PORTABLE_GUIDE.md`。

## 6. 网页会议试用（不是现在自动执行）
`scripts/Open-MeetingBrowser.ps1`是用户主动使用的便利启动器，需要指定数据根；单独user-data-dir仍需Codex验证实际音频树，启动PID不是隔离认证。
```powershell
.\scripts\Open-MeetingBrowser.ps1 -Browser Chrome -DataRoot 'D:\MeetingRecords\ProjectEcho' -MeetingUrl 'https://你的会议地址'
```
请不要把真实链接贴回开发日志；启动器不保存链接。登录/入会由你手动完成，打开后录音仍需要你按开始。

## 7. 第一轮你需要验证的事
能运行界面；两路声音各自正确；只录选中的来源；停止后可回放；普通Chrome不混入专用会议浏览器；平台静音与本程序mic开关区别清楚。桌面客户端/网站没有在本机安装或可用时如实注明。
