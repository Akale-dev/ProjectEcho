2026-10-23 **已同步为 M5F Pro，M3P_SYNC_APPLIED**。用户本轮明确要求同步，验收后同步门槛已解除。普通/人物/Qwen字幕适配、实际结果和本地便携包见 [M5F交付记录](96_M5F_M3P_TIMING_SYNC.md)。下文保留原迁移清单与2026-10-15发布时的历史状态。

# M3P Lite 验收与后续 M5 同步记录

2026-10-15，用户要求从 M3O 升级到 M3P，并保留相关记录供验收后同步 M5。此次实现范围仅 Lite；M5 产品代码和既有发行包保持。

- Lite 基线：`d91c26a511e8e03e5a3b76263b4e2efefd0336ec`（M3O），工作分支 `codex/m3p-lite`。
- Pro 基线：`a3fb9b79945a42d880db681a0862d45e81790b6b`（M5E）。历史 M3K–M3O 同步已经完成，此处等待验收只针对 M3P 新增修改。
- 历史状态（2026-10-15）：`USER_ACCEPTANCE_PENDING / PRO_NOT_APPLIED`。用户验收通过后按既有同步授权实施，无需重复索要同步批准。
- 交付与操作说明：[Lite M3P 记录](https://github.com/Akale-dev/ProjectEcho/blob/codex/m3p-lite/docs/94_M3P_SUBTITLE_TIMING_ERRORS.md)。证据与前像：`lite/.local/M3P/`。

## 迁移范围

| 文件/组件 | 应迁移内容 | Pro 注意事项 |
|---|---|---|
| `TranscriptRevisionDraft.cs` | 时间诊断、循环查找；保存仍允许无时间文字 | 保留完整文字、独立文字/定位完整性，禁止把缺失时码改成猜测值 |
| `MainWindow.TranscriptTiming.cs` | 寻找下一条、展开完整版本、滚动定位、已校验音频边界 | 根据 Pro 普通稿/人物稿选择合同适配；不可误切人物稿或丢草稿 |
| `MainWindow.Transcription.cs` | 条目高亮属性、导出前提示与首错选择、草稿保存提示、搜索隐藏错误处理 | 只迁入增量，不覆盖 Qwen、speaker、人物字幕导出和其错误保护 |
| `MainWindow.TranscriptDrafts.cs` / `TranscriptionView.xaml` | 按钮/提示、浅红背景、寻找按钮在导出左侧并一起换行 | 保留人物编辑区和 Pro UI |
| `Localization/*-m3p.json` | 简体源文、繁体和英文提示 | 合并新增键，不替换 Pro 字典 |
| `M3pTranscriptTimingTests.cs` / `MainWindow.M3pTimingSmoke.cs` | 6 项自动回归和实际 WPF 检查 | 添加 Pro 人物稿/Qwen 待定位场景，旧 untimed/草稿禁导出按钮断言应改为“可点击但必须提示” |
| 版本、打包、验证脚本 | 本次 Lite 专用版本与便携包 | M5 另命名新版本/新目录，不能照搬 Lite 发布脚本或整包 |

## 必须保留

M5Dfix 的 Qwen 无效 raw 时间窗仍保全已验证全文，未定位窗明确 `untimed`，文字/定位完整性分开；其原始对齐质量限制不因高亮功能而升级。人物聚类、改名、人工稿及所有旧版本保持。原稿、私有输入与模型不进入补丁/包。

同步后重新完成 Pro 构建、完整自动回归、普通/人物字幕和 Qwen 待定位场景的实际 WPF、最终包及哈希验证。本轮新增源码推送和已核验便携包发布授权；真实采集、麦克风、模型下载、私有数据上传和关机没有新授权；原目标机/质量/clean Windows 缺口继续单列。

## Git 发布（2026-10-15）

用户明确要求推送更新并上传便携包。M3P 源码发布至 [codex/m3p-lite](https://github.com/Akale-dev/ProjectEcho/tree/codex/m3p-lite)，便携包与 SHA-256 校验文件发布至 [M3P Lite Release](https://github.com/Akale-dev/ProjectEcho/releases/tag/m3p-lite-20261015)。M5 分支只更新交接说明；M3O 旧分支、标签和 Release 保留。发布回执及远端校验保存于 `lite/.local/M3P/publish/`。
