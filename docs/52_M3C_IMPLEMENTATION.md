# 52｜M3C 实施合同与验证记录

更新日期：2026-10-01。本文先记录已实现的 SQLite、可恢复删除和依赖合同；最终构建、自动回归、WPF、发布与两小时带 mic 的结论由主任务汇总后填写。**本文中的设计说明、测试入口和初轮证据不等于最终验收通过。**

## 范围与决定

用户本轮要求开启 M3C：启用 SQLite、会议库筛选排序、数据目录打开按钮、改善回听进度流畅度、识别百分比、字幕工作区调整、会议删除确认、全文导出、句子复制以及英文/繁体界面，并在最后继续可以自动执行的既有测试，包括两小时带 mic。此次启用 SQLite 明确覆盖 2026-09-30 不使用数据库的旧决定；旧决定与当时测试报告作为历史保留。

SQLite 只保存可重建索引。原始来源轨、capture index、checkpoint、meeting manifest、归档文件及不可变转写 JSON 继续保留；数据库不是录音成功、声音完整、说话人身份或精确字幕时间的证据。单一 CaptureHost 和录音/导出/ASR 故障隔离不变，不引入后台数据库服务、API 上传或通用作业框架。

## 当前磁盘结构与权威

```text
<用户选择的数据根>/
  library.sqlite3                           # 派生会议元数据索引
  M1Recordings/<目录标识>/
    meeting.json                           # 已有录音/导入清单
    filing.json                            # 可选：用户录后修改的标题、项目
    sources/...                            # 独立原轨（录制会议）
    journal/...                            # 原始时间与恢复证据（录制会议）
    transcripts/
      original/<revisionId>/...            # 导入原稿和规范化不可变版本
      reviewed/<revisionId>/revision.json  # 人工修订，不覆盖原稿
      search.sqlite3                       # 此会派生搜索索引
  DeletedMeetings/<UTC时间-GUID>/<原目录标识>/
                                            # 确认删除后保留整会原件
```

外部音频导入会议仍使用既有导入清单与原件副本结构。`Projects/...` 是早期方案，当前实现按 `M1Recordings` 的直接子目录识别会议；项目是归档元数据，不因改项目而搬动音轨。

数据库、journal/WAL/SHM 旁文件以及 `*.sqlite3.rebuild-<GUID>` 坏缓存备份可能包含会议元数据或文字，属于本地会议资料。它们不自动上传、不附入音频导出、不收入 Git。可恢复删除同样保留资料；它不是永久擦除功能。

## SQLite 会议库

实现位置：`src/ProjectEcho.Core/Library/MeetingLibraryIndex.cs`。

`new MeetingLibraryIndex(dataRoot)` 固定当前本地数据根；公开接口为同步 `Refresh`、`Query`、`Projects` 和 `MoveToTrash`。录音采集不调用这些接口。

| 接口/数据 | 合同 |
|---|---|
| `Refresh(token)` | 仅扫描 `M1Recordings` 直接子目录，读取小型 meeting/filing 元数据，在单个事务内替换完整索引；不解码音频、不扫描采集 journal、不载入字幕 |
| `Query(query, token)` | 以参数绑定执行日期、项目、最短/最长毫秒过滤，按枚举选择固定排序 SQL |
| `Projects(token)` | 返回当前实际存在会议的项目去重列表 |
| `directory_key` | 相对于 `M1Recordings` 的直接目录名，大小写不敏感主键；不信任数据库提供的绝对路径 |
| `meeting_id` | 保留清单内 ID，缺少时使用目录名；相同 ID 或相同标题不会覆盖另一个目录 |
| `Date` | 本机录制取 `startedAtUtc`，外部导入取 `importedAt`；导入日期不冒充真实开会日期 |
| `DurationMs` | 从既有整数 `duration100ns` 转换；未知时保留 null，不从文件大小/文字长度猜测 |
| `Status` | `saved`、`incomplete` 或 `metadata_error`，表示元数据状态，不替代磁盘音轨/时间完整性校验 |

数据库 `user_version=1`。`meetings` 表保存目录键、ID、标题、项目、日期、时长、导入标记和状态，建立日期、项目/日期、时长索引。`filing.json` 优先提供用户录后标题与项目；原始清单不因此改写。损坏元数据或未完成目录仍保留可见状态，便于检查和恢复。

