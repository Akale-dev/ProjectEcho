# 原生同场说话人 worker

此目录属于 ProjectEcho M5C。它不采集、播放、上传音频，不读取字幕、不猜测姓名、不保存 embedding。

- 输入严格为完整 16 kHz / mono / PCM16 RIFF WAV，最长8小时。
- 已知人数：锁定 pyannote 3.0 窗口/声音特征 → 全场单位向量 → spherical k-means → 全场窗口投票。16个固定 SplitMix64 k-means++ 初始化、每个最多80次迭代；选纯声音 cosine 总目标最大值。没有根据用户标注挑结果。
- 未知人数：明确仍调用 sherpa 1.13.8 原 C API complete-link；不声称自动人数也得到本次改进。
- 保留原10秒窗/1秒步長；不使用ASR或外置VAD删音。与原管线一致，非重叠少于10模型帧的局部槽不产生用于聚类的embedding；原音频未删除，缺少可信人物证据仍由字幕覆盖/待确认处理。
- 原帧时码和感受野末端语义保持，以便Core锁定模型EOF规则统一处理。native不静默剪掉其他越界。
- 运行前经实际ORT解析两模型并验证必要metadata；损坏文件、模型角色选错或不支持的embedding家族明确失败，不进入C API终止路径。已核验包内nemo/3d-speaker，允许wespeaker metadata；其它家族当前明确拒绝。进程设置无GUI错误模式，异常失败不等待错误对话框。
- worker仅打印CPU确认、算法marker、区间和汇总计数/进度，绝不打印声音向量或文字。重跑保留旧版本由Core完成。
- 开始录音时取消仍由现有Core进程组终止；worker也响应控制事件，在循环边界退出130。没有输出文件。

开发构建：`scripts/Build-M5CSpeakerWorker.ps1 -OutputDirectory .local/M5C/native-worker-01`。正常使用只需要生成的exe和3个DLL；不需要Python、Node或SDK。

构建脚本只用现有项目内工具链/已锁依赖，不自动下载。ORT公共头文件为微软v1.22.2，使用其向后兼容C API；实际执行的ORT仍是已验证1.28.2 DLL。见deps.lock.json、THIRD_PARTY.md和生成的native-worker.json。

源码中窗口与票数重建沿用上游公开逻辑；spherical聚类为本项目改动，不能将该worker标成未修改的官方sherpa程序。旧upstream exe作为Legacy副本保存。

