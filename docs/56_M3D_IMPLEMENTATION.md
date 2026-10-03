# 56｜M3D 本地识别升级与界面调整

追加授权进展：用户随后要求配置真实Vulkan、测试并制作附base模型的新机ZIP；当前Vulkan部署与完整包合同见[57](57_M3D_VULKAN_AND_COMPLETE_PACKAGE.md)。本文原CPU-only与GPU未验段落保留为追加授权之前的验收快照，不代表57的最终结果。

2026-10-01，Windows 实际实施。仅本地文件/合成测试接口，没有采集、启麦、入会、上传、下载模型或改变系统策略；不进入 M4。已运行的旧版用户进程未关闭。

## 实现范围

| 用户项目 | 实现 |
|---|---|
| 1 硬件与 initial prompt | CPU / Auto / Vulkan / CUDA，CPU线程1–64、引擎GPU编号0–63、可选4000字符提示词；本机配置往返和逐次参数留存 |
| 2 录音页大字 | 删除“专注当下，完整记录” |
| 3 会库提示栏 | 删除“左侧按日期选择历史会议…”整栏 |
| 4 录音状态 | 左侧新会议旁闪烁红点；暂停Ⅱ、收尾等待状态；Saved隐藏 |
| 5 语言按钮 | EN/中保留，左对齐，有边框且按文字收窄 |
| 6 点击会议 | 不同页、已选同会的鼠标/键盘点击都回会库；不清同会编辑；跨会保留本次运行的未保存归档/字幕草稿 |
| 7 识别状态 | 对应会议右下角闪烁“识别中…”；点击回该会本地识别页；真实任务开始/完成/取消/失败生命周期清理 |
| 8 多模型版本 | 每次仍是独立原稿，修订仍保留父版；两个选版菜单加入模型/完整时间/类型/短码，刷新保留各自选择，旧版可导出 |
| 9 导出页标题 | 删除“一份完整、可带走的声音”及其副标题 |
| 10 SHA-256比对 | 删除模型预期哈希输入及比对；兼容旧字段但忽略其值；保留音频/原稿完整性及本地审计指纹 |
| 追加 配置教程 | 两个“如何配置”打开同一独立教学窗对应页；三语言、CPU/Vulkan/CUDA选择、官方资源、完整解压/选exe、模型下载与新手排错；只显式点击链接才打开浏览器 |

普通 Recording/Paused/Stopping 期间允许会库纯导航，保持 Host、录音状态、采集标题、mic选择和控制请求不变，不加载历史混音；所有回放入口仍禁用。活跃10秒短试录维持会库禁选，因为其完成事件使用共用试听路径，不能把试听结果关联错会。红点表示会话状态，不表示音量或平台mute。

## 真实硬件合同

