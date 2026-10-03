# M3H 麦克风按钮图片生成

- 途径：内置 `image_gen.imagegen`；ON 为新图生成，OFF 为引用 ON 的精确对象编辑；生成前读取 imagegen skill，编辑前 `view_image` 检查 ON。
- 未使用 CLI / API key / Python / SVG 代替；没有裁切、重绘、程序化改色或修改生成的 PNG。
- 需求：固定正方形 canvas、透明 alpha、同一话筒同一位置尺寸；开/关两图，按钮按压效果由 WPF 实现。
- 最终资源：`src/ProjectEcho.App/Assets/MicOn-M3H.png`、`src/ProjectEcho.App/Assets/MicOff-M3H.png`。
- 状态：两张均完成并复制进入项目；透明 alpha 与一致尺寸已检查。
- 内置生成器原输出另行保留；项目使用的是Assets目录副本。

## 检查结果

通过 Windows System.Drawing Bitmap 只读检查，均为 **1254 × 1254 / Format32bppArgb**；每张超过一百万完全透明像素。详情 `image-alpha-inspection.json`。alpha >= 128 的主体边界 ON 为 `[111,112,1141,1137]`，OFF 为 `[108,109,1143,1137]`。生成结果与要求的 1024 尺寸不同，但二者 canvas 完全相同；两状态主体边界差异折算到64px为最多约0.16px，无须裁切。肉眼检查话筒尺寸、脚座和外圈匹配。

建议消费端固定 Image Width/Height，保留整个正方形canvas，不按非透明边界各自裁切；按压使用居中的 RenderTransform ScaleX/Y，而不改变Margin、Padding或布局位置。未修改XAML或进行WPF按钮验收，由主代理整合后执行。

## ON prompt

```text
Use case: stylized-concept.
Asset type: production PNG for a Windows desktop microphone toggle button, viewed at 64 px.
Primary request: generate ONE premium, restrained 3D microphone-ON icon, not a mockup.
Scene/backdrop: genuinely transparent RGBA background, no surface, no backdrop, no checkerboard pixels, no rectangular button plate.
Subject: front-facing simple studio microphone with rounded capsule and short U-shaped cradle, stem and small foot, surrounded by a thin rounded circular ring. Everything strictly symmetrical and upright. Muted teal ring and soft teal capsule accents, neutral graphite and satin silver microphone hardware, subtle smooth ceramic/metal texture. Polished, simple and professional, immediately legible at small size. No tiny mesh details, cables, decoration, text, letters, labels or logos.
Composition/framing: square 1024 by 1024 canvas. Circle CENTER exactly at (512,512), outer diameter 800 pixels, no elements outside circle. Ring occupies exact visual bounds x=112..912 and y=112..912. Microphone centered at x=512, within y=244..780, width about 310 pixels. Orthographic straight-on view, no tilt and no perspective angle. Generous truly transparent canvas margin. This image will have a matching OFF variant using the EXACT same ring and microphone geometry and registration, so do not use asymmetric shadows or glow.
Lighting/mood: subtle top-left studio highlight, very soft ambient shading attached only to the object, small high-quality tactile bevel, no floating cast shadow.
Constraints: transparent alpha, crisp clean anti-aliased silhouette, consistent contrast on white or charcoal UI backgrounds; icon only, centered fixed square canvas, no text, no multi-icon sheet, no red slash in this ON asset.
```

## OFF prompt

```text
Use case: precise-object-edit.
Image 1 is the exact edit target: the microphone ON icon that you just generated. Create only its OFF state.
Change only these items: recolor the teal capsule to neutral matte graphite / dark satin silver; recolor the teal circular ring to muted rich red; add one elegant red diagonal prohibition slash from lower-left to upper-right, with the same bevel and material as the ring, connecting INSIDE the circle and never extending outside its outer silhouette.
KEEP ALL GEOMETRY AND REGISTRATION: exact square canvas pixel dimensions; same transparent padding; same ring center, thickness, scale and outer pixel bounds; same microphone center, capsule shape, hardware, stand, foot, object scale, exact position and lighting. The microphone must not shift or rescale even one visually appreciable pixel. The ring outer silhouette must remain at its current bounds. Same orthographic front view, no rotation, no perspective. On and OFF will be swapped in the exact same 64 px desktop button, so outer ring and microphone MUST register exactly.
Genuinely transparent RGBA background, preserve transparency including inside the ring. Clean alpha edges, no cyan fringes, no ground shadow. No text, letters, labels, backdrop, square plate, checkerboard pixels or extra decoration. One icon only.
```
