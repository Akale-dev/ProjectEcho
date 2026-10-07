# 75｜M5 同场说话人：实施与验证

2026-10-04。用户本轮明确授权 M5-A/B/C；本阶段不是旧 M5 浏览器扩展。工作基线为 M4-AlignFix，保留该版与所有既有录音验收缺口。本文随实际执行更新，未执行项不计通过。

## M4 基线核对

- 本轮重新执行 Release 构建：零警告、零错误；311 项默认回归通过，均未开启设备采集。证据 `.local/M5/baseline-build.log`、`.local/M5/baseline-regression/results.json`。
- Whisper 与 Qwen CPU/Vulkan 可用；Qwen 原生声学定位按原文和输入哈希绑定，为近似时间，内部 80ms 粒度不等于文字正确或 10ms 精度。
- 旧 elapsed 长暂停样本的 Vulkan 原始词界反序 FAIL 保留；显式 CPU 重试通过，不能把自动后备当作 Vulkan 通过。
- M4 的三小时证据是构造重复压力轨；自然长会、金融/电话/重叠质量、其他硬件、旧 CPU 实机、真正干净 Windows、1.7B 仍未验。M1/M2 原真实 mic、同步和现场缺口不变。

## 范围与数据合同

采用会后独立原生 CPU 进程，完整已保存混音是声音归组依据。用户点击才运行；不读取参会者、真实姓名或跨会声音库，不把 remote/mic 当人物。分析前不删除 VAD 未命中区间。

声音分析保存于 `diarization/analyses/{analysisId}/analysis.json`；带标签字幕保存于 `diarization/revisions/{revisionId}/revision.json`。内部 speakerId 与显示代号/名称分离，原字幕正文不拼标签。每个输出段保留原段及 UTF-16 文字范围，全文字符必须恰好覆盖一次。人工改名、单段归属、合并/拆出、人工切点形成新修订；重跑保留旧分析及人工结果。

句内自动拆分只使用当前文字和当前音频的可验证 Whisper/Qwen 边界。无可靠边界、短插话或重叠证据不足时保留文字和待确认；不按字数造时间，不制造重叠双方正文。新 `pause-excluded-v1`、旧 `elapsed-v1`、导入 `file-v1` 独立保留。

SQLite 为可重建派生索引，读取结果先验证权威 JSON 与依赖；显示姓名单独索引，不污染正文。导出标签为显示前缀，回导不把可读前缀当可信身份。

## 验证记录

执行环境和命令证据集中于 `.local/M5/`。当前本机 Windows 11 build 26200.9457/x64，AMD Ryzen 7 5800X3D，SDK 10.0.401/runtime 10.0.12；来源为已保存或公开测试声音，未启用 mic、未录音、未入会、未上传。

### M5-A：原生声音归组

锁定 sherpa-onnx 1.13.8 / commit 11afbd00、ONNX Runtime 1.28.2、pyannote segmentation 3.0 ONNX（MIT）、3D-Speaker ERes2Net（Apache-2.0）。CPU 单独进程，原生内部重叠 10 秒窗提取特征后全场一次完整链接聚类；不是每窗独立编号。两模型均显式 CPU，min-duration-on/off=0，不先用 VAD 删除短声。精确来源、SHA、依赖许可、评分修正和命令见 [78](78_M5_NATIVE_EVIDENCE.md)。

原官方程序中文路径失败保留；便携副本仅加入进程 UTF-8/longPathAware/asInvoker manifest 后实际中文路径通过，无全局环境修改。记录原/修改后两个 SHA，拒绝未知程序合同。原生二进制与依赖、两权重和许可均进入独立便携包，不需要 Python/Node/SDK。

9.999/10/10.001 秒真实语音分别验证：前两者只给独立推理副本补至11秒，以避开上游≤10秒不走特征聚类的特殊路径；补帧16016/16000/0，原件与时间线不变、越界不静默裁切。`.local/M5/boundary-real-01/summary.json`。

### M5-B/C：实际映射与纠错链路

- `.local/M5/mapping-real-03/report.json`：只读 M4 真实 Qwen 三小时原稿，600句/10433词界覆盖全部600句；真实 Whisper/base 公共JFK得到22个可复用边界。人工改字段没有借用旧词界。
- `.local/M5/policy-real-01/summary.json`：既有公开JFK+合成QPC暂停的独立副本，真实CPU说话人分析→Qwen字幕→导出/重开，新11秒 `pause-excluded-v1` 与旧28.345秒 `elapsed-v1` 均保持；原PCM/QPC及来源文件hash不变。这不是设备采集。
- `.local/M5/pipeline-short-final-01/summary.json`：完整导入/混音/16k→真实CPU自动与已知人数→人物修订→改名/改单段/合并/拆出→TXT/SRT/VTT标签开关/回导→SQLite损坏重建→实际取消与坏ONNX原生失败，完成版本及原件保持。字幕是明确标记的合成文字，验证归属和守恒，不冒称ASR正确率。
- 实际WPF界面验证名称、稳定ID、五种纠正、独立版本、姓名搜索、三语、重开、标签导出、录音优先取消及Recording/Paused/Stopping禁启动/禁播；模型真实按钮和合成界面分别记录，不互相代替。

### 三小时完整生产链路

`.local/M5/pipeline-three-hours-01/summary.json`：10800秒/172800000帧；361段公开真人裁片+静音，含接近EOF的尾声，全场3组。生产适配器实际CPU推理 **565365ms（9分25.365秒）**、峰工作集 **1462222848B**，整个导入/准备/分析/人物修订/导出/SQLite探针 **643983ms（10分43.983秒）**。361条合成时间字幕、9025字符全部保留，无待确认/拆人/误合；这只指该有明确真值的构造样本，不是自然会议质量。

