# 18｜依赖锁定与发行

## 2026-09-28工程环境锁定
.NET 10为LTS；本机SDK锁定 **10.0.401**，对应随SDK提供的运行时 **10.0.12**，版本固定在global.json。官方SDK ZIP的SHA-512已核验，来源清单见 `tools/dependencies.json`。[S08]
NAudio.Core/NAudio.Wasapi **3.1.0**已从官方NuGet恢复到项目缓存，完成WPF小程序编译与NAudio基础类型运行验证；采集builder、生命周期和时间戳接口仍须M0实际验证。正式工程按精确版本引用并提交packages.lock.json；不能照抄2.x例子或擅自改preview。[S06,S07]
FFmpeg/ffprobe用于可选编码/探测；whisper.cpp及模型仅M3选配。[S10,S11,S14]
SDK/开发依赖只在忽略的.tools/.local中，不属于端用户发行包。FFmpeg/ffprobe、whisper.cpp与模型尚未安装，尚未选择或审核Windows编码器构建。

## 2026-09-29 M2 编码与打包决策

压缩格式锁定 AAC/M4A，通过现有 NAudio.Wasapi 3.1.0 的 `MediaFoundationEncoder.EncodeToAac` 和 `MediaFoundationReader` 调用 Windows 自带编解码器，不另下载/分发 FFmpeg。本机已执行生成 PCM 的完整编码/逐帧解码和首尾相关性检查；产品 codec 对每次输出重新检查全长、格式与帧数。短片补齐、时间映射限制和干净 Windows 待测步骤见 `38_PORTABLE_GUIDE.md`。MP3 初步探索出现首端延迟及极短片不可解码，不进入可选格式，失败证据不改作通过。

`scripts/Publish-Portable.ps1` 发布 App 与 CaptureHost 各自所需的自包含文件，写 portable 标记、依赖清单、许可和全文件 SHA-256。原始签名 nupkg 的 SHA-512 对照缓存 `.nupkg.sha512`；NuGet metadata 的排除签名 contentHash 单独登记，并与存在的项目锁文件核对，不能混淆这两类哈希。来源必须为官方 nuget.org；压缩编码器是系统前置能力，不随包复制系统 DLL。应用启动不需要 SDK/.NET 预装；是否已通过干净 Windows 的录音/回放/导出流程，仍看实际验收记录。

2026-09-29 六工程已实际运行官方 NuGet 已知漏洞查询（含传递依赖），返回未列出已知漏洞；这不是全面安全认证。历史 NU1900/沙箱网络失败仍保留，证据 `.local/diagnostics/m2-20260929/dependency-audit-normal-user.json`。发布为离线缓存恢复，不重新联网审计。

## M2最小运行版
App/CaptureHost/Core及所需.NET self-contained运行文件 + 导出编码器（完成来源许可核验后）+ LICENSES/THIRD_PARTY/manifest；不包含SDK、源代码机密、browser profiles、调试dump、真实音频或模型。
M2必须交付WAV及至少一种压缩格式（MP3或M4A）；第二种压缩格式可选。M2即可发布，不等待M3/M4/M5；M6在此基础上扩大回归。只复制开发机器App.exe或只publish App不算完整交付。

`dotnet publish`固定win-x64/self-contained=true；WPF首版禁止trim，single-file=false。用文件夹发行比只复制exe更稳妥；不得漏依赖DLL。官方self-contained说明不免除原生系统依赖检测。[S17]

## 离线与便携
在无预装SDK、没有模型的目标Windows环境：解压到可写目录→启动→选择用户数据目录→录音/回放/导出。应用默认不发网络请求；浏览器/Zoom/Webex自身上网不算录音器联网，但测试需区分进程。
不安装虚拟音频驱动、不注册服务、不改系统代理/默认浏览器。系统SmartScreen/组织白名单拦截时说明来源与签名状态，遵从批准流程，不指导禁用防护。

## 许可/SBOM
M2交付时就核验全部随包依赖来源与许可，不能留到M6才处理；M6可完善SBOM和更广的发行验证。
记录每个组件版本、源码URL、binary来源URL、hash、build config、license路径。FFmpeg LGPL/GPL取决于构建及启用组件，不可只写“开源所以可随意分发”；分发遵循实际构建义务。[S11]
模型文件的license独立核对；不把仓库代码license直接套到所有模型。外部模型/API不在MVP依赖中。

## 发布说明必须真实
列出已测试OS与四个主要平台模式、已知降级、未测场景；给出完整文件hash/版本。M0/M1内部样机不能标成生产就绪。
