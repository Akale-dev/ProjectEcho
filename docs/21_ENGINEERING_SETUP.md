# 21｜工程环境配置与规划修订记录

日期：2026-09-28。用户已明确接受规划复审建议，并授权配置所需工程环境。本次交付是修订规划和可用开发工具链；录音应用尚未实施，M0仍为NOT_STARTED。

## 已配置

| 项目 | 实际状态 |
|---|---|
| 系统 | Windows build 26200.9457，25H2，X64；PowerShell 7.6.5 |
| .NET SDK | 微软官方Windows x64 ZIP，10.0.401；官方SHA-512一致后解压到 `.tools/dotnet` |
| 随SDK的运行时 | .NET / ASP.NET Core / Windows Desktop 10.0.12 |
| 版本与构建默认值 | `global.json`固定SDK、禁预览与自动滚动；`Directory.Build.props`启用nullable、隐式using、确定性构建及NuGet锁文件 |
| NuGet | 项目 `NuGet.Config`仅配置官方nuget.org；实际恢复NAudio.Core/NAudio.Wasapi 3.1.0，缓存位于 `.local/nuget-packages` |
| 命令入口 | `scripts/Dotnet.ps1`，缓存/临时目录留在项目内，退出时恢复当前进程环境；不改系统PATH |
| Git | 复用2.53.0.windows.1，初始化本地main分支；未设远程、未改身份、未提交或推送 |
| 以后按需配置 | M2选择编码器，M3选择whisper.cpp与模型；本次均未安装 |

版本来源：[微软SDK下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)、[微软发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)、[NAudio.Wasapi官方包](https://www.nuget.org/packages/NAudio.Wasapi/3.1.0)。下载URL和完整SHA-512记录在 `tools/dependencies.json`；ZIP只保留在忽略的 `.local/downloads`，SDK不是端用户发行包的一部分。

## 实际验证

在 `.local/engineering-smoke/ToolchainCheck` 创建一次性的环境小程序，仅构造WPF控件和NAudio音频格式对象，不创建窗口、不枚举/打开音频设备。

- SDK `--info`：10.0.401，项目内安装路径。
- 从项目NuGet源恢复固定依赖：成功，生成小程序自己的 `packages.lock.json`。
- Windows构建：成功，0警告/0错误。
- 小程序运行：成功，WPF与NAudio基础类型可用。
- win-x64 self-contained、非单文件、不裁剪发布：成功，发布小程序在本开发机运行成功。
- 更新后的 `reference/AudioContracts.cs` 加入环境小程序再次编译：0警告/0错误，仅确认C#设计声明可编译。
- SDK命令成功及失败后均恢复原进程环境；Dev在产品工程不存在时明确失败，未伪报构建成功。
- Git正常读取本地仓库；SDK、缓存、录音与浏览器profile忽略规则生效。初次由沙箱账号创建的空Git元数据已保存在 `.local/initial-sandbox-git`，随后以日常用户初始化，不修改全局safe.directory。

这不证明WASAPI/NAudio采集接口已验证，也不证明完整录音应用可在干净电脑运行。T02与T46仍为NOT_RUN；真实M0应用的build/test和所有平台音频验收均未执行。现有 `Dev.ps1` 在缺少产品工程时应明确报错；其Publish分支仍为App模板，M2交付前要实现完整打包。

本地证据位于 `.local/diagnostics/`：`environment.json`、`dotnet-info.txt`、`toolchain-restore.txt`、`toolchain-build.txt`、`toolchain-run.txt`、`toolchain-publish.txt`、`toolchain-published-run.txt`。规划/数据契约静态校验另存 `planning-checks.json`，仅表示规格自身一致性。

## 使用方式

无需修改PATH，在项目根执行：

```powershell
.\scripts\Dotnet.ps1 -Arguments @('--info')
.\scripts\Inspect-Environment.ps1
.\scripts\Verify-Planning.ps1
```

M0建立正式解决方案后，使用 `scripts/Dev.ps1 -Action Restore|Build|Test|Run|Publish`。恢复会联网下载公开开发依赖，其余构建不隐式恢复。正式项目应将实际 `packages.lock.json` 纳入Git；当前 `.local` 小程序的锁文件只是本次环境证据。

## 已采纳的规划变化

默认未分类、日期命名、主动试录、单书签；M2只要求WAV+至少一种已验证压缩格式；完整时间轴及暂停静音；SQLite、剪辑/隐私区间编辑、术语管理及复杂调音延期。保留一个CaptureHost、原始分轨、恢复和两小时同步。补充停采生效边界与仅靠磁盘重建时间轴的契约。录音及暂停期间禁本程序回放。

M2前完成便携包和许可/无预装SDK/.NET验证，M1覆盖锁屏/睡眠；新增T51–T57覆盖末帧、隐私队列、离线重建、健康状态、防回录、快速入口、磁盘预算。24条需求、57条产品验收均未标为已完成。

`handoff-manifest.json`及`PACKAGE_CHECKS.md`保留初始交接包历史。修订文件与旧哈希不一致属于预期；不重写旧清单来伪装原包未变。当前规划校验使用Verify-Planning，初始包校验不能替代它，也不能替代产品测试。

下一步：执行修订后的 `prompts/01_START_M0.txt`，创建真正的来源选择和用户主动试录样机，再逐项验证API与音频能力。
