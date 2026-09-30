# ProjectEcho 应用图标

本图标是为 ProjectEcho 本地会议录音用途原创绘制的几何图形，没有引用外部图片、商标、字体图标或素材库。深蓝色圆角底托承载三条白色音频柱，右侧薄荷绿声波表示回声与会话延续，珊瑚色小圆点提示录音用途。图标只用于产品识别；实际是否采集仍以界面文字状态为准。

`ProjectEcho.svg` 为可编辑矢量源；`scripts/Generate-AppIcon.ps1` 使用 Windows System.Drawing 按同一几何定义生成 256px PNG 和 ICO。ICO 包含 16、24、32、48、64、128、256px 七种尺寸，由 `ApplicationIcon` 嵌入 Windows EXE，同时供 WPF 窗口使用。侧栏使用 PNG 避免 WPF 将 ICO 小尺寸帧放大后模糊。

生成过程不联网，不安装依赖。仓库所有者尚未声明整个应用的公开再分发许可；本说明不代替软件许可。
