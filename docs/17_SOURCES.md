# 17｜官方来源与核验边界

核验日：2026-09-28。以下仅支持API/工具事实；平台兼容、用户机器和数值性能仍待实测。

## S01｜Microsoft Application loopback audio capture
https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/

进程及子进程边界；不绑定物理端点；sample要求build 20348+。并非Zoom/Webex兼容认证。

2026-10-02 补充：微软 sample/结构文档仍列20348；[OBS官方指南](https://obsproject.com/kb/application-audio-capture-guide)与[官方源码](https://github.com/obsproject/obs-studio/blob/master/plugins/win-wasapi/plugin-main.cpp)采用Win10 2004/19041+，锁定NAudio3.1.0包内XML一致。用户要求后项目允许19041–20347兼容尝试，明确标待验，不将任何一方文档当作19043实测结果。见[58](58_M3D_ENDPOINT_RECORDING_FIX.md)。

## S02｜Microsoft Loopback Recording
https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording

端点loopback捕获该输出设备播放内容；与独立mic输入不同。

## S03｜Chrome tabCapture API
https://developer.chrome.com/docs/extensions/reference/api/tabCapture

需用户调用扩展；标签页流捕获；保持本地播放需AudioContext回接；扩展与桌面进程捕获不是一回事。

## S04｜Chromium User Data Directory
https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md

官方user-data-dir参数；分离目录是项目浏览器策略的基础，实际音频范围仍需验证。

## S05｜Chromium Audio Service
https://chromium.googlesource.com/chromium/src/+/main/services/audio/README.md

浏览器音频服务架构；提示不要将标签页renderer当唯一音频进程。

## S06｜NuGet NAudio package
https://www.nuget.org/packages/NAudio

核验页面稳定包3.1.0；NAudio 3目标net9+；按需引用Windows WASAPI组件。

## S07｜NAudio official repository
https://github.com/naudio/NAudio

官方列明process loopback与录音能力；迁移说明提示API变更；M0需真实构建。

## S08｜.NET support policy
https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

.NET10为LTS；不以runtime patch代替SDK版本，不写死未来版本。

## S09｜.NET install scripts
https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script

可非管理员安装到指定目录；本包不执行在线安装。

## S10｜FFmpeg filters documentation
https://ffmpeg.org/ffmpeg-filters.html

amix混合、amerge组合声道、重采样/响度/限幅等参考；具体渲染参数属项目设计。

## S11｜FFmpeg license considerations
https://ffmpeg.org/legal.html

许可取决于构建/启用组件；本包没有FFmpeg二进制。

## S12｜OpenAI file transcription
https://developers.openai.com/api/docs/guides/speech-to-text

核验API文档25MB及支持音频格式；不把API限制外推到聊天界面或其他服务。

## S13｜IAudioCaptureClient.GetBuffer
https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer

device position、QPC position(100ns)和flags；底层时钟与回调到达时刻不同。

## S14｜whisper.cpp official repository
https://github.com/ggml-org/whisper.cpp

本地ASR候选；效果/速度须在实际配置上测量。

## S15｜ChatGPT desktop documentation
https://learn.chatgpt.com/docs/app

工作位置/文件夹与Codex入口；界面随版本变化。

## S16｜AGENTS.md official guidance
https://learn.chatgpt.com/docs/agent-configuration/agents-md

项目指令发现/层级；本包采用根目录AGENTS.md。

## S17｜.NET publishing overview
https://learn.microsoft.com/en-us/dotnet/core/deploying/

self-contained发布；仍需原生依赖/OS验证。

## 不是官方事实的部分
本项目预设、功能优先级、20MB导出预算、30秒原始分块、5秒上传重叠、漂移/延迟/响度目标均为设计决定。尚未调用或验证任何Zoom/Webex SDK；采用Windows音频路径捕获，并在验收矩阵逐项记录。

## 后续版本核验
M0锁定真实SDK/NuGet版本；M2选择编码器来源与license；M3选择ASR模型并测量；未来外部服务能力在实际使用当天核对，不据此自动上传资料。

## S18｜本次开发SDK来源（2026-09-28）
https://dotnet.microsoft.com/en-us/download/dotnet/10.0

https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json

实际选取Windows x64 SDK 10.0.401，ZIP校验官方SHA-512后项目内解压。来源与校验值见 `tools/dependencies.json`；WPF/NAudio环境小程序验证不等同采集API或M0验收。
