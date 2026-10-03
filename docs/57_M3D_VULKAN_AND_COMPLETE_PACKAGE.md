# 57｜M3D Vulkan 部署与完整离线包

2026-10-01晚至2026-10-02，按用户追加授权执行。M3D原10项与教学按钮先完成，随后配置真实Vulkan并准备新电脑使用的ZIP；用户选择随包 **base多语言模型**，要求发现问题修正后更新ZIP，全部完成后正常关机。没有录音、开启mic、打开会议或上传资料。

## 引擎选择与便携依赖

本机只读识别AMD Radeon **RX 6900 XT**，驱动 **32.0.21043.10005**，系统Vulkan loader **1.4.309.0**。本轮没有改驱动、注册表策略或系统PATH。

最初验证了Lemonade SDK维护的v1.8.4 Vulkan ZIP，资产SHA与GitHub公布值一致，实际base/large及取消重试通过。这是第三方维护构建，不是ggml-org官方预编译包。其PE依赖含MSVCP140/VCRUNTIME/VCOMP，不能因本机已有这些库就声称新电脑解压可用。该候选证据保留在`.local/M3D-Vulkan/archive-provenance.json`、`real-01/report.json`、`gpu-full-01/report.json`；**最终包不采用这个候选二进制**。

最终从ggml-org官方 **b5130 / 1.9.4-dev** 源码自构建两个独立Windows x64引擎。源码commit `927cfce34f31707e17f2bff35c349632fb9e2c3a`，下载源ZIP SHA-256 `b268c24559fa6256befe3341e0e1a6a4106bdb636aef84b37df872b081f39c21`。使用项目内w64devkit2.9.1（GCC16.2/CMake4.4.2/Ninja1.13.2），Vulkan编译器与头文件使用逐包核验SHA的MSYS2小包，仅解包，不安装MSYS2或执行安装器。来源、许可与工具验证见`toolchain/downloads-provenance.json`、`toolchain-ready.json`和随包`THIRD_PARTY.asr.json`。

构建脚本`Build-WhisperPortable.ps1`使用静态compiler runtime，关闭OpenMP、curl、SDL2、native CPU调优和AVX/AVX2/FMA/F16C/SSE4.2/BMI2。CPU兼容优先，不把当前机器指令集写死为所有电脑前提。源代码未做算法修改。CPU EXE静态导入仅ADVAPI32/KERNEL32/msvcrt；Vulkan EXE只额外依赖系统vulkan-1.dll，**不要求预装Visual C++、GCC、SDK、Python或Node**。显卡驱动仍由目标Windows提供，不能把驱动装进便携ZIP。

PE检查见`pe-imports-cpu-final.json`、`pe-imports-vulkan-final.json`。原CPU安装/模型/配置不覆盖；新Vulkan目录及单独配置安装在用户指定`H:\AI\Whisper`范围，实际路径与结果写于交付记录。

## 实测与限制

平台Windows11 25H2 26200.9457/x64、.NET SDK10.0.401/runtime10.0.12、NAudio3.1.0。M3D新增便携配置后189默认回归通过；七项开发WPF流程最终通过。测试只使用已有官方11秒JFK公开音频及合成文件，播放器测试静音，不读取私人会议用于识别。

最终静态Vulkan引擎真实生产链路：base成功3359ms、large-v3成功8752ms、large实际进入识别后取消3698ms并确认独立进程退出、取消后base重试成功4075ms。明确核对实际 **Vulkan0 / RX6900XT** 后端，prompt、原件/模型hash、三份独立原稿及旧子修订均验证；`.local/M3D-Vulkan/gpu-static-final/report.json`。这些用时仅为固定样本，部分验证并行执行，**不是严谨CPU/GPU速度benchmark**。

最终静态CPU兼容引擎真实base第一次7597ms、观察到真实子进程后取消142ms、重试7002ms均通过，字幕导出往返及原hash保持；`.local/M3D-Vulkan/cpu-static-final/report.json`。CPU和Vulkan路径分别验，不以GPU机器上CPU能启动替代无GPU测试。

构建首轮Vulkan失败来自PowerShell数组拼接使四个CMake参数被合成一项，已拆为独立参数后构建成功；保留`build-vulkan-01.log`。初版CPU发现SSE4.2/BMI2仍默认开启，已显式关闭并重新构建和实测，旧输出不作最终包。C++上游仍有格式符和编译器vector优化警告、CMake最低版本弃用提示；**不把引擎构建称作零警告**。项目C# Release构建0错误0警告。所有最终二进制SHA及flags单独留档。