核验基准为已有 whisper.cpp **1.9.4 / b5130** 的本机 `--help` 和官方源码：[CLI参数](https://github.com/ggml-org/whisper.cpp/blob/b5130/examples/cli/cli.cpp)、[后端选择](https://github.com/ggml-org/whisper.cpp/blob/b5130/src/whisper.cpp)、[Vulkan说明](https://github.com/ggml-org/whisper.cpp/blob/b5130/README.md#vulkan-gpu-support)。没有虚构 `--vulkan` 参数：CPU传 `-ng`；其他模式传 `-dev`，所选程序必须含对应后端。提示词以 `--prompt` 的独立 ArgumentList 项传入，不经过shell，不允许额外任意参数。

stderr只有枚举GPU或加载DLL不够：解析实际 `whisper_backend_init_gpu: using … backend`，继续检查初始化失败。显式 Vulkan/CUDA 若缺失、后端不同或失败就终止本次进程，不导入成功稿；Auto允许CPU回退，并记录真实后端证据。未报告后端保持未知，不猜测显卡利用率。唯一进程尝试仍有取消、超时、输出大小限制；原轨、采集回调和导出不受ASR失败影响。

`WhisperConfiguration`/通用配置追加 Hardware、GpuDeviceIndex、InitialPrompt；缺字段旧配置默认CPU。`attempt.json`升为`m3-whisper-attempt-2`，记录请求硬件、实际后端证据、提示词、模型/输入指纹及结果版本ID。用户预期模型SHA不再验证或比较；音频/原稿保护不删。提示词仅本地保存，没有自动加入音频导出。提示词提供上下文，不是词典强制替换或专业词准确率承诺。

本机现有程序只包含 CPU 后端DLL。本轮没有安装Vulkan/CUDA引擎，没有实际AMD/GPU推理或速度验收。界面选项和失败保护已经实现，GPU硬件验收独立保持NOT_RUN。

## 已执行验证

- Windows11 25H2 **26200.9457 / x64**，项目SDK **10.0.401**、runtime **10.0.12**、NAudio **3.1.0**；来源模式只为合成文件/已存在公开JFK文件，没有系统音频采集。环境：`.local/M3D/environment.json`。
- Release构建0错误0警告；**186组默认自动回归PASS**，包括4组硬件/参数/失败保护和多模型版本生命周期。`.local/M3D/build-01.log`、`build-02.log`、`.local/TestResults/M3D/default-01/results.json`。
- 实际WPF设置测试：旧配置默认、删除模型hash字段、四硬件模式/提示词/线程/设备的保存重载、非法设置不覆盖、忙中禁改、三语言不改提示词；最小窗口实际PNG目视检查。`.local/M3D/s-151637840/report.json`。
- 实际WPF选版：不同/相同模型独立稿、旧人工修订、回听与校订独立选版、刷新保留、旧版TXT/SRT导出、磁盘重开及原件hash。`.local/M3D/v-151641095/report.json`。模型结果为明确标记的合成输入，不冒称推理。
- 实际WPF导航修复后通过：三语言、同会重复点击、跨会/新录音归档草稿、识别闪烁链接及刷新/清理、普通录音/暂停/保存状态的纯导航和回放锁定。`.local/M3D/n-151916677/report.json`、`wpf-navigation-03.json`。
- 既有WPF会库/字幕跟随与静音媒体/文字工作流/导出关窗流程回归通过，汇总`.local/M3D/wpf-02.json`（其中导航初测FAIL原样保留，后续单独复测）。
- 最终开发版完整七项WPF均PASS：`.local/M3D/wpf-final.json`。增强的版本流程还验证未保存字幕跨版本/刷新/跨会恢复，且不制造保存版本；最小窗口回听/校订控件可达，PNG已检查，见`.local/M3D/v-152253010/report.json`。
- 追加两配置教学按钮，三语言实际打开正确页、复用教学窗、固定官方链接截获（未真的开浏览器）、链接Tag不能改目标、关闭不改配置均PASS；教学窗最小640×560可滚动，固定关闭按钮。最终`.local/M3D/wpf-help-final.json`、`build-help-final.log`。
- 最终默认回归仍**186 PASS / 0 FAIL**：`.local/TestResults/M3D/default-final/results.json`。新增教学窗/按钮属于App UI，未加入录音依赖。

## 真实引擎与多模型测试

入口 `--m3d-real-whisper-probe` 只接受既有官方公开11秒JFK文件的已知hash，明确指定已有CLI、base及large-v3；不读取私人会议或改变模型/配置。生产音频导入→16k副本→ASR→独立原稿/修订→从磁盘重开→旧版TXT/SRT/VTT导出全链路通过。

| 运行 | 实际结果 | 本次耗时 |
|---|---|---|
| CPU / base / 金融术语提示词 | Succeeded，1句 | 2.294秒 |
| CPU / large-v3 / 同提示词 | Succeeded，独立1句，旧版保留 | 41.570秒 |
| Auto / base / 同提示词 | Succeeded，现有引擎走CPU | 1.942秒 |
| 显式Vulkan / CPU版引擎 | 按预期Failed，不新增成功版本 | 0.283秒 |

真实探针整体PASS，`.local/M3D/real-01/report.json`。三次成功稿与一份未改文字的测试子修订均保留；所有音频/模型/程序/旧attempt和版本hash不变。以上用时包含加载、指纹与保存，不能外推其他长度/机器；提示词出现于参数/记录证明传递成功，**不证明金融术语识别质量提升**。固定英文样本不代表金融电话会、中文/中英混合或长会议准确率。

实际WPF按钮运行base/公开JFK也通过，直接填写prompt控件并验证`attempt.initialPrompt`一致，任务启动标记正确侧栏会议，结束清理状态；字幕导出往返与原配置hash保持。入口`--m3-whisper-smoke`，`.local/M3D/real-ui-02.json`。首轮测试断言误用`InitialPrompt`而不是落盘camelCase字段，产生测试KeyNotFound失败；生产识别已成功，修正断言后复测通过，`real-ui-01.json`和原失败堆栈保留。

## 发现与修复、未测

首个开发版WPF启动器未设置项目内DOTNET_ROOT，未产生验收证据即终止自有测试进程；补齐子进程环境后重跑，见`wpf-launch-01-not-run.txt`。未改全局PATH。

导航初测发现列表record加可变通知字段后相等/hash受WPF订阅影响，导致重新选择失效；改为稳定对象引用身份的class，复测通过。不是把失败条件删掉或放宽；旧失败堆栈与汇总保留。另复审修复识别A期间显示B进度归属混淆、异步加载完成后错误切换其他会议标签的竞态。

未执行物理鼠标/人工听感、实际AMD/Vulkan/CUDA推理、金融专业语料准确率、更多显卡/驱动、多显示器DPI/读屏、干净Windows。GPU只读WMI查询在沙箱被拒，未改权限或驱动。M1/M2既有mic断续FAIL、两小时/声学同步/平台现场/深路径限制全部保留，本轮不重跑采集也不提升其状态。

## 交付

自包含包目标`artifacts/ProjectEcho-win-x64-20261001-M3D/ProjectEcho.exe`；包内验证最终结论记于STATUS。保留整个文件夹。引擎/模型继续独立配置，不随包分发，不下载二进制。未提交或推送远端。

最终631文件完整包已经生成，实际完整性、默认WPF启动和自带Host的coreclr/hostfxr/无采集IPC通过（人为设置不可用的外部DOTNET_ROOT，核验使用包内运行时）；不等于干净Windows验收。包内七项WPF均最终通过，`.local/M3D/portable-wpf.json`及`portable-transcript-retry.json`。其中字幕流程首轮在恢复剪贴板时遇到Windows `CLIPBRD_E_CANT_OPEN`，其他六项通过；同包、同断言独立重试字幕流程通过，没有修改产品或降低断言，原FAIL保留。最新清单hash和本机配置复制证据见STATUS及`.local/M3D/delivery.json`。

代码复审未发现阻断项：任务保存使用启动时的会议/模型/输入，UI晚到回调再次核对所选会议；GPU显式失败不生成成功稿；帮助只打开固定官方URL，不带会议或提示词。教学窗在打开时选择界面语言，若在主窗切换语言，关闭重开教学窗即可刷新语言。
