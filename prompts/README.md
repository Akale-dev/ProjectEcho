# 分阶段任务入口

2026-10-04：这些文件是未来任务模板，保存或读取它们不代表已授权执行。先读 AGENTS.md、docs/00_HANDOFF.md、STATUS.md、docs/12_MILESTONES.md；只运行用户当前明确要求的阶段。

当前安排：**M3Hfix → M4 → M5 → 用户另行启动M6**。实现状态及剩余现场验收以 STATUS 为准，总计划见 [70](../docs/70_M4_M6_LOCAL_ASR_ROADMAP.md)。M4与M5已按各自明确请求实现；M5的自动人数/叠声质量失败和独立未测须保留，P5不全绿。继续任务先读STATUS、[75](../docs/75_M5_MEETING_SPEAKERS.md)，M6仍未启动，不沿用历史录音、mic、上传、推送或关机授权。

| 阶段 | 当前入口 | 范围 |
|---|---|---|
| M4 | [05_M4_CHINESE_ASR.txt](05_M4_CHINESE_ASR.txt) | Qwen CPU/Vulkan、VAD候选与句子定位；先做M4-A可行性 |
| M5 | [06_M5_SPEAKER_DIARIZATION.txt](06_M5_SPEAKER_DIARIZATION.txt) | 同场换人、聚类、显示改名与标签导出 |
| M6 | [07_M6_TRANSCRIPT_QA.txt](07_M6_TRANSCRIPT_QA.txt) | 可选第二ASR复核、争议回听与带标记导出 |
| M7（暂缓，原M4） | [08_M7_PROJECT_ANALYSIS.txt](08_M7_PROJECT_ANALYSIS.txt) | 保留项目分析规划，启动时另定路线 |
| M8（可选，原M5） | [09_M8_OPTIONAL_TAB.txt](09_M8_OPTIONAL_TAB.txt) | 精确单标签采集，保留用户/组织许可边界 |
| M9（原M6） | [10_M9_RELEASE.txt](10_M9_RELEASE.txt) | 扩大发行验证，可独立补验 |

旧 `05_M4_PROJECT_ANALYSIS.txt`、`06_M5_OPTIONAL_TAB.txt`、`07_M6_RELEASE.txt` 只保留编号迁移提示，不再作为当前同号阶段的实施指令。既有 M0–M3 和 `08_RESUME_AND_FIX.txt` 文件名不变；不要按文件排序推定新的阶段含义。修复入口也不能自动跨阶段启动新功能。