日期过滤为起始包含、结束不包含；界面按本地日期把“结束日包含”转换为下一日零点。长度上下限包含边界，未知时长不会伪装成零来通过长度筛选。排序支持新/旧日期、长/短时长、标题和项目，未知日期/时长排后；标题使用 SQLite 内置 `NOCASE`，不承诺中文拼音排序。

`Refresh` 是从旧文件自动建立/同步派生索引，不是音频格式迁移。取消在提交前回滚索引事务；原件始终不在该事务内。路径读取拒绝网络目录、备用数据流、重解析点以及非当前会议根直接子目录。

## SQLite 转写搜索

实现位置：`src/ProjectEcho.Core/Library/SqliteTranscriptSearch.cs`；调用入口仍为 `TranscriptService.Search(meetingDirectory, query, token)`，无需 UI 改变文字/时间数据合同。

每会 `search.sqlite3` 使用 `user_version=1`，包含：

- `source_state`：核验后全部修订的 SHA-256 指纹。
- `segments`：修订 ID、段 ID、用于搜索的 `ToUpperInvariant` 文字及显示顺序。
- `segments_fts`：使用 `segments` 为 external content 的 FTS5 trigram 索引。

三个及以上 Unicode 字符的查询使用字面 FTS5 trigram 子串匹配；一至两个字符使用 SQLite `instr`，支持“预算”等短中文词。SQL 参数绑定和 FTS 字面引号处理使 `OR`、`NEAR`、引号、`%`、`_` 等保持文字含义。英文检索不区分大小写，简繁内容按实际原文匹配，不自动转换会议资料。每会搜索最多返回 5000 条，保留历史原稿和修订的各自命中。

搜索先执行既有 `ListRevisions`，验证版本、时间/段约束与导入原稿哈希，再使用 SQLite。新原稿或修订改变指纹后，段表和 FTS 在一个事务内重建。已有指纹相同也不直接信任缓存：同一事务内核对权威段数、每一行的版本/段 ID、顺序及完整检索文字，然后运行 FTS5 `integrity-check` 且 `rank=1`，比较词元与 external content 表，最后在同一快照中查询。

这样，缓存删行、同数改文或仅丢失 FTS 词元不会静默变成“找不到原稿”；返回的文字、说话人标签、起止时间和修订属性仍从已核验的权威版本读取。缓存只能帮助检索，不能伪造转写内容。原稿 SHA-256 失败时拒绝加载和搜索，保留现场，不用旧缓存掩盖原件损坏。

本阶段没有建立跨会集中全文表，也没有给每次搜索承诺固定延迟。跨会界面沿用逐会调用；核验原稿、内容和 FTS 会产生读取成本，大型会议库/大量历史版本的性能仍须单列实际测量。

## 缓存恢复、并发与录音隔离

共享实现：`src/ProjectEcho.Core/Library/SqliteDerivedCache.cs`。

每个绝对数据库路径在进程内串行访问；数据库连接关闭池化，配置两秒默认忙等待、`synchronous=FULL`、外键及 `trusted_schema=OFF`。数据库查询/更新使用局部连接与事务，不挂在录音回调或 CaptureHost 音轨 writer 上。

新空库创建 schema；未知版本、SQLite 已识别的格式损坏、缺表/缺字段，以及搜索内容/FTS 完整性不符，会先关闭连接，保留原数据库与存在的 journal/WAL/SHM 为唯一 `rebuild` 备份，再重建并重试一次。不会删除旧数据库证据，更不会改原音频/原稿。忙锁、无权限、磁盘不足等未被当作“可随意重建”的错误；相应 UI 功能报告失败，录音仍通过既有独立路径工作。

会议库展示前调用 `Refresh`；查询从数据库读取目录键后再次验证根范围，并跳过已不存在的目录。数据库不会在后台自行枚举用户其他目录或跟随链接。索引的版本变化不触发原始录音/转写文件的原地改版。

## 可恢复删除与 UI 边界

会议行末的三点菜单由用户选择删除并确认，默认确认按钮为否。目标为当前数据根内 `DeletedMeetings/<UTC时间-GUID>/<原目录名>/`；保留原目录名使复制会议、同名标题和无 manifest ID 的旧录音仍可辨认与恢复。

后端先核验源目录是当前 `M1Recordings` 直接子目录，再逐层检查整棵目录树不得包含重解析点；目标及祖先也在当前本地数据根内。最终操作是同卷目录移动，不递归删除、不覆写已有会议、不修改内部文件、不使用系统回收站。

