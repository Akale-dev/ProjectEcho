2026-10-15 **Git 发布授权**：用户明确要求提交并推送本次更新、上传已核验便携包至 GitHub Release。源码分支 `codex/m3p-lite`，版本标签 `m3p-lite-20261015`；M5 分支仅保存交接文档，本次发布不代表 M5 功能同步验收。私人数据与本地测试原始证据不上传。

2026-10-15 **M3P Lite 已交付，待用户验收后同步 M5**：字幕时间异常浅红高亮，导出按钮可点击并提示/选择首错，新增“寻找错误”循环定位；搜索隐藏错误和草稿校订同步处理。384/0 回归、674 文件 ZIP、四进程启动通过；最终 WPF 18 通过/1 旧录音页布局失败（M3O 同环境同样失败），新增 M3P 检查通过，剪贴板失败后同包复验通过。Pro 452 文件和旧发行 140 文件哈希保持。M3O 前像、M3P 源码、增量补丁与同步清单已保留。真实会议/麦克风、物理输入/原生对话框/OS DPI、目标机和 clean Windows 未验；没有新采集、上传、下载、推送或关机。 详见 docs/94_M3P_SUBTITLE_TIMING_ERRORS.md；同步清单在父目录 docs/95_M3P_LITE_PENDING_SYNC.md。

2026-10-06 **M3O Lite 已交付，M5E Pro 同步已完成**：四个秒数数字按字形轮廓居中，两栏绿底三角从当前位置播放，暂停双竖线保持并可继续。378/0 回归、18/18 最终包 WPF、四进程启动和 673 文件中文 ZIP 核验通过。用户已明确要求立即同步；M3K–M3O 已合入实际 M5Dfix，发布 M5E，Qwen 全文保全修复保留。旧包与数据保持；真实采集/麦克风、目标机、物理输入/DPI、clean Windows 未验。详情见 docs/93_M3O_PLAYBACK_BUTTON_POLISH.md。

# Development status

2026-10-06 **M3N Lite已交付，待用户验收后一起同步M5 Pro**：修复mic低底噪/DC不断重置无声计时；录音/暂停中可启用、取消自动结束及改时长，Host确认后生效并留证。回听新增−10/−5/+5/+10秒矢量按钮、上下切字幕及左右5秒快捷键，跳转保留暂停/播放和倍速；两栏从头播放改为左竖线+右三角，焦点/按下不裁切。378/0回归、18/18包内WPF、四进程启动及672文件中文ZIP通过。1681个Pro源码/旧包文件不变，32文件增量补丁及同步记录已保存。见[交付与验收记录](docs/92_M3N_MIC_SILENCE_AND_PLAYBACK_CONTROLS.md)、[完整便携包](../artifacts/ProjectEcho-win-x64-20261006-M3N-Lite-Final.zip)。真实mic/会议、目标机、物理键鼠/DPI和clean Windows待验；无新采集、下载、上传或关机。下方保留历史记录。

2026-10-06 **M3M Lite已交付，待用户验收后一起同步M5 Pro**：移除手动预检，开始录音自动检查保留；原按钮位置增加自动结束勾选，默认关闭/5分钟，可选1、5、10分钟，所选根进程退出或全部已启用来源无声音到期后正常停止并保存。mic声音重置，暂停不计；缺包不冒充静音，尾包与结束原因持久保留。362/0回归、16项包内WPF覆盖（含剪贴板权限复验）、四进程启动及671文件中文ZIP核验通过。1680个Pro源码/旧包文件hash保持，23文件增量补丁和同步清单已留存。见[交付与验收记录](docs/91_M3M_AUTOMATIC_RECORDING_END.md)、[完整便携包](../artifacts/ProjectEcho-win-x64-20261006-M3M-Lite-Final.zip)。真实会议/麦克风/目标机及旧闸门仍待验；本次未新增采集、下载、上传或关机。下方保留历史记录。

2026-10-06 **M3L Lite已交付**：字幕校订删除圈定说明及底部状态区，列表空间增大（默认261DIP）；搜索框单行32DIP；原宽时间框显示两位小数，未编辑毫秒保留；启动后台自动刷新记忆数据目录会议库，不自动打开/播放。343项回归覆盖（340首过+3相同DLL重试）、15项包内WPF、四进程启动及670文件中文ZIP验证通过。详见[90](docs/90_M3L_COMPACT_REVIEW_AND_STARTUP_LIBRARY.md)、[完整M3L ZIP](../artifacts/ProjectEcho-win-x64-20261006-M3L-Lite-Final.zip)。旧包和Pro保留，待本次验收后同步，目标机及旧M1/M2闸门不升级。

2026-10-06 **M3K Lite修复包已交付**：时间框保留原宽118DIP，固定冒号/小数点；数据目录选择持久保存，独立进程重启沿用，不可用不静默换目录；录音状态121→151DIP（约+25%）。同会时长41:15.325核查正常，输入41:09:01被按41小时解释，与旧模型末端实际超914.875ms不是同根因。343回归、14项包内WPF、四进程目录验证和669文件ZIP通过，目标机/DPI等旧缺口保留。首次升级需选一次旧版没有保存的原数据目录。见[89](docs/89_M3K_FIXED_TIME_AND_DATA_DIRECTORY.md)及[最新修复ZIP](../artifacts/ProjectEcho-win-x64-20261006-M3K-Lite-TimeFix-Final.zip)。旧包/原件/Pro保留，本轮验收后再同步Pro。

2026-10-06 **M3K Lite已交付，待用户验收**：取消录音页合成浏览器入口、来源卡约缩短32%、会议库精简；字幕起止时间可改，多段内存草稿一次保存一个新修订、关闭提醒；Whisper通用分句优先原标点/词间token间隙。330回归、12项实际包内WPF、668文件中文ZIP验证通过。base同文186→196段，26635字文字符/25标点保全，保守非停顿边界125→57；同音频turbo原标点仅2，不宣称模型排名。详见[87](docs/87_M3K_LITE_EDITING_AND_SEGMENTATION.md)、[研究88](docs/88_M3K_WHISPER_SEGMENTATION_STUDY.md)。M3J旧包和Pro保留，本次M3K待用户验收后按既有授权同步；旧M1/M2/目标机缺口保持。
**M3K Lite下载：** [完整便携ZIP](../artifacts/ProjectEcho-win-x64-20261006-M3K-Lite-Final.zip)，同名目录ProjectEcho.exe。下方为旧版历史，早先“Pro未同步”仅指当时，不撤回后来M5D已完成的历史同步。