**中文路径实际失败及修复：**首个完整ZIP在含中文的新目录解压后，真实WPF识别报错码3；stderr把模型路径中的中文变成`??`，证明MinGW CLI窄argv仍按系统ANSI处理。没有把测试目录改成英文来规避。新增仅作用于CLI进程的Windows manifest，设置UTF-8 activeCodePage、longPathAware、asInvoker；不修改系统语言/区域或注册表。CPU/Vulkan均重链，并重新核对依赖、中文模型/音频/输出路径与中文prompt，然后重做ZIP迁移实测。跟踪文件`scripts/whisper-portable.manifest`；新构建flags/manifest哈希记录在同一构建清单，算法代码不改。旧ZIP与失败日志保留为排除交付的证据，最终交付文件名带`Complete-Final`。

实现依据：[微软进程级UTF-8说明](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/use-utf8-code-page)。修复只承诺当前受支持Windows路径，不外推很老的Windows版本；也不自动提升既有其他模块深路径限制。

## 完整包与搬家

完整ZIP包含应用及CaptureHost的.NET自包含运行时、SQLite/录音导出依赖、独立CPU/Vulkan EXE、base模型、许可证/来源清单与使用说明。默认 **CPU + base + auto语言 + 2线程**，不要求新电脑具备某个GPU。开启Vulkan时在教程指引下选择`Tools/whisper-vulkan/whisper-cli.exe`并选择Vulkan硬件；未匹配后端明确失败，不静默谎报加速。

根目录`bundled-whisper.json`仅含相对路径；用户Data/settings配置优先，仅当用户配置不存在时用随包默认值。解析以程序自身目录为基准，与启动工作目录无关；包内选项保存为相对路径，包外用户文件保持绝对路径。坏用户配置不自动替换，`..`、网络、ADS及重解析路径拒绝。新增三组回归验证换目录、源位置移除、不同cwd、坏配置不回退和安全边界。

ZIP不包含Data、旧会议、录音、字幕、提示词、原电脑设置、模型训练工具或SDK。迁移后的原包启动、CPU/Vulkan实际WPF识别、完整性与ZIP解压后逐文件核对，最终结果另记STATUS和本地交付汇总。

**尚未完成真实干净Windows机器验证**：本机换目录、限制进程runtime环境、PE依赖闭包和ZIP完整性可提供便携证据，但不能代替另一台没有SDK/.NET/VC预装的电脑。其他GPU/驱动、金融电话会专有词准确率、中文混合长会及原M1/M2现场/声学同步限制不提升为通过。

## 最终收尾（2026-10-02）

UTF-8修正后的引擎再次真实Vulkan全流程通过：base4057ms、large7492ms、识别中取消3288ms、base重试3608ms（`.local/M3D-Vulkan/gpu-utf8-final/report.json`）。新ZIP在中文目录全量654文件hash通过，CPU及Vulkan的实际WPF识别/字幕往返均通过；CPU36547ms、Vulkan3989ms，配置/程序/模型均来自新解压目录，外部DOTNET_ROOT故意无效。新机器实测仍未执行。依赖与哈希见`zip-relocation.json`、`wpf-bundle-final.json`。

安装位置`H:\AI\Whisper\whisper-b5130-vulkan-ProjectEcho-20261002`已更新UTF-8版本并保留旧自有文件备份。最终包本机Data设置为该引擎与用户原large-v3，实际UI确认Vulkan0、9699ms/公开11秒、原配置不变。ZIP不含这份本机Data，仍以CPU/base相对配置开箱使用。

最终交付`artifacts/ProjectEcho-win-x64-20261002-M3D-Complete-Final.zip`；文件字节与SHA-256见同名`.zip.sha256`和`.package.json`，避免包内文档引用自身ZIP摘要形成循环。解压654个清单文件。原失败ZIP保留于`.local/M3D-Vulkan/rejected-package`。最终二进制、模型、清单和ZIP均已核验；包内七项WPF全部PASS。收尾复审还纠正根README的旧CPU-only/模型hash输入/搬家重选说明，文档修正后重新压缩并全量核验，二进制及模型与真实迁移测试所用逐字节相同。所有新实现和测试留在工作区，未提交/推送。

## 来源索引

- [whisper.cpp b5130](https://github.com/ggml-org/whisper.cpp/tree/b5130)与[官方Vulkan构建](https://github.com/ggml-org/whisper.cpp/blob/b5130/README.md#vulkan-gpu-support)。
- [w64devkit2.9.1](https://github.com/skeeto/w64devkit/releases/tag/v2.9.1)；各MSYS2开发工具来源和SHA见下载清单，开发工具不随包分发。
- [base模型固定版本](https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1)，MIT，复用已核验本机文件，不重新下载模型。
- [仅候选测试的Lemonade发行](https://github.com/lemonade-sdk/whisper.cpp-rocm/releases/tag/v1.8.4)。