目录移动成功就是删除的权威状态，SQLite 行删除随后尽力执行。即使程序在两者之间结束，旧根路径已不存在，查询不会展示它；重建只扫描 `M1Recordings`，不会扫描 `DeletedMeetings`，因此不会复活。恢复时把最内层原会议目录移回 `M1Recordings`，避免覆盖同名目录后刷新即可。

UI 删除工作须先阻止该会议新增导入、ASR、修订、混音/导出、回听和字幕加载，等待已启动的相关读取结束；关窗等待删除任务。录音期间不能从菜单发起删除，当前数据根不能在删除期间改变。初轮审查发现并修补了新增任务竞态、刷新触发重复选会覆盖未保存归档文字、删除结果被刷新状态覆盖和过期目录异常越过 UI 兜底的问题；最新代码回归证据待最终汇总，不以代码审查替代实际 WPF 验证。

## 依赖、原生文件与许可

2026-10-01 从官方 NuGet 核对并恢复以下精确版本；所有工程 `packages.lock.json` 已记录传递依赖内容哈希。开发恢复通过项目 `scripts/Dotnet.ps1`，缓存位于项目 `.local`，运行时不下载任何依赖。

| 包 | 锁定版本 | 许可/来源 |
|---|---|---|
| Microsoft.Data.Sqlite | 10.0.12（精确范围） | MIT；Microsoft 官方包 |
| Microsoft.Data.Sqlite.Core | 10.0.12 | MIT；Microsoft 官方包 |
| SQLitePCLRaw.core | 2.1.12 | Apache-2.0；SQLitePCL.raw |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | Apache-2.0；SQLitePCL.raw |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | Apache-2.0；SQLitePCL.raw |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | 包装遵循 Apache-2.0，原生 SQLite 引擎为公有领域 |

