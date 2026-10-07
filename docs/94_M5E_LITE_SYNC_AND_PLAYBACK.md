# M5E Pro：保留 M5Dfix，同步 M3K–M3O

2026-10-06。用户要求完成 M3O 后立即同步 Pro，并再次强调保留 Qwen 修复。合并基线为工作区实际 **M5Dfix Pro**，不是历史 M5D 或 Git HEAD。修改前 489 份实现/文档输入保存在 `.local/M5E/pre-change/`；M3N Lite 的 423 份前像在 `lite/.local/M3O/pre-change/`，旧发行包哈希另存。

## 同步范围

M3Hfix 暂停时间轴及 M3I/M3J 修复已由 M5D 合入；本次以 Lite 已提交 M3J 为三方合并共同基线，迁入之后的 M3K、TimeFix、M3L、M3M、M3N 和 M3O。没有用 Lite 整份应用覆盖 Pro。

- 紧凑录音/会议库/校订界面；固定两位小数时间编辑，未编辑边界保留原始毫秒；字幕多段草稿一次保存、未保存关闭提醒。
- 记忆数据目录与不可用守卫；启动自动刷新会议库。
- Whisper 模型无关短句整理，文字和标点保全；不改变 Qwen 对齐算法。
- 默认关闭的自动结束、1/5/10 分钟、固定根进程退出或组合来源不活动；麦克风去直流低噪判断；实时配置经 CaptureHost 确认后生效。正常停止/尾包排空、缺包不冒充完整等原边界保留。
- 四向秒数跳转与字幕方向键；M3O 数字轮廓居中、绿底三角从当前位置播放、暂停双竖线不变。

## Pro 保留与适配

保留 M5Dfix 的 Qwen 全文保全、坏定位窗口 untimed、文字/定位完整性分离和人物词界隔离。原 Qwen 识别/定位服务不回退，专用回归与 WPF 待定位场景继续执行。定位质量未因此改善，历史失败不升级。

人物版字幕的批量文字/时间编辑通过独立人物修订服务一次保存，保留人物 ID/显示名、原文范围、分析/音频/源稿哈希及父修订。schema 2 增加 `edit-subtitles` 操作及逐段前后文字/时码；明确改时段标记 `manualTiming`，清除失效候选换人点，不重新推断身份或词界。重新加载严格核对声明变化与父稿；旧 schema 1/2 继续读取。较旧 Pro 不支持新操作，新增稿请用 M5E 打开，旧稿不变。

## 验证

证据 `.local/M5E/`，Windows 本机 Release、合成自动回归、实际 WPF、便携 ZIP 与自包含启动结果完成后追加。新测试覆盖人物批量保存、毫秒、后续改名/人工拆句、无时码导出拒绝、取消与无效输入不落盘、篡改/父哈希拒绝。

没有录音、麦克风采集、私人会议重处理、上传、新模型/依赖下载、推送或关机。实际公司机、clean Windows、物理键鼠/DPI，以及既有 M1/M2/M4/M5 质量缺口仍未升级。M6 未启动。

构建阶段结果：Release 零警告零错误，465/0 默认回归通过（404 既有 Pro、59 Lite 增量、2 人物批量校订）。25 份 Qwen/声学边界/原生实现文件与 M5Dfix 前像 SHA 相同。实际 WPF 已确认双栏播放、紧凑编辑、人物批量文字/时间、版本标识及 Qwen 全文待定位提示；最终包结果另行补记。


## 最终交付与核验

Windows 11 25H2 **26200.9457/x64**，SDK **10.0.401**、runtime **10.0.12**、NAudio **3.1.0**。来源为合成文件及此前已保存的公开样本，没有新采集。

| 验证 | 结果 | 证据（.local/M5E/ 下） |
|---|---|---|
| Release 与默认回归 | 零警告零错误；465/0（原 Pro 404 + Lite 59 + 新人物批量 2） | build-03.log、regression-02/results.json |
| 最终包实际 WPF | 23/23，含 M5Dfix 全文待定位提示及 Pro 四工作页 | bundled-ui-final-03/runs.json |
| 记忆目录与启动库 | 四独立进程全部通过，不可用不回退 | bundled-startup-final-03/report.json |
| Qwen 真实运行 | 已有 0.6B Q8_0 + ForcedAligner Q8_0；公开短中文 CPU、合成中英 Vulkan0 通过 | real-ui/runs.json |
| 人物与 Whisper | 已有公开四人样本，实际人物和 Whisper 证据绑定/保存/编辑/重开通过 | real-ui/runs.json |
| 完整便携包 | 807 文件中文解压 SHA/长度；无效外部 runtime 根下 App/Host 空闲握手通过 | zip-verification-final.json |
| 测试与交付组件 | 两版共 12 个 Core/Capture/Export DLL 位置逐项相同 | tested-components-final.json |
| 保留 | 25 份 Qwen/声学/原生实现输入不变；原生/模型资产保持 M5Dfix；123 个旧发行文件不变；外部模型只读 | qwen-and-native-preservation.json、releases-preserved.json、models-preserved.json |

Qwen 原来坏窗口保全文的 8 组回归及 WPF 全部保留并通过；本轮没有重跑私人 1.7B 会议，也没有修复历史模型定位质量问题。真实短流程通过不等于中文字准率/长会议/自动人数质量通过。

完整包：[M5E Pro](../artifacts/ProjectEcho-win-x64-20261006-M5E-Pro-Final.zip)，**462094449 字节**，SHA-256 **`42f8250ba20610e398013d74bfb37f273e89d8130f91217cdd7f0139433a1777`**。完整解压，运行 ProjectEcho.exe 并选择原数据目录；原稿、人工稿、音频和既有引擎配置保留。M3O [Lite 包](../artifacts/ProjectEcho-win-x64-20261006-M3O-Lite-Final.zip)另行交付。

过程失败均保留：沙箱的 junction/IPC；Lite 反射测试补齐 Pro 原有参数；旧单段保存断言更新为批量语义；负面标签样例保留原 M5Dfix 长字符串；Pro 资产核对基线改为实际 M5Dfix。首候选在 M3C WPF 检查中仍使用“导入页是末页”的 Lite 断言，修正为 Pro 四页顺序后重新封包，候选及失败记录保留在 Candidate-01 和 candidate-01.json。最终包上述检查全部通过。

源码复审、前后 hash 和交付清单保存在本轮 evidence；没有 Git 提交或推送，历史 HEAD 不冒充当前源码。包内说明是封包前快照，本文件与包旁 package.json 补充结果，最终 ZIP 不重写。状态 **IMPLEMENTED / WINDOWS_LOCAL_VALIDATED / PRO_SYNC_APPLIED**；真实会议/麦克风、目标机、物理输入/DPI、clean Windows 与旧质量缺口保持未验，不启动 M6。