另有原官方程序单独三小时实跑530152ms/峰1462239232B，见78。开发机同时有短回归工作，速度不作为独占硬件承诺。声音由公开片段重复构造，不能推广为自然三小时会议准确率。

长测使用冻结的托管测试目录；最终托管版本进一步加入线性重叠扫描、按首次可靠发言排序、完整帧音频尾端毫秒上界与读盘校验。`.local/M5/final-long-remap-02/summary.json` 用最终代码加载全部旧版本、重解析同一完整原生输出、重新映射361段及导出/检索均一致，权威JSON未改；这次复查没有冒充重新运行三小时模型。

## 质量结果和失败边界

| 项目 | 实际结果 |
|---|---|
| A-B-A-C-A、20分钟后返回及半音量、三小时跨窗，自动/提示人数 | 真实声音归组保持3人；各项拆人0、误合0，已分配裁片内标签混淆0 |
| 预先冻结的裁片覆盖门槛 | 最低多数标签覆盖约0.889低于0.9，**FAIL保留**。裁片含原内部静音，不能把未覆盖直接说成漏讲话或DER |
| 受控电话、10dB噪声、120ms回声、220ms插话 | 自动/提示模式按冻结构造门槛通过；回声仍有约4.71%标签竞争，不能说无误差 |
| 受控叠声 | 自动/提示均拆人1，标签竞争约9.72%，**FAIL**；正文仍完整，不能捏造两份重叠发言 |
| 公开原始四人样本→产品导入混音→实际按钮 | 自动人数为5而应4，**FAIL**；已知人数4模式和切会/取消/人工稿保留链路通过 |

PowerShell最初评分使用整数重载导致覆盖小数被截断；旧评分与模型raw保留，从同一raw另存 `scores-corrected.json`，门槛没有降低。人物分组、检测覆盖、显示待确认和字幕错归是不同指标，详见78。

因此 M5 实现和本机功能交付可用，**不声明 P5 质量闸门全部通过**。P5-01/P5-04保留PARTIAL，具体失败单列，未执行项目继续NOT_RUN。显示改名与人工纠错是实交付，可用于核对和修正模型结果；没有推进M6。

## 最终功能与便携核验

Release `build-final-02.log` 零警告零错误；331项默认回归见 `.local/M5/regression-final-02/results.json`，不含设备采集。真实GUI的三语12个版本、跨版本同ID改名下拉正确、已保存analysis应用到改字新稿且不启动模型、便携路径安全通过，见 `ui-063910394/report.json`。真实Whisper按钮漏传混音hash/policy和搜索版本名称滞留均已被复审发现、修复并实际复测。

完整候选包10项WPF（M5、Qwen、无模型导出、暂停、布局、会议ASR、预览、验证/回听等）均PASS，见 `.local/M5/portable-ui-candidate2/summary.json`；其中一项明确的旧合成CLI夹具使用项目SDK，其余使用无效DOTNET_ROOT验证自包含。真正干净Windows不据此升级。真实包内sherpa CPU及Whisper按钮→已保存analysis复用见 `.local/M5/real-ui-064000456/report.json`，功能链通过，自动人数5≠4质量失败仍在该报告独立列出。

首轮Whisper词界读取未处理跨解码窗口的已有合句，真实22词界为零的FAIL保留在`mapping-real-02`，修后`mapping-real-03`通过；不是靠接受猜测词界绕过。首个候选包封装时遇到M5版本标签与旧M4标签断言不同步，`portable-ui-candidate`旧M3H烟测FAIL保留，完整重建的Candidate2十项全通过。其它探针前置路径/前缀断言错误也保留原日志，不混同模型质量失败。

最终ZIP、中文解压逐文件hash、自带App/Host、包内M5及真实按钮复测结果在封包后追加至STATUS和同名`.package.json`。包内说明为封包前快照；模型及声音不上传、不含测试音频或用户Data。

### 封包后结果（2026-10-04）

交付 `artifacts/ProjectEcho-win-x64-20261004-M5-Final.zip`，358372664B，SHA-256 `c65abe6b548f5f4d32df5789b538bb799b485afd7cd10da76054fce296a3de92`。772项清单+自身manifest共773文件，中文解压路径`artifacts/E75/同场说话人`逐文件核验通过；manifest SHA `c23ecba5f83d657b9ddc41fa04a7e347408fa86dea1a756b7bb5f79b0ee6f2f4`。

解压后自带App/Host正常启动及无采集IPC、M5十二版本WPF、真实sherpa CPU及Whisper按钮→已保存analysis均通过。真实自动人数仍5≠4，已知4人通过，质量FAIL不变。证据：`.local/M5/final-zip-verification.json`、`final-tested-components.json`、`ui-064933419/report.json`、`real-ui-064939060/report.json`、`.local/diagnostics/portable-20261004-144930/portable-smoke.json`。6个App/Host Core/Capture/Export DLL均与331回归所测文件相同；最终Core SHA `a35f11accb5487c8f469ec4622bec414ed5b0af1ab0a6f05e7b88357ae6358a5` 与三小时最终重映射所测相同。

M5实现与本机便携交付完成；P5质量闸门仍PARTIAL，未启动M6。

## 独立未测范围

自然三小时会议人工标注质量、真实电话与远场/回声/相似音色全面质量、其他 CPU/Windows 和真正干净 Windows 未执行前保持 NOT_RUN。公开多说话人片段与重复压力轨只能证明各自已测范围，不推广为所有会议准确率。
