# 79｜M5 末端说话人区间拒绝修复

2026-10-04。用户报告本地录音说话人分析失败；本次只处理 M5 已有功能，不启动下一阶段。未录音、启麦、播放、入会或上传。

## 已确认原因

该例输入为1,182,893帧/16kHz，即73.9308125秒；项目毫秒时间线上界为73,931ms。三次原生进程均正常退出，10条输出区间起点有序、长度为正；最后一条结束于73,960ms，超过上界29ms，原严格解析器因此拒绝整场。不是录音尾声丢失或段数超限。

锁定sherpa-onnx 1.13.8/11afbd00的pyannote实现对最后不完整窗口保留`floor(samples/270)+1`个标签，再以感受野中心表示时间。锁定ONNX metadata为sample_rate=16000、window_size=160000、receptive_field_size=991、receptive_field_shift=270，其SHA-256为`220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079`。

本例最终网格为`(4381×270+991/2)/16000 = 73.96034375s`，原生三位小数输出正是73.960。源码依据见[锁定管线](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/offline-speaker-diarization-pyannote-impl.h)的`ComputeSpeakerCount`和`ComputeResult`，本地副本已保存于M5来源证据。CLI输出前明确按起点排序，重叠本身不是本例原因。

## 修复约束

- 普通`ParseOutput`仍严格拒绝反序、零长、异常格式、超数量和真正越界。
- 新合同`SherpaDiarizationTimestamps`仅对已验证模型SHA、已验证原生程序、>10秒且未额外补零、最后1秒步长不完整的输入启用。
- 只接受由精确帧数和原生float/F3公式枚举的末端越界网格值，不能用任意30ms/50ms容差替代。可能有两个合法格点或多个重叠人物，分别处理；完全位于真实EOF之后的区间仍拒绝。
- 只把派生说话人区间终点映射到原音频毫秒上界。原PCM帧、混音、文字、区间起点、聚类身份和新旧timelinePolicy均不更改，没有裁剪音频。
- 原stdout保留；尝试记录新增policy、原帧数、sourceEndMs和逐条rawEndMs/normalizedEndMs。分析警告继承到人物字幕版本，完成提示明确显示归一化；失败Log也包含具体原因及区间数值。

## 实测

Windows11 25H2 build26200.9457/x64、SDK10.0.401/runtime10.0.12，CPU原生sherpa路线。Release零警告零错误；**339项默认回归PASS**，包含8组新增边界回归：精确本例、双网格/重叠、错hash、伪造近似端点、真实EOF后起点、10/11秒与补零隔离、三小时float、数量及取消。证据`.local/M5IntervalFix/build-02.log`与`regression-01/results.json`。

`.local/M5IntervalFix/user-validation-01/report.json`在独立副本上真实运行模型通过：10个音频区间、2个声音组；20条原字幕映为23条人物字幕，全部文字恰好保留；仅第10条终点73960→73931ms。37个原有文件逐一hash不变，录音policy仍`pause-excluded-v1`。报告不包含发言正文；用户声音和转写未进入Git或便携包。

随后在原会议另存新的分析和人物版本，保留三次失败尝试、原稿和所有原文件；实际恢复结果、包内复测及最终ZIP以STATUS和`.local/M5IntervalFix/`收尾证据为准。旧M5自动人数、叠声质量失败及自然会议/其他硬件/干净Windows未测不升级。

### 封包后确认

原会另存恢复通过，`.local/M5IntervalFix/user-recovered-01/report.json`记录原37文件hash保持及新分析/新人物版；包内程序在独立副本再次复测通过，`bundled-case-01/report.json`记录恢复后的42已有文件保持、73960→73931ms、20→23段且全文不丢不重。用户音频/转写未放入包或可跟踪源码。

交付`artifacts/ProjectEcho-win-x64-20261004-M5-IntervalFix.zip`，358381466B，SHA-256 `a00467442f877fb0f0d776e24413081514e9b7a5ec457ee8c101b0dad3f9ab47`。773清单+manifest共774文件于中文路径`artifacts/E79/尾端修复`解压逐一核验；manifest SHA `defebe90d22887dad29faae5124607e76b5af67ab5d190a33f160db9f487e1f8`。

自带App/Host无采集启动/IPC与10项实际WPF通过，证据`.local/M5/interval-fix-zip.json`、`interval-fix-ui/summary.json`、`.local/diagnostics/portable-20261004-155744/portable-smoke.json`。最终Core `aba882544aa77eb5b821e5308bcc25f6f2a5fdbb1e6a39a1b879c5c9acb3131e`与339回归、用户例恢复、包内探针一致。原M5-Final不改包，干净Windows及旧M5质量限制不升级。

## 使用

使用修复包，在设置中选择原来的Data目录，再打开原会议的最新“同场说话人”版本。修复只增加新版本，不要求重新录音或重做ASR。若对其它会议重新分析，符合该模型末网格规则时会显示调整提示；真正异常仍明确失败。
