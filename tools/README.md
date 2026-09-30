# Tool binaries
项目本地开发SDK放在忽略的 `.tools/dotnet`，开发NuGet缓存放在 `.local/nuget-packages`；版本与官方下载校验见 `dependencies.json`，实际检查见 `docs/21_ENGINEERING_SETUP.md`。这些不属于端用户发行包。

FFmpeg/ffprobe待M2选定并核验，whisper.cpp/模型仅M3按需配置。不要下载不明构建或把SDK随录音应用分发。Git复用已安装版本，本地仓库无远程推送授权。
