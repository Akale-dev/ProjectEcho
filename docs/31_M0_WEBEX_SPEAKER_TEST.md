# M0 Webex 桌面扬声器测试音验收

2026-09-28。Windows 26200.9457 / X64，实际 CiscoCollabHost 文件版本 **46.9.0.35800**。SDK 10.0.401、NAudio.Core/Wasapi 3.1.0 沿用锁定。

结论：**用户补充确认第一份录制时没有播放测试音；其近静音结果符合预期，静音对照通过。第二份包含明显测试音，生成带警告混音，回放由用户确认正常，有声测试通过。两份设备帧计数均恒零，Complete=false/时钟审计 FAIL 单独保留。** 两个子场景通过不等于实际会议或完整 T04 通过。

## 实际操作与来源

代理通过 Webex 设置的独立截图进入“音频”，仅点击“扬声器”的测试按钮。未点击“铃声和提示”的全设备测试，也未点击麦克风测试、视频或开始会议。音频页显示找不到麦克风。没有改音量、输出设备或隐私设置。

在 ProjectEcho 重新枚举到活动 CiscoCollabHost 音频 session，选定其进程树；根与音频进程均为当次 PID 32140。预检明确通过，mic 关闭；随后代理点击主动 10 秒试录，启动时回放、停止回放、来源和 mic 控件均禁用。

随后出现第二份同来源试录。其开始按钮的触发者未由代理直接观察，不能将两次均归为代理操作。第二份目录与当前 WPF 试听结果匹配。两份均 `ProcessTree`、mic=false、`reusableSelection=false`；不根据窗口标题或旧 PID 长期绑定。

试听前实际观察 Webex 扬声器按钮恢复为“测试”、输出指示无活动；CaptureHost 为 0。代理将 WPF 恢复前台，点击最新混音的“试听混音（有警告）”；用户确认正常。未开启 mic、未入会、未切全端点模式。

## 数值证据

| 项目 | 第一份，22:43:39 | 第二份，22:44:35 |
|---|---:|---:|
| 会话经过时间（秒） | 10.0104821 | 10.0010246 |
| 原轨帧数 / packet | 479520 / 999 | 479040 / 998 |
| 混音帧数，48kHz 双声道 PCM16 | 480504 | 480050 |
| 原轨峰值 / RMS | 0.00003052 / 0.000000377 | 0.445129 / 0.039402 |
| 原轨非零样本 | 146 | 360002 |
| 混音峰值 / RMS | 0.00003052 / 0.000000376 | 0.444977 / 0.039357 |
| QPC 无效标志、相邻 gap/overlap | 0、0/0 | 0、0/0 |
| checkpoint | 10，全部一致 | 10，全部一致 |
| 设备位置不可依赖包 | 998 | 997 |
| 完整性审计 | FAIL，1997 项 | FAIL，1995 项 |
| 场景结果 / 人工试听 | 未播放测试音的静音对照 PASS；未单独人工试听 | 有声测试 PASS；用户确认回放正常 |

原轨为 48kHz 双声道 float32，PCM 起点 58 字节；混音起点 44 字节。审计没有 checkpoint、WAV 结构、混音帧长或其他错误；现存错误全部是 `capture_incomplete` 与设备位置不可依赖/重叠。原轨和失败产物均保留。

用户随后明确说明第一份录制期间未播放测试音，因此更正最初在输入条件未知时的归类：它是符合预期的静音对照，并非有声测试失败。峰值约 0.00003052、RMS 约 0.000000377，且完整保留约 10 秒经过时间，支持该静音结果；这不等于长时间静音、无 callback 或 API 的 SILENT 标志场景已验收。第二份的来源说明、进程身份、音量统计与用户听感分别记录。仅修改预期条件和结论，未改 WAV、清单或原始审计错误。

## 本地文件与命令

会话目录位于 `.local/M0Recordings/`：

- `20260928-144339-697c740322e34e9c995547e7e4637e8b`；混音 `mixed-preview-incomplete-7b399a73c6184e00bf1698e759e7cb23.wav`。
- `20260928-144435-70acf6fb60554691b022ce0f3a71a654`；混音 `mixed-preview-incomplete-abd64c33954c473595434f4e7f728a79.wav`。

审计：`.local/diagnostics/recording-audit-20260928-144538-305-65469019.json`、`recording-audit-20260928-144538-705-fac6ca9d.json`。范围、操作者观察、听感确认及文件 SHA-256：`.local/diagnostics/m0-webex-speaker-evidence-20260928.json`。

```powershell
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\20260928-144339-697c740322e34e9c995547e7e4637e8b')
.\scripts\Dotnet.ps1 -Arguments @('run','--project','tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj','--no-build','--no-restore','-c','Debug','--','--audit-recording','H:\AI\ProjectEcho\.local\M0Recordings\20260928-144435-70acf6fb60554691b022ce0f3a71a654')
```

两次按预期非零退出，wrapper 异常与报告均保留。本轮未改源码、未重新构建/跑合成集，最近代码基线仍为 30 项合成 PASS，构建 0 错误与已有 NU1900。无下载、上传、提交或 push。

## 未完成

Webex 实际会议远端电脑音频、独立 mic、设备时钟完整性及 M1 长录音/末帧均未通过。用户已指示暂缓后续浏览器测试，网页会议和双浏览器隔离保持 NOT_RUN，并标注暂缓。本条只补充客户端静音对照及扬声器测试音的有限证据，完整 T04 保持 NOT_RUN。