官方包依据：[Microsoft.Data.Sqlite 10.0.12](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12)、[Microsoft.Data.Sqlite 文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)、[SQLite FTS5 文档](https://www.sqlite.org/fts5.html)。

许可文本保存于 `tools/licenses/Microsoft.Data.Sqlite-MIT.txt`、`SQLitePCLRaw-Apache-2.0.txt` 和 `SQLite-Public-Domain.html`；具体下载地址见该目录 README。Microsoft 包 nuspec 声明源码 commit `95017c711e6afc1085133d440e42b4bd78155701`；SQLitePCLRaw 包未声明 commit，因此发布清单明确 `sourceCommitAvailable=false`，不补造源码哈希。

`scripts/Publish-Portable.ps1` 已加入这六包的来源、版本、包 SHA-256/SHA-512、NuGet content hash/锁文件比对和实际许可类型；不再把全部依赖标为 MIT。发布检查要求 App 根和 `CaptureHost` 均包含 `Microsoft.Data.Sqlite.dll`、`SQLitePCLRaw.core.dll`、`SQLitePCLRaw.provider.e_sqlite3.dll`、`SQLitePCLRaw.batteries_v2.dll` 与 `e_sqlite3.dll`，并比对两处原生 DLL 与官方缓存包内 win-x64 文件的 SHA-256。实际最终包检查结果待填写。

初轮默认回归的 `sqlite-native-version.txt` 记录 `SELECT sqlite_version()` 为 **3.53.3**；这是项目实际加载的引擎返回值，不能用 SQLitePCLRaw 包版本代替引擎版本。最终发布后还需保留相应实际加载与包完整性证据。离线 publish 明确关闭 NuGet 在线审计，不能把发布零警告当作漏洞审计通过，也不能把开发机 self-contained 运行当作干净 Windows 验收。

## M3C 界面和转写改动

- 左侧会库标题旁筛选按钮：日期区间（本地自然日含端点）、项目、最短/最长分钟数和六种排序；列表显示项目/长度/不完整状态。刷新或筛选不重新选中当前会议，保留未保存编辑。
- 每行三点菜单提供确认删除；默认否；删除期间禁止该会议新文件任务、改数据根或回听，关闭窗口等待操作收尾。设置中可直接打开当前数据根。
- 转写页删除大标题，将导入文字放到末页；校订体可滚动，播放栏与状态栏固定在下方。最小窗口的编辑/导出按钮仍可滚动到达。
- 回听与校订两处提供全文 UTF-8 TXT 导出，保留所选修订原文/重复句，不带时间戳。字幕选择后可通过复制按钮或 Ctrl+C 复制句子原文；编辑框自己的复制行为不被截获。
- 播放更新使用轻量16ms调度，字幕仅跨实际时间边界时重算。UI单调时钟容错最多外推350ms，停止/暂停/定位/倍速/换源时重置；字幕仍使用实际媒体时间，不改录音时钟。首轮Rendering实现仅4.8次/秒有效更新，不能据此断言Windows媒体位置固定低频。修后实际WPF采样约39次/秒有效更新；这不是所有机器固定帧率承诺。
- whisper.cpp启用真实`-pp`回报，有界stderr解析只接收引擎格式百分比。准备/未知进度用不确定状态；引擎100%不替代文字保存完成。真实公开JFK样本的UI和引擎均回报100%，原稿/修订不受进度显示影响。
- 左下语言菜单即时切换简中/繁中/英文；533条界面模板内嵌，无运行时联网。用户标题、项目、转写、路径作为动态参数保留原文。偏好仅写该便携包的Data/settings或开发.local/settings；原始技术异常可能保留来源文字。

简繁转换使用[Windows NLS LCMapStringEx](https://learn.microsoft.com/en-us/windows/win32/api/winnls/nf-winnls-lcmapstringex)，只在开发时生成静态资源；正常产品无需该脚本。修复了首轮开发生成器错误依赖NUL终止而带出尾字符的问题，改为精确使用API返回字符数，533条源/繁体长度及资源覆盖复核通过。没有把用户原文交给转换器。

## 自动回归范围与证据状态

`tests/ProjectEcho.Tests/M3cSqliteTests.cs` 注册九组默认无设备测试，覆盖：元数据/归档迁移、日期/项目/时长筛选排序、外部导入与未完成状态；重复 ID；坏库/未来版本/缺表重建；目录路径篡改和 Windows junction；删除不复活/手动恢复；并发与取消；中英文/符号 FTS 查询；修订刷新/重开；缓存与原稿完整性。逻辑损坏加固又补充删段、同数改文字和单独清空词元三种漏检复现。

| 证据 | 当前适用范围 |
|---|---|
| `.local/TestResults/M3C/sqlite-01` | 早期首轮 147 通过、3 失败；三项为测试捕获异常/匹配数量断言问题，保留原始失败记录 |
| `.local/TestResults/M3C/sqlite-02` | 编译受并行改动影响后误启动旧二进制，已取消；不作为新代码验证 |
| `.local/TestResults/M3C/default-01` | 主任务已取得初轮 154 通过、0 失败；早于最新逻辑损坏检查和删除竞态修补，不能覆盖后续改动 |
| 最终 Release 构建、默认回归 | `.local/M3C/build-final-01.log`：0错误/0警告；`.local/TestResults/M3C/default-02/results.json`：154 PASS/0 FAIL，包含SQLite逻辑损坏加固 |
| 实际 WPF 筛选/删除确认/草稿保持/多语言/字幕功能 | `.local/M3C/wpf-final-01.json`：7流程全PASS，包括两个新M3C入口、历史转写、真实Whisper、字幕回听、导出关窗、布局；测试接口/路由事件，非物理键鼠或读屏 |
| 完整 self-contained 发布与依赖/原生文件检查 | `artifacts/ProjectEcho-win-x64-20261001-M3C`；`.local/diagnostics/portable-20261001-004239/portable-smoke.json`：630文件、App隐私启动/包内Host IPC/运行时PASS；不是干净Windows |
| 最终包直接运行 | `.local/M3C/bundled-wpf-final-01.json`：会库与三语言、平滑回放与字幕编辑、真实Whisper、字幕回听、导出关窗五流程exit0；本机Whisper配置在哈希验包后另存可变Data/settings，不随发行源包分发 |
| 获授权两小时带 mic 实录与离线核验 | 待独立汇总实际选源、设备、时长、时钟/边界、混音/压缩全长及证据；不得用合成测试或历史 mic-off 长录替代 |

操作系统 build、SDK/runtime、实际来源模式、最终包路径与未完成现场闸门由主任务依据本轮真实环境报告填入。SQLite 回归不证明 Zoom/Webex/浏览器现场隔离、硬件时钟长期同步、中文识别质量、物理可听回放或干净 Windows 已通过；既有未测项保留到取得对应证据为止。
