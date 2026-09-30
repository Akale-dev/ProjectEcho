# 无 host 管理员权限的干净 Windows 测试可行性

2026-09-29，只读研究。没有下载 QEMU/Windows 二进制或 ISO，没有安装/启用系统功能，没有启动虚拟机，没有修改安全策略、TPM、OOBE 或注册表。T46 仍未执行。

## 结论

**技术上有“项目内 QEMU TCG + 官方 Windows Server 2025 Desktop Experience”候选，但不能承诺它能完成正式 T46，也不能把它当作已经可用的干净 Windows。** TCG 可在 Windows 上纯软件模拟 CPU，因此不以启用 Hyper-V/WHPX 为前提；实际便携解包、客机安装、音频端点、进程树 loopback、媒体 codec 与运行性能都尚未验证。[QEMU 系统说明](https://www.qemu.org/docs/master/system/introduction.html)

还存在官方支持边界：.NET 10 支持的 OS 表包含 Server 2025 x64，却同时备注 QEMU emulator 不受支持运行 .NET 应用。备注举例是跨架构模拟，不能扩展成“任何采用 QEMU 的硬件虚拟化都不支持”；这里提出的 **TCG CPU 模拟** 至少不能被无条件称为受支持目标。因此即使后续成功，也应作为注明虚拟环境的补充兼容性证据，不能据此替换 Windows 11 真机音频/设备/系统状态验收。[.NET 10 官方支持表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

## 本机 WHPX 实际只读结果

随后在正常非管理员环境实际查询得到：

- `Win32_ComputerSystem.HypervisorPresent = false`。
- `VirtualMachinePlatform`、`HypervisorPlatform`、`Microsoft-Hyper-V-All` 的 `Win32_OptionalFeature.InstallState` 均为 `2`（Disabled）。
- 官方 `WHvGetCapability(WHvCapabilityCodeHypervisorPresent)` 返回 `S_OK`、写入 4 字节、值为 0；可读 API 明确返回 HypervisorPresent=false。
- `WinHvPlatform.dll` 存在，`WHvCapabilityCodeFeatures` 可返回 API 特性值 767，但这不代表 hypervisor 已运行，不能据 DLL 或特性位声称 WHPX 可用。

因此本机**没有现成可用的 WHPX 加速路径**。没有启用任何 feature、创建 partition 或 VM。保留现有验收边界，不用明确有 .NET 官方支持限制的 TCG 模拟来把 T46 写成通过。当前需要另一台已获准的干净 Windows 目标，或后续由用户/IT提供已启用的受支持虚拟化环境。

脚本：`scripts/Inspect-Virtualization.ps1`；证据：`.local/diagnostics/m2-20260929/whpx-capability-normal-user.json`。沙箱首次 CIM 查询拒绝访问，另存 `whpx-capability-sandbox.json`，不把该拒绝当作 feature 不存在。所有查询均只读；[WHvGetCapability 官方说明](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvgetcapability)说明 code 0 用于判定 hypervisor 是否运行、平台 API 是否能创建 VM；[Win32_OptionalFeature](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-optionalfeature)说明状态值。

## 来源、版本与权限

| 项目 | 已核实 | 尚未核实 |
|---|---|---|
| QEMU 上游 | 下载页最新源码为 11.1.2，2026-09-28；GPL v2，源码有发布签名 | 本机编译没有执行，也不为此安装编译链 |
| Windows 二进制 | QEMU 官网链接到 Stefan Weil；页面当前最新是 11.1.0，`qemu-w64-setup-20260811.exe`，约 197 MB，附 SHA-512 | 没有下载/验签/运行；不能把源码最新版等同 Windows 包版本 |
| 安装器 | 发布者源代码声明 `RequestExecutionLevel admin`，写 HKLM | 不直接运行此安装器，不改其 manifest/提权要求 |
| 项目内便携候选 | 本机已有 `C:\Program Files\7-Zip\7z.exe` 19.00；可研究只提取程序、DLL、固件和许可 | 解包格式、依赖是否完整、`qemu-system-x86_64 --version`、TCG 启动尚未执行 |
| 资源 | H 盘本次只读观察剩余约 878.71 GiB | 不能由磁盘空间推定内存/CPU性能或安装耗时；运行 TCG 会占 CPU，不宜与当前真实 mic/长录测试同时进行 |

来源：[QEMU 下载页](https://www.qemu.org/download/)、[Windows 发布者目录与版本记录](https://qemu.weilnetz.de/w64/)、[发布者安装脚本](https://raw.githubusercontent.com/stweil/qemu/ar7/qemu.nsi)、[GPL 许可](https://raw.githubusercontent.com/qemu/qemu/master/COPYING)。Windows 发布者页面还声明构建是实验性质且新安装器签名证书过期；没有建议关闭 SmartScreen/证书检查或绕过组织限制。

发布者当前提供的安装器 SHA-512 为：

```
5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543
```

这只是读取了[发布者校验文件](https://qemu.weilnetz.de/w64/qemu-w64-setup-20260811.sha512)，尚未下载文件逐字节核对。QEMU 只会作为开发验收工具，不放进 ProjectEcho 便携包；第三方 DLL/固件许可需随测试工具完整保留。

## Windows 镜像、TPM 与版本区别

微软官方提供 Server 2025 Standard/Datacenter 的 64-bit ISO，并可选择 Desktop Experience。评估期 180 天，页面要求注册后下载安装，且安装后 10 天内联网激活以避免自动关机。没有代用户填写个人信息或接受许可；未下载/核验具体 ISO，所以当前没有 ISO 文件大小或官方摘要可报告。[评估说明](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-server-2025)、[官方下载入口](https://www.microsoft.com/en-us/evalcenter/download-windows-server-2025)

Server 2025 文档列出 64-bit/SSE4.2/POPCNT 等 CPU要求；Desktop Experience 推荐 4 GB RAM，32 GB 磁盘只是绝对下限。TPM 2.0/Secure Boot 对 secured-core 及某些功能有要求，不能据此声称任何客机配置都合规。须实际核对 TCG 暴露的 CPU/固件能力，遇到要求不符停止，不伪造 CPU/TPM 结果。[Server 硬件要求](https://learn.microsoft.com/en-us/windows-server/get-started/hardware-requirements)

Windows 11 的 TPM 2.0、UEFI/Secure Boot 能力和正常初始设置要求不能删除。QEMU 的 TPM 模拟还依赖外部 `swtpm`；目前没有在本机核实可信、完整、可无管理员运行的 Windows swtpm/UEFI 链。因此 Server 候选不能冒充 Windows 11 安装，也不能以跳过 Win11 检查来关 T46。[Windows 11 要求](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements)、[QEMU TPM 文档](https://www.qemu.org/docs/master/specs/tpm.html)

## 音频能证明什么

QEMU 有 HDA 模拟及明确的 `hda-output` 输出专用 codec；若实施，先选择纯合成客机声音，明确用输出专用设备，避免默认 duplex 自动碰 host mic。`-audiodev none` 会丢弃输出，可用于不发声的部分客机音频处理检查，但不能算真实回放听验。对应的 Windows guest 驱动、Windows Audio 状态、process-loopback 与时间戳仍须现场验证。[QEMU HDA 源码](https://github.com/qemu/qemu/blob/master/hw/audio/hda-codec.c)、[音频 backend 文档](https://www.qemu.org/docs/master/system/invocation.html)

Server 2025 的功能清单列出 Media Foundation；“功能可用”不等于本次 ISO 安装后一定已启用、编码器已工作。须在客机静态枚举/实际合成编码，缺失时保持明确失败；不要为得到 PASS 把客机装上 SDK/现代 .NET 或改变产品依赖条件。[微软功能清单](https://learn.microsoft.com/en-us/windows-server/get-started/editions-comparison?pivots=windows-server-2025)

纯软件虚机的调度与虚拟音频设备不能替代真实 Realtek mic、拔插、睡眠/锁屏、实际 Zoom/Webex/Chrome 设备范围和两小时声学同步。若后续只完成 App 启动与文件编码，记录只能写这两个项目通过；完整录音/回放/导出仍按实测逐项判断。

## 最小后续步骤

1. 当前无需为研究下载 GB 镜像。若继续候选实验，先核对 QEMU 包摘要、许可和组织执行许可，再仅在项目测试目录解包，运行普通用户 `--version`/设备列表/空机启动；不得运行需要管理员的安装器或降低防护。
2. 第一关确实通过后，再用官方评估入口按正常许可/安装流程取得镜像，记录实际版本/摘要，创建项目内虚拟磁盘；遵循正常 Windows 设置流程。
3. 保持 guest 无 SDK/现代 .NET/模型，普通用户运行完整便携包；先验证启动和本地文件编码，再检查真实 guest 音频端点和用户主动合成录音/回放。不得把配置计划或 Linux-only 文档例子写成 Windows PASS。
4. 该环境若受安装、组织策略、官方支持或音频能力限制，应保持 T46 未验收并转用已获准的干净 Windows 11 真机/硬件虚拟化目标。

本地只读工具/空间记录：`.local/diagnostics/m2-20260929/clean-vm-research-local.json`。这不是虚拟机执行证据。
