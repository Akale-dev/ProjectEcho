# 随包许可来源

- `NAudio-MIT.txt`：官方仓库 `https://raw.githubusercontent.com/naudio/NAudio/v3.1.0/LICENSE`（2026-09-29 经浏览工具读取，保留完整版权与许可文字；本机下载网络受沙箱限制，未改变代理/策略）。对应 NAudio.Core/Wasapi 3.1.0、commit `0aaef29d04bec9567bdf2f669036fabecc33a2e2`。
- `dotnet-runtime-*`：官方 NuGet `Microsoft.NETCore.App.Runtime.win-x64` 10.0.12 缓存包的 LICENSE.TXT、THIRD-PARTY-NOTICES.TXT。
- `dotnet-windowsdesktop-LICENSE.txt`：官方 NuGet `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.12 缓存包的 LICENSE。
- `System.Numerics.Tensors-*`：官方 NuGet `System.Numerics.Tensors` 9.0.0 缓存包的 LICENSE.TXT、THIRD-PARTY-NOTICES.TXT。
- `Microsoft.Data.Sqlite-MIT.txt`：官方 `dotnet/efcore` 的 `v10.0.12/LICENSE.txt`，2026-10-01 下载；对应 Microsoft.Data.Sqlite/Core 10.0.12（NuGet 记录源码合并仓库 commit `95017c711e6afc1085133d440e42b4bd78155701`）。来源：<https://raw.githubusercontent.com/dotnet/efcore/v10.0.12/LICENSE.txt>。
- `SQLitePCLRaw-Apache-2.0.txt`：官方 `ericsink/SQLitePCL.raw` 的 `v2.1.12/LICENSE.TXT`，2026-10-01 下载；对应 core、bundle_e_sqlite3、provider.e_sqlite3、lib.e_sqlite3 2.1.12。NuGet 版权声明为 `Copyright 2014-2024 SourceGear, LLC`，包未声明源码 commit，清单明确为空且 `sourceCommitAvailable=false`，不补造哈希。来源：<https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT>。
- `SQLite-Public-Domain.html`：2026-10-01 从 SQLite 官方保存的原始版权声明页面，说明原生 SQLite 引擎为公有领域；包装与互操作层仍遵守上述 Apache-2.0。来源：<https://www.sqlite.org/copyright.html>。

发布时 `THIRD_PARTY.json` 写每包准确来源、版本、源码 commit、包 SHA-256/SHA-512；`release-manifest.json` 写各许可文件与二进制 SHA-256。所有原 NuGet 缓存包均来自工程环境授权下恢复的项目本地缓存。Microsoft Media Foundation 属目标 Windows 系统组件，没有把系统 codec DLL 放入分发包。
