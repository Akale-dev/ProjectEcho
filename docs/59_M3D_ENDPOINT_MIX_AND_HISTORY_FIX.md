# 59｜端点混音、音质与会议处理结果修复

2026-10-02，M3D既有功能修复。用户确认这些反馈都来自新电脑：Windows 10 Pro 21H1 **19043.2364**，输入和输出均为 **Jabra LINK 860**。用户确认 **remote原轨WAV单独播放正常**，派生混音较差且较轻。没有读取或上传目标机音频、完整日志或会议内容。

## 用户日志的准确解释

- 第一例：原件落盘后带有remote断续/缺口警告，派生混音以“同一路来源时间范围重叠”失败。不能称完整捕获；原件保存状态和派生混音失败是两件事。
- 21.731秒mic关的例子：WAV/M4A生成与解码通过；`CaptureResult.Complete=false`是旧式设备时钟条件未满足，不等同编码失败。
- 22.645秒中途开mic的例子：有警告的全时段WAV/M4A生成成功；实际未观察到的声音区间仍为缺口，生成全时段文件不意味着找回了未采到的声音。
- 设备与原始时间戳的完整性警告不被删除或改成成功。当前代码和合成复现支持下面几个缺陷；目标设备的全部现场根因仍须新版复测。

## 已实现

### 按选中会议显示处理结果

处理状态、后台任务归属和“打开导出目录”改为按规范化会议路径维护。A的后台混音/导出完成或失败时不覆盖当前选中的B；无结果的会议显示自己的空状态。关闭/取消、新会议清选和切换数据目录同样不会残留另一场会议的结果。

新结果保存在本会议的`processing-result.json`，仅作本地界面历史，绑定源meeting清单hash。导出目录另绑定本会议的导出凭据与render hash，保持相对路径以便整包搬家；拒绝跨会议凭据、路径穿越和重解析点。已有音频仍独立校验，历史文字不作为完整性证明。旧版本没有此文件时，从属于当前会议的受约束派生元数据恢复历史显示；v4历史与v5可播放文件区分，旧混音需主动重生成。

### 合法量化重叠不再误报混音失败

写入/恢复原有合同允许最多一个输入帧的负QPC量化偏差；旧混音在当前包有discontinuity或设备有效性变化时不做量化映射，随后将这种已保存的合法微小重叠一律拒绝。合成旧实现复现同一错误，新实现通过。

修复仅在同格式、同段、前包已保留到尾、后包从原包首帧开始、QPC递增且设备不倒退时，允许将前一完整原包的派生区间收至后一锚点。超过一帧仍需原有有界抖动证据；不跨段，不填正缺口，不改原PCM/QPC，不把尾部一个碎片单独压缩成负时长。

### 平稳设备时钟映射与音量

旧版对每个20ms左右的包按相邻QPC拉伸，±760µs一类API时钟抖动可能成为反复的播放速率变化。原来的常量PCM测试能发现空洞和边界错误，却看不出这种调频失真。

v5在设备帧有效且严格连续的同一段内，采用约一秒的平稳窗口；端点使用真实锚点，逐个检查派生边界距原QPC不超过2ms，超界则按真实锚点拆窗并最终保留既有合法映射。真实gap、discontinuity、Pause/MicOff或格式变化切断窗口；QPC-only来源不获得新的设备时钟豁免。跨音频包/块的线性插值只在证实相邻的PCM间读取一个邻样本，不跨缺口/关麦。派生清单记录窗口规则、实际平稳映射包数、最大校正幅度；原件不改，未声称解决全部长时漂移。

来源增益从固定0.4改为1。先以浮点完整混音测真峰值，再做必要的整段固定衰减后写PCM16，避免先削波后“保护”；继续独立复核真峰值与全长。回听播放器初始音量由WPF默认值改为明确的1，不改变Windows音量或设备设置。仍输出既定单声道混音，不扩展为立体声编辑器。

派生版本：`m2-qpc-mono-5-stable-device-unity`。旧v4文件保留，不默认为新版；用户可对已有正常原轨主动重生成混音，无需重录才能应用新派生算法。

### 开麦准备不阻塞已运行的remote

旧实现先启动remote，再在同一采集线程同步激活/协商mic；中途mic_on也会同步初始化，驱动响应慢时可能耽误remote读包。修复将mic客户端准备与采集服务绑定分开：一个MTA辅助线程只激活/协商/Initialize，尚未Start或取得采集服务；原采集线程继续服务remote，并负责GetService、Start、GetBuffer/Release及释放采集服务。仍只有一个CaptureHost。

初次同时开mic时，两路客户端和原轨写入准备好后才开始。中途开mic期间主循环继续远端采集/健康检查；mic_off、pause、stop及系统长调度缺口自动暂停均取消待完成准备。晚到客户端由准备线程释放，不能启动或保存mic PCM；不等待卡住的驱动初始化完成才确认已运行来源的停止边界。10秒准备超时不启动mic；未退出的准备任务不会被重复创建或换设备。

线程规则依据：[IAudioCaptureClient生命周期](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudiocaptureclient)。回听默认值依据：[MediaPlayer.Volume](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.mediaplayer.volume)。这些文档不是Jabra原生调用实测结果。

## 验证记录

开发机为Windows11 25H2 **26200.9457/x64**；SDK10.0.401、runtime10.0.12、NAudio3.1.0。源模式为合成文件/内存来源，mic未启动。环境：`.local/M3D-EndpointFollowup/environment.json`。

- 重叠：旧算法4 FAIL/1 PASS，新窄修复5 PASS；`.local/M3D-EndpointFollowup/OverlapProbe/{before,after}/results.json`。
- 旧Win10Fix二进制对照：引用5个旧包DLL，SHA逐一一致；7项新音质测试在旧版0 PASS/7 FAIL（预期红测）。997Hz与扫频参考失配，匹配SNR分别约−13.9dB、−24dB；这不是硬件噪声或听感评分。无抖动强双源测得固定增益0.399988，支持固定衰减缺陷。证据`.local/TestResults/M3D-EndpointFollowup/quality-before-01/results.json`及`QualityBefore/binary-verification.json`。
- 新音频算法阶段：**213默认回归PASS**。16k/44.1k/48k合成正弦/扫频相对独立设备域参考SNR约84dB、低幅度源增益约0.99997；强双源为整段线性衰减，SNR约93.4dB、峰值约−2.01dBTP，原件hash不变。证据`.local/TestResults/M3D-EndpointFollowup/audio-01/results.json`及其`endpoint-audio-quality/*/quality-evidence.json`。
- mic准备：8新测试及既有边界/校验合计14 PASS；合成慢驱动验证远端继续读包、GetService/Start/Release归属原线程、初始准备/stop、mic_off/pause/stop取消、自动系统缺口暂停、失败后显式重试。首轮独立审查发现自动暂停漏取消待开mic，已修复并补测；保留旧日志。证据`OverlapProbe/mic-preparation-reviewed/results.json`。
- 最终完整回归 **221 PASS / 0 FAIL**，Release **0警告0错误**；`.local/TestResults/M3D-EndpointFollowup/default-final/results.json`和`build-final-01.log`。此后只调整UI历史/测试截图，App另行零警告构建并验证；音频/采集逻辑未再修改。
- 实际WPF **PASS**：切换A/B/空会议、后台晚到、失败/取消归属、新窗口重读、实际旧v4合成会议历史、未知schema/变更索引/跨会议路径拒绝、旧音频仍禁播放与导出、原件hash不变。证据`.local/M3D-EndpointFollowup/ui-064402547/report.json`。不是Win10现场/物理键鼠/实际麦克风验证。
- 新ZIP逐文件及解压启动检查见交付后的`.local/M3D-EndpointFollowup/zip-verification.json`，以实际结果为准。

**交付核验已实际PASS：**ZIP为 **260,754,924字节**，SHA-256 `3d5b97c807585765f928463115086cadd3838e1de032632dc9aabe31296aeb24`。656清单文件+manifest逐一检查，解压到`artifacts/Echo59`后默认WPF/Host IPC与自带运行时通过，回听Volume=1、mic默认关、无自动来源/采集。直接使用解压包另跑完整会议处理结果WPF流程也PASS（`ui-064744883/report.json`）。Core/Capture/Export与221回归所用DLL逐一hash相同，ASR引擎/base与旧包相同且本轮未重新运行模型。证据：`zip-verification.json`、`bundled-history-ui.json`、`tested-components.json`及`.local/diagnostics/portable-20261002-144646/`。本段为打包后仓库追加结果；包内说明指向独立证据，不宣称目标电脑通过。

**未测：**新电脑19043/Jabra本次修复后的真实录音、原生跨MTA客户端交接、主观回听、长期时钟/双轨声学同步；旧日志中未观察区间无法恢复。进程兼容尝试保持此前状态，不把本轮端点合成结果当其现场验收。没有上传、改设备格式/系统音量、注册表策略、全局PATH或其他项目，没有推送。

## 使用新版

完整新包名为`ProjectEcho-win-x64-20261002-M3D-AudioFix2`，保留M3D、Win10兼容尝试及CPU/Vulkan/base。请解压到新短目录，保留旧包/Data。设置中选择旧Data，选中会议后查看其历史记录；点击“生成 / 重试完整混音”从原轨生成v5，再比较回听。

新版录音现场验证须用户主动执行：Jabra有声开始+mic从开始开启、Jabra有声开始+中途开mic、mic始终关闭三项各短录，检查停止、原轨保存、混音和回听。若还有失败，仅反馈新版错误文字和哪项场景，无需上传音频或Data。真实断续仍会保留警告。
