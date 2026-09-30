# M0 应用源码

`ProjectEcho.slnx` 包含 App（WPF）、Core、Capture.Windows、CaptureHost、Export 及 Tests。使用项目根的 `scripts/Dev.ps1 -Action Build|Test|Run`。App 构建产物旁会复制完整的开发版 CaptureHost 依赖目录。

这只是用户主动的 10 秒音频可行性样机；实际平台采集尚未验收，不是 M2 便携发行包。M0 状态、命令与已知限制见 `docs/24_M0_REPORT.md`。`reference/` 仍为历史设计参考，不是运行时实现。