**M3J Lite：** [完整便携ZIP](../artifacts/ProjectEcho-win-x64-20261005-M3J-Lite-Final.zip)；源码2a7f2e5，ZIP SHA-256 60dc2986a572e128610bbaee835855736dece14440cb7abf27f9b7ac9af22ae2。[详细修复与验证](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M5C仍未同步。

2026-10-05 **M3J Lite已交付，待目标机验收**：用户已确认M3I公司机录音故障不再复现及后处理提速；新ASR模型末端914ms越界。M3J按要求覆盖逐段时间异常，并左移标识、显示会议时长、增加录音页录后处理卡。最终包319回归、八项WPF及实际用户整场Vulkan识别通过；恢复旧184段/1待校对，新稿186全定时，推理103.369秒。见[84](docs/84_M3J_ASR_EOF_AND_RECORDING_UI_FIX.md)。M5C仍未同步。


**M3I Lite下载：** [完整便携ZIP](../artifacts/ProjectEcho-win-x64-20261005-M3I-Lite-Final.zip)；入口同名目录下ProjectEcho.exe。源提交98d3420，ZIP SHA-256 74af54360a02473d6fde2cc5bd0229af3ce6e1bc782395c278dccd896d2788fc。M5C未同步；目标公司电脑验收待完成。
Updated: 2026-10-05

2026-10-05 **M3I Lite已交付，等待公司电脑验收**：大包、Whisper零时长保全文、正常说明精简、路径校验提速和真实阶段进度。最终包307回归、七项WPF及CPU/Vulkan公开识别通过；同一小时原轨439.393→29.173秒且WAV hash相同。主目录M5C产品代码保持，待用户验收再同步；见[82](docs/82_M3I_LITE_CAPTURE_AND_ASR_FIX.md)、[83](docs/83_M3I_TO_M5C_SYNC_CHECKLIST.md)。


## 目录维护：旧版本与测试数据已清理

2026-10-03 按用户明确授权，删除旧包/重复副本、编译缓存及不再需要的测试录音和会议数据：98,958 个文件，磁盘实际增加约 49 GiB 可用空间。保留最新 M3H-Final 目录与 ZIP、识别设置、源码、开发依赖和 73 份自动测试结果；最新 661 个发布文件及保留文件的完整性核验通过。清理后程序重新生成的索引正常保留。历史“原件/旧包保留”状态由本条更新，历史验收结论不变；本轮未运行产品测试。详见 [65 清理记录](docs/65_WORKSPACE_CLEANUP.md)，明细 `.local/Cleanup-20261003/`。

## 最新：M3H会议识别设置、图片按钮与实时电平

**已实现：**CPU/自动/Vulkan（旧CUDA明确不支持）；提示词自动跟随当前会议保存，不进默认、不提醒保存；本场上次识别选项与新会默认分开，新会创建时快照，失败/取消与后台会话隔离。透明成对PNG/固定中心按压、短硬件下拉、去重复标题、刷新主按钮、录音状态/新会计数0。远端选来源即系统峰值预览，mic只在选设备并点击开启后临时预览、不写文件；Off/Start/关闭等待实际释放。连续dB电平按渲染帧快速上升/平滑回落，真实原峰值与录音帧数不改。

**已测试：**Windows11 25H2 26200.9457/x64，SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release零警告，**263默认回归PASS**。真实远端process/明确端点合成tone响应及隔离/PID失败保护通过；实际WPF三语双主题48次切换中心0DIP、会议提示词/默认/坏旧配置隔离、fake preview起停取消与逐帧平滑通过。初轮1DIP布局漂移已修；旧开发启动默认WPF绘制原生崩溃经A/B采用进程内SoftwareOnly规避，未定位精确底层模块，不改系统/驱动，不影响Whisper Vulkan。软件绘制下最终六项WPF及旧混音/回听/Log回归均PASS，旧失败保留，见[63](docs/63_M3H_MEETING_SETTINGS_AND_LEVEL_PREVIEW.md)。

**最终交付：**`artifacts/ProjectEcho-win-x64-20261003-M3H-Final.zip`，**262605661字节**，SHA-256 `a72c46b46950aefbbfeb542db5bec85c1c5a79a7e41061bdec2cb209f7b40664`。661项清单+manifest解压hash、自带Host runtime/IPC、新/旧六项包内WPF、真实包内Vulkan0公开JFK（3939ms）及字幕往返通过；三生产DLL与263测试相同。证据`.local/M3H/zip-verification.json`、`bundled-wpf-final.json`、`tested-components.json`及`vulkan/wpf-final-package/summary.json`。本轮授权全部收尾后正常关机，执行另记`shutdown-*.json`，不强制关闭未保存程序。

**未测试/限制：**真实mic预览/权限/驱动/拔插未主动执行；合成生命周期与PCM清零不冒充真机。真实共享多进程session分支、独占模式、物理键鼠/跨屏DPI、目标旧Windows/自然会议/干净Windows/长期同步和3小时识别仍NOT_RUN。无真实录音/mic/私人音频访问、上传、全局修改或推送。

## 最新：M3G Vulkan联动、百分秒时间与录音UI

**已实现：**随包CPU/Vulkan选项联动真实程序路径、旧错配配置内存修正、外部自配不替换、GPU实际证据检查保留。字幕/回放/控制ACK生效时间显示到0.01秒，原始ms/us不改写。来源改名为从特定应用/从所有应用录音；删除mic勾选与10秒试录，透明矢量mic图标移至设备下拉下方，默认off、点击请求、ACK后状态、跨会清同意。关闭时预选设备不传给Host，保留首次启用前用户显式改选。

**已测试：**Windows11 25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release零警告、**241自动回归PASS**，三语实际WPF图标/默认隐私/来源锁定与模拟ACK通过。M3F随包Vulkan CLI使用真实RX6900XT/Vulkan0成功；同包CPU CLI配GPU参数报告no GPU found，证明配置错配路径。用户原错误未含CLI路径，不武断归因成二进制损坏。证据`.local/M3G/`和`.local/TestResults/M3G/default-01/results.json`；报告[62](docs/62_M3G_VULKAN_TIMING_AND_MIC_UI.md)。

**最终交付/实测：**`artifacts/ProjectEcho-win-x64-20261002-M3G-Final.zip`，260835657字节，SHA-256 `65f95a62466eb6e67c379e87fe70dcff8cc4a521e5f5929a53967df5aedfade9`。659项清单+manifest实际解压hash、Host自带运行时/IPC、包内三语mic与CPU/Vulkan切换/保存/旧配置、百分秒实际静音定位、录音验证/历史回听排队通过。最终包真实WPF/base识别公开JFK报告Vulkan0、4258ms，原件与配置hash不变；三生产DLL与241回归相同。证据`.local/M3G/zip-verification-final.json`、`bundled-wpf-final.json`、`tested-components.json`、`vulkan/wpf-final-package/summary.json`。首轮测试夹具路径比较失败已修复，原包移至.local/M3G/preliminary-package保留；原生WPF仅差1ms定位可能停原位仍保留，10ms步进实际通过，不伪造时钟。

**未测/限制：**新增小数显示不保证模型声学边界10ms准确；自然会议、3小时识别、其他GPU/CUDA、目标机最终复测、干净Windows、真实mic及原M1/M2平台/长期声学同步未验。本轮无录音/mic/入会/上传/全局修改/推送。

## 最新：M3F 字段提示、回听恢复、单实例与按需压缩

**已实现：**删除录音首行说明，SVG矢量Logo与名称/Ver.M3F同行；录音和本地识别的缺字段/非法配置显示红框，纠正清除。缺来源在进入录音状态前拦截，已确认Host退出的错误不再阻止历史回听，未确认停采仍禁播。WAV回听避免无关M4A全解码、重复点击不重跑校验、等待状态可见且显式播放可排队，切会/停止/录音/关闭清请求。M3F同用户同Windows会话单实例，再开恢复原窗口。用户追加采纳：默认录后只生成完整WAV，M4A/MP3按需导出；会议通常≤3小时，暂不做超大文件或自动分段适配，保留限制和提示。

**已测试：**Windows11 25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0，零警告构建、**235自动回归PASS**；实际WPF三语/红框/矢量100%-200%渲染、失败后两场已有字幕会议静音回听、排队10项安全边界和原件hash通过。单实例六项真实多进程/WPF通过，后台自动启动受OS前台规则限制，但最小化恢复可见已验。首轮脚本null与UI过早布局断言失败保留并修复。见[61](docs/61_M3F_VALIDATION_REPLAY_AND_SINGLE_INSTANCE.md)、`.local/M3F/`及`.local/TestResults/M3F/default-01/results.json`。

**交付核验：** `artifacts/ProjectEcho-win-x64-20261002-M3F.zip`，260,806,537字节；SHA-256 `72e387b146f34d65148addcba11a9130fb10a319286345cf02d5f33c0ab5af82`。658文件实际解压hash、自带Host/WPF、新UI/录音失败恢复/回听排队及Log回归、包内6项隔离单实例和普通MainWindow无参数双启动均PASS；所有自建程序正常退出。三生产DLL与235回归hash一致。证据`.local/M3F/zip-verification.json`、`normal-startup.json`、`single-instance-bundled/report.json`。

**未测/范围：**目标新电脑的启动等待实际耗时、物理键鼠/真实跨屏DPI、完整3小时自然会议识别、真正干净Windows及原M1/M2现场/长期双轨同步仍未验。旧M3E需要关闭后再启M3F（旧版没有单实例协议）。无新采集/mic/私人音频访问/上传/全局设置修改/推送。原件和已有压缩副本不删除。

## 最新：M3E 界面、会议 Log 与 Whisper 尾端修复

**已实现：**左上 Ver. M3E、删左下提示/语言置底、转写与字幕命名、端点开始即确认（仍须显式选设备且不自动回退）、识别硬件同行/主按钮、标题跨页保持；每会议时间戳 Log、旧历史读取/错误记录/脱敏 TXT 导出，基本信息不再显示全局最新记录。记录绑定任务原会议，包括回放异步错误；坏行与日志故障隔离。Whisper 仅允许末句≤250ms小幅越界修正，原始SRT/JSON/文字保留，其他非法仍拒绝并显示数值。

**已测试：**Windows11 25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0，Release零警告零错误，**233默认回归PASS**；实际WPF三语最小布局/草稿/Log A-B异步归属/重开/旧错误/导出脱敏及原件hash通过。真实随包CPU/base对既有SAPI中英混合三样本通过管线；中文再次复现39840>39715ms并成功记录125ms修正。首轮229PASS/3FAIL与构建锁冲突保留，修复后通过。证据`.local/TestResults/M3E/default-final/results.json`、`.local/M3E/`和`.local/diagnostics/m3e-asr-sapi-20261002-01/asr-report.json`，报告[60](docs/60_M3E_LOG_UI_AND_ASR_FIX.md)。**最终交付：** `artifacts/ProjectEcho-win-x64-20261002-M3E-Final.zip`，260,783,512字节；SHA-256 `5a3847b0f57215fcc8b39457e87065761d8604a4cc2fe0c0ed3aeb35d770d939`。657文件ZIP实际解压hash/自带runtime/Host IPC、新UI/Log及包内CPU-base公开JFK实际WPF识别/字幕往返PASS；三生产DLL与233回归hash相同。见`.local/M3E/zip-verification.json`及`tested-components.json`。

**用户实测更新：**用户确认M3D-AudioFix2在新电脑回放正确、音质恢复；此前目标为19043.2364/Jabra，本轮未重新检测系统。这只升级混音/回听的用户报告范围，带mic线程交接/完整性/两小时同步不外推。

**未测：**用户32.831秒原识别输出未取得，目标机M3E重试尚未执行；不能认定该样本已经证实同根因。干净Windows、其他GPU/CUDA、自然金融中文长会、既有M1/M2现场闸门继续保留。无录音/mic/私人音频访问/上传/全局修改/推送。


## 最新：AudioFix2端点混音/音质与会议处理结果修复

**已实现：**按当前会议显示、保存和恢复处理结果；后台A不会覆盖B，旧v4历史可看但不冒充新版音频。修复保存合同允许的一输入帧内负量化偏差被混音拒绝；设备连续段用有界平稳映射和跨包邻样本插值，避免把短期QPC抖动转为逐包变速；来源改unity增益，浮点真峰值预检后才必要整体衰减，回听音量明确为1。混音版本v5，旧原轨/派生保留，可主动重生成。mic初始化拆为未启动客户端准备+原采集线程服务绑定，remote不再等其同步协商；stop/off/pause及系统缺口自动暂停取消待开mic，迟到客户端不启动。

**已测试：**Windows11 25H2 **26200.9457/x64**，SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release **0警告0错误，221默认回归PASS**。旧Win10Fix二进制7项新音质对照均预期失败，新版正弦/扫频约84dB参考SNR、强双源约93.4dB且真峰值−2.01dBTP；不是设备噪声/听感验收。新增慢mic准备/取消/线程归属、量化重叠/跨块/边界通过。实际WPF会议结果/旧v4元数据/异步归属通过；新ZIP656清单文件+manifest逐一核验，实际解压后WPF默认音量1与隐私启动、Host自带运行时/IPC及包内会议结果流程PASS。测试过的Core/Capture/Export与新包hash一致。

**交付：**`artifacts/ProjectEcho-win-x64-20261002-M3D-AudioFix2.zip`，**260,754,924字节**，SHA-256 `3d5b97c807585765f928463115086cadd3838e1de032632dc9aabe31296aeb24`。CPU/Vulkan/base/默认配置与上个完整包hash一致，无私人Data。报告[59](docs/59_M3D_ENDPOINT_MIX_AND_HISTORY_FIX.md)，证据`.local/M3D-EndpointFollowup/zip-verification.json`、`bundled-history-ui.json`及`.local/TestResults/M3D-EndpointFollowup/default-final/results.json`。

**未测/失败保留：**用户此次均在新机Win10Pro21H1 **19043.2364/JabraLINK860**测试，确认remote原轨正常、派生较差；旧Win10Fix有混音重叠失败及带警告成功，不能记为完整录音通过。**AudioFix2在该目标机的新录音/原生跨MTA交接/实际听感仍NOT_RUN**。后两份日志属于已生成WAV/M4A但有时钟/完整性警告，警告不删除；真实缺口不恢复为音频。旧两小时/平台隔离/干净Windows/声学同步闸门不升级。本轮无采集、mic、回放声音、用户音频访问、上传、设备/全局设置修改或推送。

## 最新：Win10 19043 进程兼容尝试与端点时钟候选修复

**已实现：**用户确认目标机实际为Windows 10 Pro 21H1 **19043.2364**、无兼容模式，并要求进程录音支持此版本。旧20348门槛正确按原策略拒绝；现依据锁定NAudio3.1.0和OBS官方实现，增加19041–20347显式“兼容尝试、待实测”路径，低于19041仍拒绝，失败保留HRESULT且不自动转端点。端点已有范围确认仍保留。remote仅在有效连续设备帧支持下采用与mic相同的有界QPC抖动规则，writer/恢复/混音一致校验；原PCM/时钟/Stop/Pause/MicOff不改，QPC-only不放宽；失败附相对数值诊断。混音v4保留设备证明的真实缺口，旧原件及派生文件不覆盖。

**已测试：**开发机Windows11 25H2 **26200.9457/x64**，SDK10.0.401/runtime10.0.12/NAudio3.1.0。最终Release **0警告0错误，201默认回归PASS**（9组新端点时钟/跨块/Stop/篡改与3组版本/模拟激活测试）；独立代码复审无阻塞。新ZIP **655清单文件+manifest**逐文件hash验证，实际解压至新目录后WPF隐私启动、自带Host运行时/IPC通过；CPU/Vulkan/base/默认配置与上个完整包hash一致。没有采集、mic、入会、上传、全局修改或推送。

**交付：**`artifacts/ProjectEcho-win-x64-20261002-M3D-Win10Fix.zip`，**260,720,845字节**，仍附CPU/Vulkan/base且无私人Data。SHA-256 `3702c260029c2530de7c1d1ea745b301323c1db2c5260ab883a250f7b50104af`；原包保留。见[58](docs/58_M3D_ENDPOINT_RECORDING_FIX.md)与`.local/M3D-EndpointFix/zip-verification.json`、`.local/TestResults/M3D-EndpointFix/default-final/results.json`。

**未测/失败保留：**目标机旧版端点实录失败为用户报告，只有几KB `.wav.partial`，没有取得失败原包，不能确认本修复覆盖其实际抖动幅度或断续。**修复后19043进程激活/范围/完整保存及Jabra端点录音仍NOT_RUN**；旧失败目录恢复、M0严格时钟、长录音/声学同步、干净Windows闭环均不升级。首轮197PASS/1FAIL（旧夹具把合法连续device小抖动当非法）与中间CA1416警告日志保留，最终已修正通过。下一步由用户主动做普通录音30–60秒；若失败仅反馈新错误数值，无需音频或Data上传。

## M3D最终追加交付：RX6900XT Vulkan实测通过，附base的完整ZIP已修复中文路径并复测

**已实现/部署：**保留全部M3D功能及教程；新增真正可搬家的相对引擎/模型配置。项目内从官方b5130源码静态构建独立CPU/Vulkan EXE，免VC/GCC/.NET SDK预装；模型随包base。Vulkan部署在`H:\AI\Whisper\whisper-b5130-vulkan-ProjectEcho-20261002`，原CPU程序、模型及原配置不改。最终包的本机Data配置已选择此Vulkan引擎和用户原large-v3；**ZIP本身仍默认CPU+base且不含本机Data**。

**已测试：**Windows11 25H2 26200.9457/x64，RX6900XT、AMD驱动32.0.21043.10005、Vulkan loader1.4.309.0；C# Release 0警告0错误、**189回归PASS**、开发与最终包各七WPF流程PASS。最终UTF-8引擎：真实Vulkan base/large-v3、识别中取消/进程退出、取消后重试、prompt、原件/版本保留全部PASS。真实本机安装目录经最终包UI使用large-v3，**Vulkan0、9.699秒**完成公开11秒JFK并通过导出往返，设置未改。它是固定样本结果，不作金融质量或通用性能承诺。

**发现并修复：**首个ZIP实际在中文路径识别失败（窄argv把中文变成问号）。给CLI加入进程级UTF-8/longPathAware/asInvoker manifest，不改系统locale；重编两引擎、更新安装和ZIP。新ZIP实际解压至含中文的新目录、不同cwd、无有效外部DOTNET_ROOT；**654文件逐一核验，CPU及Vulkan真实M3D UI识别均PASS**，所有引擎/模型路径来自解压目录。旧失败ZIP已移至`.local/M3D-Vulkan/rejected-package`隔离，旧日志保留。

**最终ZIP：**`artifacts/ProjectEcho-win-x64-20261002-M3D-Complete-Final.zip`，**260,719,760字节（约261 MB）**，解压内容654文件/452,057,087字节。ZIP SHA-256 **`532a24ccd0942e4abb908a46b966a82384508cf37f0db70301d26a0947902737`**；清单SHA **`137da2f07954fef1716a52ba5fca352686979f4d42c8c14f7733f79f02e45027`**。含应用/Host运行时、SQLite、独立CPU/Vulkan、base、16项新增ASR许可与来源。收尾更正旧README说明与报告链接后重新压缩，所有非文档文件与实际测试ZIP哈希相同，最终ZIP逐文件解压流hash再次通过（`documentation-repack.json`）。配套`.zip.sha256`及`.package.json`可验证，程序入口为同名文件夹内`ProjectEcho.exe`。

**未测/限制：**真正另一台干净Windows仍NOT_RUN；本机依赖闭包/无外部runtime/中文目录搬家通过不能替代它。Vulkan需要目标机兼容厂商驱动；CUDA、其他GPU、专业金融/中文混合长会质量仍未验。通用CPU引擎以基础x64兼容优先，2线程大模型可能慢，可调线程或用Vulkan。上游C++存在已记录编译警告，未冒称引擎零警告。M1/M2旧硬件、两小时、声学同步、深路径限制不变。无采集/mic/会议/上传/全局配置改动/推送；用户授权完成后正常关机，不强制关闭未保存应用。

报告[57](docs/57_M3D_VULKAN_AND_COMPLETE_PACKAGE.md)，证据`.local/M3D-Vulkan/gpu-utf8-final/report.json`、`cpu-static-final/report.json`、`zip-relocation.json`、`local-installed-wpf.json`、`wpf-bundle-final.json`、`.local/TestResults/M3D-Vulkan/default-final/results.json`、`.local/diagnostics/portable-20261002-001855/portable-smoke.json`。状态`COMPLETE_PACKAGE_DELIVERED / AMD_VULKAN_AND_RELOCATION_PASS / CLEAN_WINDOWS_PENDING`。下面M3D的CPU-only条目为追加部署之前的历史。

## M3D：本地识别硬件/提示词/多版本与界面、零基础配置教程

**已实现：**用户10项M3D及追加教程按钮：CPU/Auto/Vulkan/CUDA、线程/引擎GPU编号、initial prompt、本地模型SHA预期值比对移除；删三处页面大字/提示栏；侧栏录音状态和对应会议“识别中…”闪烁/跳转；紧凑边框EN/中；同会重复点击及普通录音期间纯会库导航；模型/日期/类型版本选择、旧版不覆盖、会内未保存草稿保持。CLI/模型旁分别有“如何配置”，三语新手教学、正确CPU/CUDA包和Vulkan限制、完整解压/选文件/模型下载与排错、显式官方链接。原音频/原稿完整性与停采/禁播约束保留。

**已测试：**Windows11 25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0，Release **0警告0错误、186组默认回归PASS**。开发版七项WPF、三语设置/教程、录音状态与导航/回放锁定、版本/未存草稿/旧版导出/磁盘重开及最小窗口均通过；自有WPF渲染图已目视检查。真实已有whisper.cpp1.9.4/b5130在11秒公开JFK文件运行base+金融prompt **2.294秒**、large-v3+prompt **41.570秒**、Auto/base **1.942秒**均成功，四个独立版本含测试子修订保留；显式Vulkan在CPU引擎上**明确失败且不生成成功稿**。实际WPF按钮prompt传递/侧栏任务归属/结束清理及字幕往返也通过。耗时不外推，专业词质量提升未验。

**交付：**`artifacts/ProjectEcho-win-x64-20261001-M3D/ProjectEcho.exe`，完整win-x64自包含631文件，清单SHA-256 `9dde0f2b0c38bf25b4b4082a335495e917295335a02d9c4e8a78e6199913b4e5`。包完整性、WPF启动、自带Host运行时/无采集IPC通过。包内七流程最终通过；剪贴板占用首轮FAIL和同包重试PASS分别见`.local/M3D/portable-wpf.json`及`portable-transcript-retry.json`。保持完整目录；已有模型配置在验包后复制到可变Data/settings，模型/引擎不随包分发。新手指南见[53](docs/53_M3C_USER_GUIDE_ZH.md)，完整实施/失败记录见[56](docs/56_M3D_IMPLEMENTATION.md)。

**未测试/限制：**本机现有引擎仅CPU；没有下载或安装Vulkan/CUDA构建，AMD/GPU真实推理和加速**NOT_RUN**。官方b5130无Windows Vulkan预编译资产，教学已明确说明需相应构建；不是选下拉即可加速。真实金融/中文/混合长会质量、物理鼠标/人工听感、干净Windows、原M1/M2现场闸门未因本轮升级通过。保留旧mic/两小时/深路径限制；没有录音、启mic、入会、computer-use、上传、全局环境更改或远端推送。

**失败及修复：**首次WPF启动器缺项目DOTNET_ROOT未运行；导航初测record可变通知字段影响ListBox身份，改稳定引用class后通过；真实UI新增测试初轮误读JSON字段大小写，生产ASR已成功，修正断言后通过。旧日志全部保留。证据：`.local/M3D/environment.json`、`build-help-final.log`、`.local/TestResults/M3D/default-final/results.json`、`wpf-final.json`、`wpf-help-final.json`、`real-01/report.json`、`real-ui-02.json`、`.local/diagnostics/portable-20261001-233406/portable-smoke.json`。状态`IMPLEMENTED / WINDOWS_FILE_AND_UI_VALIDATED / GPU_HARDWARE_PENDING`。以下为历史记录。

## 最新跟进：导航对齐、回听字幕跟随与large-v3进度显示已修正

**已实现：**01–04及语言按钮实际左对齐，语言标签固定`EN/中`；回听按真实媒体时钟自动选中并显示当前字幕，支持空隙/重叠/暂停定位/版本切换，不触发跳播循环。识别增加独立模型加载阶段、真实计时和活动提示，0%/未知进度使用不确定显示；完整`%`日志立即解析，过期回调与计时器在取消/结束时清理。

**已测试：**Windows11 25H2 26200.9457/x64、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release零错误零警告、**181组默认回归PASS**，实际WPF三语言/字幕跟随和最终包四流程通过。使用已有whisper.cpp1.9.4/b5130、large-v3、auto/2线程跑47秒公开构造样本，真实UI用时105.328秒，引擎/UI均为`[49,100]`；49%等待约36.68秒期间计时/活动持续，原件/配置hash与清理检查通过。无mic、采集、computer-use、模型下载或上传。

**边界与交付：**大模型内部只能按音频窗口提供真实百分比，数值仍可能跳跃；本次改善阶段反馈和日志即时解析，没有用计时器伪造百分比，也没拆片改变上下文。新完整包`artifacts/ProjectEcho-win-x64-20261001-M3C-UiFix/ProjectEcho.exe`，630文件/236,124,844字节，清单`32c39e0ae026e3017ec831884fe410ba0ec6eaa5244c87dabe1ea01054cf193a`。用户原large-v3配置验包后复制至新包Data/settings，模型未打包。首轮测试编译命名冲突和UI固定等待不足的失败记录保留；产品录音代码不变，旧mic/两小时/深路径/现场限制不变。详情与证据见[55](docs/55_UI_FOLLOWUP_AND_LARGE_MODEL_PROGRESS.md)。以下为历史记录。

## 最新跟进：麦克风启动缺口已修复，采集加固后20分钟复测通过

**已实现：**本机16k mic原共享转换路径反复返回124帧首包，切换同设备fresh RAW共享格式后首包/后包均160帧。M0/M1/预检共用协商，保留原QPC/device和Stop/Pause/MicOff边界，记录可选processingMode。另加入采集线程MMCSS Audio/NORMAL和M1预分配缓冲，缩短持有WASAPI包的工作；极短真实mic缺口不再被混音量化容差抹去，混音v3拒绝自动复用旧版本，导出保留实际来源版本。

**已测试：**Release 0错误0警告、**179组默认回归PASS**。RAW修复9次开麦首包与40秒跨块/压缩EOF通过；加固版再次通过短边界、40秒、两组90秒受控CPU负载（16逻辑核/4worker，非全机饱和），以及**1201.166629秒真实mic+自有进程树录音**。两路各41块、0声明缺口，原件hash/磁盘恢复/真实Stop边界通过；完整WAV57,655,999帧、MP3及3分片、18内容窗通过。MMCSS实际注册/优先级/释放成功，mic最大轮询12.0985ms/持包0.0149ms。环境Windows11 25H2 26200.9457/x64、SDK10.0.401/runtime10.0.12/NAudio3.1.0；全部测试接口执行，无computer-use、mic回放/ASR/上传。

**最终包：**`artifacts/ProjectEcho-win-x64-20261001-M3C-MicFix/ProjectEcho.exe`，630文件/236,057,213字节，清单SHA-256 `ecfd7227c57836cfdb1aac40442300f738df47891b14e3750b1e605d6cafa8d7`。完整性、WPF默认启动、自带Host运行时与IPC通过；包内四个相关WPF流程最终通过，实际包组件额外20秒mic边界通过。Capture/Core/Export与20分钟测试组件hash一致；最终Host独立复验。本机Whisper配置在验包后另存可变Data/settings，不含模型或会议。

**失败/未测仍保留：**RAW单独修复的1201秒复测在109–110秒出现两路真实缺口，与构建时间重合但未证明因果；旧两小时FAIL均未改写。新负载两组都通过，不能断言MMCSS是旧断续的唯一根因。最终M0短试录目标/尾音/隔离、mic关闭和退出通过，但严格设备计数审计仍因已知remote QPC-only能力FAIL（1321项），不包装成时钟验收通过。首轮WPF深路径失败保留；同一包/权限缩短路径后两项通过，较深目录下媒体/SQLite兼容仍有限制。其他设备、新版两小时、声学同步、干净Windows及原平台现场闸门未通过本次20分钟复测替代。

报告与证据：[54](docs/54_MICROPHONE_STARTUP_GAP_FIX.md)、`.local/TestResults/MicStartupFix/scheduling-02/results.json`、`.local/MicStartupFix/scheduling-medium-summary.json`。状态`STARTUP_FIXED / WINDOWS_20MIN_AND_BOUNDED_LOAD_PASS / PRIOR_FAILURES_AND_LONG_RUN_LIMITS_RETAINED`。以下M3C结论为历史记录。

## 最新结论：M3C 功能与可自动测试完成，真实 mic 断续和识别质量限制保留

**已实现：**用户要求的12项M3C功能已落地：SQLite会库/FTS5、日期项目长度筛选排序、可恢复确认删除、打开数据目录、平滑回听、真实引擎百分比、紧凑转写页/导入末页/固定字幕底栏、两处全文TXT与句子复制、简中/繁中/英文。原轨/时间索引/不可覆盖转写版本继续为权威文件；2026-10-01用户决定覆盖旧禁SQLite约束。

**已测试：**Windows25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0，Release **0错误0警告，154组默认回归PASS**。实际WPF七流程、最终包五流程均通过；534模板覆盖、三语言不改用户原文、筛选/删除取消与接受/文件任务互斥、固定字幕底栏、约39次/秒有效播放条更新、全文与复制、真实JFK引擎/UI百分比一致。最终包630文件和包内Host/SQLite原生/运行时验证通过。全部通过测试接口执行，未使用computer-use。

**两小时实测已执行，严格连续性FAIL：**固定16k Jabra mic+新建合成进程，香港00:53:01–02:53:02实际 **7201.170118秒**。两轨各241块；原件哈希/磁盘索引/最终Stop边界/482块checkpoint可验证。mic启动有2.2489ms缺口，545.499804秒处设备报告不连续并产生 **7.7478ms / 124帧** 未观察区间；保留`StorageComplete=false`及原FAIL报告，不冒称完整录音通过。远端仍QPC-only，双设备声学同步没有通过声明。

**派生文件与补测：**同一真实两小时有警告原件已生成完整WAV/MP3与5个20MB以内分片；345,656,166帧全时间轴、实际解码EOF、覆盖/重叠/28内容窗/哈希通过，`SourceStorageComplete=false`原样保留。15分钟全零静音（mic关）、控制连接断开停采、32秒自有Host崩溃恢复均通过各自范围，所有测试进程已退出。没有回放/识别mic或上传任何音频。

**模型限制：**固定SAPI合成英文/中文/交替三样本已测试。英文链路与导出通过；中文模型末时间39.840秒超过实际39.715秒被严格拒绝，原输出保留；混合样本漏掉英文并有错句。真实引擎进度正常，但不能据此宣称中文/中英质量通过；不以合成样本代表自然会议准确率。

**最终交付：**`artifacts/ProjectEcho-win-x64-20261001-M3C-Final/ProjectEcho.exe`，保留整个文件夹；**630文件 / 235,996,865字节**，清单SHA-256 `076240d526252c62e571046bc27995a6465fcdef60bdf2265245c5bf42d9af04`。最后App界面翻译修正单独验证，Core/Capture/Export/完整Host/SQLite等测试过的生产组件逐文件复用、哈希相同；见[52](docs/52_M3C_IMPLEMENTATION.md)。本机Whisper配置在验包后另存可变Data/settings，不包含模型/会议/profile。操作指南[53](docs/53_M3C_USER_GUIDE_ZH.md)。基线`9e4df2a`已推送，M3C实现提交`dbf7bf1`及最终验证记录在本轮收尾推送。

**未测试/阻塞：**真实Zoom/Webex mic场景与浏览器隔离、44.1k硬件、物理拔插/锁屏睡眠、读屏/真实多显示器DPI、干净Windows和双设备声学同步仍保留原状态。无管理员/系统策略/设备格式修改。M3C任务完成不等于M2所有现场退出闸门通过。用户授权全部收尾后正常关机，不强制结束其他未保存应用。

证据：`.local/M3C/environment.json`、`build-final-02.log`、`wpf-final-01.json`、`delivery-wpf-final.json`、`final-package-component-reuse.json`、`final-audio-summary.json`；`.local/TestResults/M3C/default-02/results.json`；`.local/diagnostics/portable-20261001-032548`。当前状态 `IMPLEMENTED / AUTOMATED_TESTING_COMPLETED / DOCUMENTED_MIC_AND_ASR_LIMITATIONS`。以下为历史记录。
## 最新结论：M3b 七项反馈已修复，真实 mic 复测待用户主动执行

**已实现：**针对三份 16k mic 失败索引支持的时钟问题，增加连续设备帧证据约束的短时 QPC 偏差处理、磁盘严格复算、完整原包跨块映射及收尾诊断；保留真实关麦/暂停段间隙。支持初始不选 mic 后在录音中明确选择开启，成功后固定设备，预算拒绝不连带中止远端 writer。回听按钮为图标，支持六档倍速和当前版 SRT/VTT 导出；字幕改为标点/token边界分句，校订页自动读取版本，新会议清除旧会库选择。旧音频、失败记录、字幕与人工修订保留。

**已测试：**Windows25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release **0错误0警告，141组默认回归PASS**。包括8组QPC/跨块/边界与篡改检查、首次开麦失败/重试/固定设备/预算保护、5组句级字幕。实际WPF自动读取/未保存编辑保留/新会议清选/隐私回执，静音回听六档与0.5x/2x实际推进、所选字幕版本导出往返通过。现有base识别公开JFK新参数完整一句、取消重试/原件哈希通过。用户报告Zoom **mic关闭**时录音/保存/转写通过，记录为PASS_USER_REPORTED。

**交付：**新完整自包含目录 `artifacts/ProjectEcho-win-x64-20260930-M3b-Fix2`，运行其中 `ProjectEcho.exe` 并保留整个文件夹。旧资料在设置选择旧包Data目录（目前每次启动需重选）；旧字幕需重新识别才使用新分句逻辑。最终发布验证及证据见 [51](docs/51_M3B_FEEDBACK_FIXES.md)。

**发布复验：**617文件完整性、默认WPF隐私启动与包内Host运行时通过；直接运行最终包的真实Whisper、转写/清选、静音倍速回听/字幕导出、导出关窗四流程全部exit0。证据 `.local/diagnostics/m3b-fixes-bundled-workflows-02.json`；不替代真实mic或干净Windows。

**未测试/限制：**本轮没有采集、启麦或入会。历史三次记录没有被拒绝包/异常堆栈，使用其已知相对时钟加明确合成包复现旧故障机制，不能声称原堆栈已取到。**修复后真实16k mic+Zoom/中途首次开启仍NOT_RUN**；44.1k硬件、两小时双设备同步、干净Windows等原闸门不变。字幕时间仍approximate；中文/混合语言/长会议质量未验。状态为`FIX_IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_MIC_RETEST_PENDING`。

证据：`.local/TestResults/M3bFixes/final-02/results.json`、`.local/diagnostics/m3b-mic-fix-diagnosis.json`、`.local/diagnostics/m3b-fixes-environment.json`、`.local/diagnostics/m3b-sentence-real-jfk-20260930/report.json`。以下为历史记录。

## 最新结论：M3b 界面重构、字幕播放器与多规格录音链路已实现

**已实现：**01新会议录音、02左侧日期/标题会议库、03导入录音、左下04诊断与设置；每场会议的基本信息/导出音频/回听/转写与搜索四标签。录音栏只在录音页，播放器只在回听页；支持字幕版本、当前字幕、点击定位、暂停/继续与进度条。历史混音增加源清单/索引指纹，重开校验复用不自动生成重复文件。mic格式协商不再固定48k；ASR实际经通用接口调用默认Whisper，Qwen3-ASR仅预留准备。

**已测试：**Windows25H2 **26200.9457/x64**、SDK10.0.401/runtime10.0.12/NAudio3.1.0；Release构建0错误0警告，**128组默认测试全部通过**。含16/22.05/44.1/48/96k合成原轨边界→磁盘恢复→混音→16kASR完整时间轴、4组ASR接口、6组安全缓存复用。实际WPF字幕点击/暂停/暂停中定位/当前字幕/继续播放、录音状态防回放、会库选择与缓存重开通过。现有whisper.cpp/base经新接口真实识别11秒公开英文样本通过（开发版UI2798ms，不作普遍速度承诺）。无采集、mic或上传；字幕时间仍为近似值。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3b/ProjectEcho.exe`，保留整个文件夹。617个发布文件、231143051字节；发布文件完整性、默认WPF启动、包内Host IPC/运行时已通过。本机Whisper配置在发布校验后加入Data/settings，不把模型、录音或本机配置收入发布清单。中文场景手册为 [49](docs/49_M3B_USER_GUIDE_ZH.md)，包内为`使用说明.zh-CN.md`。旧录音未迁移，新包须在设置选原Data目录才能读取。

**未测试/限制：**真实44.1k设备Initialize/GetBuffer及转换后的实际设备时钟、录制中修改系统格式仍NOT_RUN；本轮没有启麦，不用合成测试冒充设备验收。M2双设备两小时同步、干净Windows、未验/暂缓现场项目保持原状态。中文/混合语言/噪声/长会议ASR质量、真实显示器DPI/高对比/读屏未验。Qwen未部署。M3b状态为`IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_44K1_PENDING`。

**发布包复验：**直接运行新包EXE的文字/历史混音重开、导出关窗、字幕播放器、真实Whisper四流程全部退出0；证据`.local/diagnostics/m3b-bundled-workflows.json`。包内运行时与哈希证据`.local/diagnostics/portable-20260930-171813/portable-smoke.json`，不替代干净Windows检查。

详情：[50实施、失败记录与证据](docs/50_M3B_IMPLEMENTATION_AND_VALIDATION.md)、[48 ASR合同](docs/48_M3B_LOCAL_ASR_BACKENDS.md)。以下保留原M3及更早结论。

## 最新结论：M3 已接入真实 base，支持外部录音、字幕单击回听与 SRT/VTT 导出

**已实现：**按用户授权在 `H:\AI\Whisper` 安装官方 whisper.cpp **1.9.4 / b5130 CPU x64** 和多语言 **base**，核对官方 SHA-256 与 MIT 许可。开发版和新便携包的本机配置已接入。可导入 WAV/MP3/M4A，保留原件；单击字幕即跳转并播放，支持重复/快速点击与磁盘重开。识别原稿与修订可保存为 UTF-8 SRT/VTT，保留完整录音时间轴的毫秒起止、暂停和重叠；无时间文字不造字幕时间。

**已测试：**Windows **26200.9457 / x64**、SDK10.0.401、runtime10.0.12、NAudio3.1.0。最终 Release 0错误/0警告，**113组默认测试 PASS**；真实11秒公开JFK样本经M3转写、取消/重试、原件哈希和SRT/VTT往返全部通过。发布版实际UI自动语言/2线程识别全流程 **2.762秒**；实际WPF字幕点击9种定位/继续播放检查、4种录音状态保护、字幕导出再导入与重开均PASS。没有采集、mic或上传。不是物理鼠标/可听输出逐样本或普适识别准确率验收。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3-Whisper/ProjectEcho.exe`，保留整个文件夹。616个发布文件、231040862字节；完整性/默认启动/包内Host运行时PASS。发布包直接执行真实识别、字幕回放、既有文字流程和导出关窗检查均退出0。Whisper独立存放在用户指定目录；本机 `Data/settings/whisper.json` 是发布校验后添加的可变本机配置，不属于616个发布文件，未把音频/模型打入发布清单。

**未测试/阻塞：**中文/中英混合、专业词、噪声与长会议识别仍待实测，时间标记仍为approximate；短英文样本耗时不作普遍承诺。M2双设备两小时同步、干净Windows、未验平台/暂缓现场项保持原状态。首轮字幕路径比较错误导致“打开却不播放”的FAIL已保留并修复；旧WPF M4A整秒时长对照失败也保留，未把它写成新PASS。M3状态为 `IMPLEMENTED / PUBLIC_ENGLISH_MODEL_AND_REPLAY_VALIDATED`，不代表M2现场闸门已全过。

详情：[47真实模型与同步字幕](docs/47_M3_WHISPER_AND_SUBTITLE_REPLAY.md)。以下保留此前历史结论。

## 本轮结论：M2复审通过已有回归；MP3、M3文字工作流及任务式UI已实现

**已实现：**保留WAV/M4A，新增实际MP3压缩与限大小分片；M3支持TXT/MD/SRT/VTT/JSON导入、分片时间映射、原稿/修订独立保存、本地搜索和定时回听，提供可取消/重试的本地whisper.cpp适配。按用户决定不加入SQLite。界面拆为录音、会议库、导出、转写与搜索、诊断五页，增加明暗主题与原创多尺寸EXE图标。

**已测试：**Windows 25H2 **26200.9457 / x64**；SDK10.0.401、运行时10.0.12、NAudio3.1.0。最终Release构建0错误/0警告；**105组默认测试PASS**（含实际MP3编码/解码、16k完整识别副本、M3导入/修订/映射和合成CLI进程失败/取消/重试）。实际WPF文字工作流、Paused回听锁定、关窗等后台清理通过；37张布局渲染图审查、EXE图标提取通过。本轮全部为合成文件/无采集验证，没有启mic、真实录音或上传。

**新便携包：**`artifacts/ProjectEcho-win-x64-20260930-M3/ProjectEcho.exe`，保留整个文件夹；616个随包文件、230924906字节。文件哈希、默认隐私启动、Host IPC及包内运行时解析通过；新包直接执行M3文字流程和M2导出关窗检查退出0。旧包与原始音频保留。

**未测试/阻塞：**M2双设备声学同步、干净Windows及未验平台/暂缓现场检查继续保留。M3没有真实whisper.cpp/模型，真实识别准确率、速度与混合语言测试NOT_RUN；合成CLI夹具不是模型验收。MP3两小时/外部播放器和真实DPI/高对比/键盘读屏未测。M2未被标成全部退出闸门通过；M3为`IMPLEMENTED / MODEL_ACCEPTANCE_PENDING`。

本轮详见 [46实施与验收](docs/46_M3_MP3_UI_IMPLEMENTATION.md)、[45转写合同](docs/45_M3_TRANSCRIPTION_CONTRACT.md)。首轮99PASS/6FAIL及修复前截图/日志均保留。以下是历史M2及更早记录。

## 当前结论：M1问题已修复，M2便携验证版已实现；现场退出闸门仍未全部通过

**已实现：**可靠录音、独立mic真正停采、恢复与磁盘时间轴；完整mono WAV/RF64及M4A、真峰值保护、实际字节/时长分片、本地安全导出和可取消后台任务。复审修复了损坏清单数值、早于epoch的QPC、慢周期校验阻塞采集、归档错目标、关窗未等清理，以及压缩重命名后哈希失败的状态问题。原件始终保留；不上传、不依赖模型。

**已测试：**Windows26200.9457/x64，Release构建0错误/0警告；**91组默认测试PASS**。受控远端实际录制**7201.165849秒**，345655961帧完整WAV、全M4A和5分片的EOF/五处标记/覆盖/哈希通过，mic始终关闭，进程退出。实际WPF输出WAV/M4A首尾标记间隔2.300秒通过；M4A虽有WMP整秒时长显示限制，实际尾声未据此截断。受限外网、无模型环境下，新录音在旧导出失败/取消时持续写入并完成实际混音/压缩导出。用户报告的约30秒讲话/UI检查通过已记入。

**便携包：**`artifacts/ProjectEcho-win-x64-20260930-014055/ProjectEcho.exe`，须保留整个文件夹；616随包文件、230571321字节，来源/许可/哈希齐全。本机默认隐私启动、Host IPC及实际包内运行时加载通过。测试工具驱动目录冲突已修正，不需要另装.NET；不将开发机或测试驱动结果称为干净Windows通过。

**最新长文件复验：**采用上述最新包组件，独立复制已核验的两小时原件后，完整导出、真峰值/容器时间表和5分片检查全部PASS；各片实际字节≤20,000,000、解码时长≤1800秒，原件与旧报告哈希不变。所有实录和验证进程已完成；详情见docs/44。

**未测试/阻塞：**双设备两小时声学同步缺共同参考，不能用单轨长录替代；没有可用的干净Windows目标，现有虚拟化未启用且未改系统功能；设备拔插和锁屏/睡眠按用户安排暂缓。Zoom/Webex桌面只有既有客户端测试音有限证据，网页与同时隔离对照未全验。M2不能因此标作全部退出闸门通过。

详细证据：[37实施报告](docs/37_M2_IMPLEMENTATION_REPORT.md)、[42完成条件补强](docs/42_M2_COMPLETION_AUDIT_AND_HARDENING.md)、[43时长元数据](docs/43_M4A_DURATION_AND_WPF_METADATA.md)、[44实际Windows验收](docs/44_M2_AUTOMATED_WINDOWS_ACCEPTANCE.md)。所有早期FAIL和限制保留，不用后续成功覆盖。
以下按时间保留历史证据，后续结果不会覆盖早期失败。阶段表及本节是当前状态。

**2026-09-29 真实mic跟进：**用户已明确授权真实mic测试，并自行将Realtek端点改为48kHz。已修复音频就绪事件未参与等待导致的短缺口、健康时间差下溢；对尚未验证的端点/mix跨采样率组合在启动前明确拒绝，未放宽时钟或停采边界标准。**58组默认自动测试PASS；真实mic短测连续两次PASS**，每轮约20秒、mic10.56秒，开关/5秒暂停/恢复/固定设备重开与精确落盘边界通过；mic1056包全部QPC/设备计数有效，无缺口，原件不改，进程退出。初期沙箱访问拒绝、44.1→48组合失败及一次缺口失败均保留。正常用户Windows权限已Allow，无需重复索取测试授权。仍待手动说话/UI、拔插、锁屏/睡眠和两小时公共标记同步。详见 [36_REAL_MIC_TESTS_AND_MANUAL_CHECKLIST.md](docs/36_REAL_MIC_TESTS_AND_MANUAL_CHECKLIST.md)。

**2026-09-29 M1实施：**采纳独立时钟能力分类，M0以`FEASIBLE_WITH_LIMITATIONS / QPC_ONLY`进入M1，未修复/补造device计数，旧FAIL不变。M1已实现普通录音、暂停/恢复、固定mic真正停采、30秒块、checkpoint/journal/恢复副本、书签与健康/UI控制。Debug/Release均0错误/0警告，**55组默认自动测试PASS**。真实受控进程的40秒暂停/跨块录制、父连接失联停采、32秒Host崩溃恢复和**15分钟数字静音**均通过各自断言；静音保存900.658302秒完整时间轴、31块/43227840帧，PCM全零，进程全部退出。设备时钟/双轨同步单独保留未验收；真实mic、两小时同步、锁屏/睡眠未全测。详见 [35_M1_IMPLEMENTATION_REPORT.md](docs/35_M1_IMPLEMENTATION_REPORT.md)。未进入M2。

**2026-09-29 自主音频测试入口：**已增加 `scripts/Test-AudioIntegration.ps1`，通过生产Host/IPC自动协调测试子进程的播放、录制、停止及离线核验，不再依赖Computer Use点击。固定只录新建测试进程、mic关闭。产品构建0错误/0警告，默认无设备测试 **38 PASS**。首次入口启动竞态失败后已修复；第二次真实Windows自动实采的660Hz/880Hz顺序与时长检查PASS、440Hz未超过检测阈值，Host/子进程全部正常退出，原件不改。但新参数下设备计数仍恒零，**总结果FAIL（1297项时钟/完整性错误）**，M0不标完成、M1未开始。详见 [33_AUTOMATED_AUDIO_PROBE.md](docs/33_AUTOMATED_AUDIO_PROBE.md)。漏洞审计仍未完成。

**2026-09-29 复审修复：**已修复重复 QPC 被误判正常、shared/event 初始化参数、writer 未就绪即 Start、逐路收尾拖延 mic Stop，以及 IPC 输出阻塞拖住 Host 心跳保护的问题。最终 Windows 构建 0 错误/0 警告；**35 项自动测试 PASS**，WPF 无采集启动和 Host 两项无采集 IPC 检查 PASS。本地缓存恢复本次显式关闭 NuGetAudit，漏洞审计仍未完成（原基线 6 NU1900），不能将无警告等同安全通过。七份既有清单离线核验仍呈 device 恒零，旧 FAIL 不变；新参数实采/人工音频测试 NOT_RUN。本轮没有录音、启 mic、入会或继续暂缓的浏览器测试。**M0 仍 IN_PROGRESS / CLOCK_QUALITY_LIMITED；尚未进入 M1。** 详见 [32_M0_REVIEW_REPAIR.md](docs/32_M0_REVIEW_REPAIR.md)。

**2026-09-28 最新跟进：**用户确认第一份 Webex 录音期间未播放测试音，现更正为静音对照通过；第二份有声测试及回放也通过。Zoom 测试音回放和既有 Chrome 合成音实测保留。用户指示暂缓后续浏览器测试，网页/隔离未执行部分保持 NOT_RUN，不再要求现在继续该测试。所有 mic 关闭、原件不改；设备计数恒零的 Complete=false/时钟审计 FAIL 仍单独保留。见 [30](docs/30_M0_ZOOM_SPEAKER_TEST.md)、[31](docs/31_M0_WEBEX_SPEAKER_TEST.md)。最近代码基线为构建 0 错误、6 NU1900、30 项合成 PASS；本轮只更正记录。

当前目录为独立项目 `H:\AI\ProjectEcho`。已建立六工程 C#/WPF 解决方案、真实来源枚举/预检、单一 CaptureHost、主动触发的 10 秒短试录、默认关的独立 mic、两路电平、原轨/时间索引及录后混合 WAV 回放入口。原先等待用户本人按录音按钮的安排已由明确代操作授权更新，不再作为阻塞。

**历史预检：**在授权前的现场检查中，已实际选择 Zoom / Webex 音频进程树并通过静态预检；观察时均为 Inactive，输出为“远程音频”，活动 mic 为 0。当时 Chrome 是普通实例，尚无其音频 session 候选。这些结果不代表当前 PID、来源或采集状态，录音前须重新核对。

**已测试：**Windows 26200.9457 / 25H2 / X64；项目 SDK 10.0.401 与 global.json 一致；NAudio.Core/Wasapi 精确 3.1.0；实际产品构建成功，0 编译错误。合成算法/WAV测试、WPF 启动及真实设备/会话枚举、无采集 Host IPC 检查见 [M0 报告](docs/24_M0_REPORT.md)。NuGet 返回 NU1900：当前网络无法获取漏洞信息，不能把依赖安全审计称为通过。

**未测试：**端点模式、mic、Zoom/Webex 真实会议与网页、440/660/880Hz 同时对照隔离及长录音。Chrome 合成音、Zoom/Webex 客户端测试音的有限录制/回放路径已有证据；设备时钟完整性仍失败。M1 停采边界/恢复/同步与 M2 压缩导出/便携包仍未验收。

**阻塞/限制：**电脑重启后原生 Chrome URL 识别仍失败。用户授权全标签页枚举后 Playwright 正常审批通过，但只返回其 about:blank；随后项目合成页被 file: 协议限制拒绝。未绕过检查。设备位置恒零的底层原因待查；用户暂无 mic；NuGet 漏洞服务不可达。M0 保持进行中。

**工具分步诊断：**窗口枚举/绑定成功，文件和 HTTP 状态读取均失败。Computer Use 相关三个插件本地配置均启用；当前聊天另有内置浏览器 getInfo 路由缺失日志，但不能视为原生 Chrome 错误根因。没有证据支持改权限、安装扩展或改录音源码。详见 [26_COMPUTER_USE_DIAGNOSIS.md](docs/26_COMPUTER_USE_DIAGNOSIS.md)。

## 2026-09-28 规划已采纳，工程环境已配置
以下为 M0 实施前的历史记录；“未开始”只描述当时，当前状态以本页顶部及阶段表为准。
用户已接受复审意见。已同步AGENTS、产品/架构/音频/存储/导出/UI规格、里程碑、需求追踪、57项验收及开发提示词；首版精简且M2包含完整便携包。正式状态见修订文件；下方首次复审记录保留为历史。

项目内SDK **10.0.401**已从微软下载并通过SHA-512核验，NAudio.Core/Wasapi **3.1.0**已恢复到项目缓存。WPF/NAudio无采集环境小程序构建0警告/0错误、运行和self-contained发布后本机运行通过。Git复用现有安装，仅初始化本地仓库；无身份/全局PATH修改、无提交推送。详细版本、命令与证据见 [docs/21_ENGINEERING_SETUP.md](docs/21_ENGINEERING_SETUP.md)。

**未测试：**真实录音API、四平台、mic隐私、同步、恢复、混音，以及完整应用的干净环境运行。M0–M6保持未开始，T01–T57均为NOT_RUN；环境小程序不是录音应用。

## 2026-09-28 规划复审（未实施 M0）
首次审查结论见 [docs/20_PLAN_REVIEW.md](docs/20_PLAN_REVIEW.md)。当时仅提出建议、未改原标准；后续用户采纳后的更新见上节。

本机只读检查：Windows build 26200.9457 / 25H2 / X64；PowerShell 7.6.5；Git 2.53.0.windows.1。脚本检查范围内未发现 .NET SDK，未下载依赖。修改本文件前原交接包 63 个文件哈希校验通过；这不是产品测试。证据在 `.local/diagnostics/environment.json` 与 `.local/diagnostics/plan-review-2026-09-28.json`。M0–M6 状态及 T01–T50 的 NOT_RUN 均保持不变。

| Stage | State | Evidence |
|---|---|---|
| M0 capture feasibility | FEASIBLE_WITH_LIMITATIONS / QPC_ONLY | docs/34；实际API QPC/帧数可用；旧device审计FAIL与未测平台保留，不声称同步通过 |
| M1 reliable recording | IMPLEMENTED / FIELD_ACCEPTANCE_PENDING | docs/35–44；讲话/UI用户通过、修复后固定mic短边界通过；两小时远端通过；双设备声学/拔插/系统状态仍未全验 |
| M2 mixed/export MVP | IMPLEMENTED / FIELD_ACCEPTANCE_PENDING | docs/37–46；原M2回归保留，新增MP3实际编解码通过；20260930-M3完整包；T46干净Windows阻塞 |
| M3 local ASR/import | IMPLEMENTED / PUBLIC_ENGLISH_MODEL_AND_REPLAY_VALIDATED | docs/45–47；真实base英文/UI识别、取消重试、外部录音、字幕点击回听及SRT/VTT往返通过；中文/混合语言/长会议仍未验；无SQLite |
| M3b meeting UI / formats / ASR boundary | FIX_IMPLEMENTED / WINDOWS_AUTOMATED_VALIDATED / REAL_MIC_RETEST_PENDING | docs/48–51；141组通过；七项反馈修复与WPF/公开base通过；修复后16k mic+Zoom和44.1k硬件待验，Qwen仅准备 |
| M3C SQLite / localized workspace | IMPLEMENTED / AUTOMATED_TESTING_COMPLETED / DOCUMENTED_LIMITATIONS | docs/52–53；154默认回归、最终包WPF通过；实际两小时mic连续性FAIL保留；完整有警告导出、静音和故障恢复通过 |
| M3D local ASR hardware / prompt / versions / setup help | COMPLETE_PACKAGE_DELIVERED / AUDIOFIX2_FIELD_RETEST_PENDING | docs/56–59；ASR既有证据保留；AudioFix2有221回归/实际WPF/656文件ZIP通过，用户Win10Fix失败和AudioFix2新机待验分列；CUDA/其他GPU和干净Windows未验 |
| M3E meeting Log / UI / Whisper timestamp | IMPLEMENTED / 233_REGRESSIONS_AND_REAL_CPU_BASE_PASS / TARGET_RETEST_PENDING | docs/60；按会议持久历史/导出/三语界面；目标32.831秒录音需用户重试 |
| M3F input validation / replay / single instance | IMPLEMENTED / 235_AUTOMATED_AND_WINDOWS_UI_PASS / TARGET_PENDING | docs/61；按需压缩；超大适配暂缓 |
| M3G Vulkan / timeline / mic UI | DELIVERED / 241_REGRESSIONS_AND_BUNDLED_VULKAN_WPF_PASS | docs/62；百分秒定位/新mic/659文件完整ZIP通过，声学准确率和旧现场闸门未替代 |
| M3H meeting ASR / mic images / level preview | DELIVERED / 263_REGRESSIONS_AND_BUNDLED_WPF_VULKAN_PASS | docs/63；本场自动prompt/默认隔离、原生remote及合成mic边界、661文件ZIP；真实mic与旧现场闸门不升级 |
| M4 project analysis | NOT_STARTED | none |
| M5 optional tab extension | OPTIONAL_NOT_STARTED | none |
| M6 release verification | NOT_STARTED | none |

## Next task
使用保留的M3H-Final目录；旧测试会议已清空，现有识别设置保留。提示词自动跟随当前会；保存为默认配置只影响以后新会。由用户主动验证真实mic开启预览、关闭边界和所用目标设备；新录音继续保留原件与完整性警告，长期同步/干净Windows等旧闸门不变。

## Per-stage reporting template
Implemented:
Tested (actual command/platform/evidence):
Not tested:
Known issues / risk:
Files changed:
Next smallest task:




