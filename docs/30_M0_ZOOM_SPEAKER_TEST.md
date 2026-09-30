# M0 Zoom 桌面扬声器测试音验收

日期：2026-09-28。结论：**两份 Zoom 进程树试录均有声，原轨、带警告混音和 checkpoint 一致；最新一份由代理点击回放，用户确认与 Zoom 测试音相同、连续正常。设备帧计数恒零仍导致完整性审计 FAIL。**

本场景是客户端扬声器测试音，不是实际会议。T03 的“用户同意的 Zoom 桌面短会”及独立 mic 未完成；不把部分证据改写成平台全通过。本轮未重新录制、未入会、未开启 mic、未上传。

## 环境、范围和实际操作

- Windows 26200.9457 / X64；项目 SDK 10.0.401、NAudio.Core/Wasapi 3.1.0 沿用现有锁定。
- 本次从清单指向的 Zoom 可执行文件实际读取 FileVersion：**7.1.9.48550**。它与早期记录的版本不同，本轮不沿用旧版本号。
- 用户报告已在 Zoom 开启音频测试并录制；发现两份新 ProjectEcho 产物。两份清单都是 `ProcessTree`，根进程与音频进程同为 Zoom、同 PID/路径，启动时间存在。PID 只作为当次证据，不复用到下一场。
- mic=false、设备 ID=null，无约定的 mic 轨/文件；`reusableSelection=false`。当前 UI 来源已清空、mic 关闭，回放按钮显示“有警告”。落盘缺少 mic 不能单独证明全部内存隐私行为。
- 用户确认停止 Zoom 自带测试音后，代理在 ProjectEcho 点击最新录音的试听按钮；用户确认“听到了，相同且正常”。回放前 CaptureHost 数量为 0。

## 两份产物的数值与检查

| 项目 | 第一次，22:30:53 | 第二次，22:32:09 |
|---|---:|---:|
| 会话经过时间（秒） | 10.0010948 | 10.0093056 |
| 原轨帧数，48kHz 双声道 float32 | 479040 | 479520 |
| packet 数量 | 998 | 999 |
| 混音帧数，48kHz 双声道 PCM16 | 480053 | 480447 |
| 原轨峰值 / RMS | 0.695679 / 0.139919 | 0.695679 / 0.140964 |
| 混音峰值 / RMS | 0.695313 / 0.139738 | 0.695221 / 0.140762 |
| checkpoint | 10，全部一致 | 10，全部一致 |
| WAV PCM 起点 | 原轨 58 / 混音 44 字节 | 原轨 58 / 混音 44 字节 |
| QPC 无效标志 / 相邻 gap、overlap | 0 / 0、0 | 0 / 0、0 |
| 设备帧位置异常包 | 997；原值恒零 | 998；原值恒零 |
| Complete / 完整性审计 | false / FAIL，1995 项 | false / FAIL，1997 项 |
| 人工听感 | NOT_RUN | 用户确认相同且正常 |

两次审计错误均仅为 `capture_incomplete` 与设备位置不可依赖/重叠；没有 checkpoint、原轨结构、混音帧长或其他检查错误。混音按经过时间保留了原轨前后空缺，没有将源 PCM 时长冒充整个会话时间。未验证正常停止最后一个完整帧或长录音漂移，不能从这张表推断这些项目通过。

测试音有声由源与混音非零 PCM 支持；来源由用户操作说明和录制清单支持。没有将 Zoom 测试音套用 Chrome 的 660Hz 判定，也没有转写或推断内容。

## 证据与实际命令

目录均位于 `.local/M0Recordings/`：

- `20260928-143053-7b080a9e4a8c423192ba95e6087e12e0`；派生 `mixed-preview-incomplete-73a45132bb0d4475ba7a871415b4d68a.wav`。
- `20260928-143209-9524eab84eb74c439c8ae8dbd9eb762d`；派生 `mixed-preview-incomplete-9505933d0cbe4860bf84ab3640b311ba.wav`。

审计 JSON：

- `.local/diagnostics/recording-audit-20260928-143405-169-0687461d.json`
- `.local/diagnostics/recording-audit-20260928-143405-568-2b22a9a7.json`
- `.local/diagnostics/m0-zoom-speaker-evidence-20260928.json`：范围、人工确认及原件/派生文件 SHA-256。只保存项目内，未分享原始清单、账号或会议标识。

在项目目录执行以下命令，两次均因保留时钟错误而按预期 exit 1；wrapper 会抛出非零退出异常，未将其当作成功：

```powershell
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\20260928-143053-7b080a9e4a8c423192ba95e6087e12e0')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\20260928-143209-9524eab84eb74c439c8ae8dbd9eb762d')
```

本轮未改源码，未重复构建或运行合成集；最近代码基线为产品构建 0 错误、6 NU1900，30 项合成 PASS。NU1900 仍表示依赖漏洞信息服务不可达。不得把该历史基线描述成此次重新执行。

## 剩余项目

Zoom 实际会议、mic、Zoom/Webex 网页、双浏览器同时频率对照、设备时钟完整性均尚未通过。随后 Webex 客户端测试音已补测，结果见 [31](31_M0_WEBEX_SPEAKER_TEST.md)。M0 继续进行，不入会、不启 mic、不扩大为全端点录音。
