# 可选 NTFS 快速扫描：研究与原型方案

2026-11-12 第二次进展：用户随后同意创建隔离 VHD，测试入口已通过本机 Windows 11 指定合成文件的真实记录比对并清理，见[验收记录](../iterations/2026-11-12-03-ntfs-vhd/README.md)。驻留实际占用和 DOS 引用语义据此修正；整卷批量扫描、吞吐、其他几何/系统与属性扩展等仍未验收。

2026-11-12 后续进展：用户批准第 1 步，并确认仅做合成记录验证。测试程序内的[离线原型](../tests/Prototypes/Ntfs/README.md)已实现；本文件下方保留原研究范围，阶段 A 的真实卷比对、阶段 B/C 仍待后续授权和验收。原型将属性扩展、压缩/稀疏分配等未支持内容明确标记为待补查，没有实现实际补查器。

日期：2026-11-12。研究基线：`1d098b4`。本轮授权为可行性研究，不表示原型或产品功能已实现，不触发 UAC、访问原始卷或扫描真实磁盘。

## 结论

若目标是接近 WizTree 的整卷扫描速度，值得验证批量读取 MFT 的独立后端，而不是仅提高现有文件读取并发。能否接近仍需在相同设备、范围和统计口径下测量，不能从接口存在推导加速比例。

建议下一步先做与主程序隔离的只读原型：验证记录解码、大小口径和硬链接，再测量整卷读取与汇总。性能和正确性达标后，另行确认权限助手与界面集成。第一阶段不替换自动/高速模式，也不修改现有增量缓存。

## 当前代码能复用什么

- `Core.cs` / `ScanPolicy.cs`：现有扫描器遍历目录并逐文件读取存储元信息；SSD 自动与高速目录并发均最多 8 路。可作为普通权限、非 NTFS 和失败时的回退后端。
- `NativeStorage.cs`：通过句柄读取身份、链接数、大小；压缩/稀疏文件另查询压缩大小。作为小规模样本的校验基准，不将原生句柄查询本身视为不受文件变化影响的快照。
- `ChangeJournal.cs`：已有卷识别、检查点和 V2/V3 变化记录解码，可研究共享底层结构校验。它处理变化追踪，不是完整 MFT 文件清单或大小解析器；现有非特权日志读取可用，不代表原始卷读取权限也可用。
- `DiskEntry` 与查询/图表：保留七项产品功能，但整卷百万级记录若立即生成每个完整路径，会成为内存和建树瓶颈；原型先用文件 ID、父 ID、名称和数值组成紧凑索引，单独测量转换成本。

## 路径比较

| 路径 | 用途与限制 | 建议 |
| --- | --- | --- |
| `FSCTL_ENUM_USN_DATA` | 按游标枚举文件记录信息，适合建立身份和目录关系；返回的 USN 结构没有长度/分配大小字段 | 作为枚举与一致性辅助研究，不称为完整空间扫描 |
| `FSCTL_GET_NTFS_FILE_RECORD` | 获取在用记录；可能返回小于请求 ID 的记录，多记录文件须分别取回；逐条调用仍有成本 | 用于小规模解析校验和特殊记录补查，不预设为最终高速读取方案 |
| 批量读取 `$Mft` 的数据流或经校验的数据区段 | 可减少逐文件打开，但必须解析记录修复、属性列表、驻留/非驻留数据及 MFT 自身碎片布局 | 最有潜力的目标；先原型，再决定能否作为产品后端 |

上述接口语义及结构见 [USN 枚举](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)、[USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)、[读取 NTFS 记录](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)和 [MFT 开发说明](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table)。API 是能力依据，不是本项目性能证据。

## 原型必须明确的统计契约

1. 同时区分文件对象与目录入口。一个对象的多个硬链接可以有不同父目录；DOS 短名不能当作第二个独立文件。用完整文件引用（含序列部分）和卷身份判断对象，不能仅用记录槽号。
2. 大小从正确的数据属性获取；不能把文件名属性中的保留字段当成通用大小 API。支持驻留与非驻留数据、扩展记录和属性列表；未知版本、越界或缺失属性不得猜成零。
3. 明确默认未命名数据流、命名数据流、目录索引和 NTFS 系统元数据各自的范围。先对齐现有两种口径；文件数据统计与卷已用空间分开，不将总量直接宣称等于系统的已用空间。
4. 对压缩、稀疏、驻留数据、WOF/云占位及重解析点分别验证分配语义。尚不支持的类型补查或标为未知；如果必须普遍逐文件补查，重新评估该方案是否仍有速度价值。
5. 提升权限后可能发现普通扫描无法访问的条目；比较须区分权限范围差异与算法错误，不能直接把两次文件数不同归为漏算。原型应报告实际覆盖范围与未知项。

属性布局存在版本限制，参考 [ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header) 与 [FILE_NAME](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name)。现有句柄大小对照参考 [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info)。这些材料不足以替代复杂文件类型的实际校验。

## 权限与读取边界

初步目标限定为本地、已解锁的 NTFS 整卷；普通目录、网络盘、其他文件系统和未知格式继续使用现有扫描。第一阶段不递归跨挂载点，不处理原始物理磁盘或 BitLocker 解锁。直接卷访问的权限与打开约束参考 [CreateFileW 的磁盘与卷说明](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)。

产品集成建议让主界面保持普通权限，仅在用户选择“NTFS 快速扫描”后请求一个短生命周期的提升权限助手。助手只接收受控卷标识与扫描操作，不接受任意命令；IPC 限制到发起进程/账户，限制消息长度与缓冲，只传必要元数据，不把卷句柄交给界面。拒绝 UAC 或权限不足时可返回普通扫描。该方案属于下一阶段待确认设计，尚未实现。

只读共享访问，不加卷写权限，不卸载/锁定卷，不启用或调整 USN 日志、不安装服务/驱动，不默认持久化文件索引。诊断仅保留阶段、计数、错误码与耗时；原始名称、路径和记录留在进程内，不进入公开归档。

## 活跃卷一致性与失败恢复

活跃卷的批量读不是原子快照。原型须校验记录签名、长度、扇区修复、属性边界、引用序列、扩展关系、偏移和算术溢出，处理记录复用、删除及 MFT 自身扩展。未知格式或不完整记录不强行发布完整成功。

有连续日志证据时，可研究扫描前后检查点及受影响对象重读；这仍须覆盖持续写入、硬链接变化、目录重命名和日志断档。设有界修复次数，持续变化则给出部分完成或回退；没有日志也不能把卷上读到的记录直接视为一致快照。第一阶段不引入 VSS 作为隐藏前提。取消、助手异常或回退前丢弃本轮未完成索引，保留旧完整结果；不得混合两条后端的不同扫描批次。

## 分阶段验收

| 阶段 | 输出 | 通过条件 |
| --- | --- | --- |
| A：解析与小规模比对 | 仅测试使用的解码器及合成记录，选定的受控 NTFS 测试卷 | 硬链接、短名、驻留、压缩、稀疏、属性扩展、坏记录与并发变化等边界正确；不支持项明确可见；取消释放所有句柄和缓冲 |
| B：吞吐与内存原型 | 单独报告读取、解析、建索引、汇总和选择性补查成本 | 同一受控卷与现有完整扫描交替至少 6 次，保留异常样本；明确 OS 缓存未清空及外部负载；不只比较原始解析耗时 |
| C：权限与界面方案 | 短生命周期助手、阶段/进度、回退与旧结果保留设计 | A/B 有可重复且足够支撑改造成本的收益后再确认实施；接入图表、查询和用户操作闭环，最后测端到端时间 |

另行比较 WizTree 时记录其版本、提权状态、扫描范围和统计定义；不要求不同定义的工具总数机械相等。真实整盘扫描需用户明确指定测试卷后才执行；当前授权不自动启动整盘扫描。测试目标兼容 Windows 10 22H2 x64 / Windows 11 x64，系统实测缺失必须保留为待验收。

本次结论仅支持“值得开展只读原型”，尚不能承诺具体秒数、加速倍数或已接近 WizTree。A 阶段工作量和结果决定是否进入 B/C；不顺带提高现有并发上限或改变增量正确性。

## 各阶段夹具与失败处理

以下条款中的“本轮”与具体夹具参数属于原始验证轮次。新任务须按当前范围取得相应授权。

- 独立规模模式使用 `--ntfs-scale-tests`、`tests/NtfsScaleEvidence.Tests.ps1` 及 `tests/NtfsFixtureGuards.Tests.ps1 -Scale`；自有快照探针使用 `--ntfs-vss-tests` 与 `tests/NtfsVssFixtureEvidence.Tests.ps1`，合并驱动检查见 `tests/NtfsValidationDriver.Tests.ps1`。本轮用户另行批准新建 1 GiB VHD 的 10k/100k 文件、262,144 槽及 256 MiB＋64 KiB / 8,192 次请求，随后批准另一自有卷的短时 VSS；具体边界见[规模与接入方案](ntfs-scale-and-product-plan.md)。这些许可不延续到个人卷或未来轮次；旧 256 MiB 模式预算不变。快照清理未确认时保留本次镜像和私有恢复标识，不强制卸载、不删除他人快照。
- 规模对照必须计入来源建立、Native 补查、索引/汇总、投影和关闭，说明预热、缓存与采样口径；驻留占用保留 Native 实测，不统一设零或按簇取整。硬链接逐入口分配归账顺序可不同，应核对象占用与范围总量。`CompleteWithinObservedRecords` 不等于个人目录范围完整，不得仅凭已解码记录或忽略系统槽宣布完整；产品接入须有同源目录入口覆盖及失败回退证据。
- NTFS 驻留补查的摘要白名单改动另运行 `pwsh -NoProfile -File tests/NtfsLookupEvidence.Tests.ps1`。原型中的 `NativeCalls` 指完整 reader/NativeStorage 回调，不是物理 I/O 或单个 Win32 调用；暖缓存补查微实验不得当作完整扫描基准。
- NTFS 单句柄补查运行 `--ntfs-resident-lease-tests`（仅创建并打开本轮随机临时文件，不读取原始卷）；摘要改动另运行 `pwsh -NoProfile -File tests/NtfsLeaseEvidence.Tests.ps1`。`LeaseHandles` 与 `LeaseSnapshots` 分别指每批成功打开的文件句柄和元信息快照组数；单句柄的两组观察仍不代表驻留性或活跃卷一致性证明。
- NTFS 压缩/稀疏默认流原型运行 `--ntfs-special-lease-tests`（自有临时文件）及 `--ntfs-special-lookup-tests`（纯合成批次）；摘要运行 `tests/NtfsSpecialEvidence.Tests.ps1`、`tests/NtfsSpecialCostEvidence.Tests.ps1`。仅接受单一理由、限定格式，组合与复杂记录不能掩掉标记。内核 USA 表示失败保持严格拒绝，见本轮记录；暖缓存补查成本不等于整卷收益或等稳定性保证对比。
- NTFS 记录留样/矩阵修改运行 `--ntfs-record-evidence-tests`、`--ntfs-kernel-matrix-tests` 和 `tests/NtfsKernelMatrixEvidence.Tests.ps1`。隔离脚本 `-KernelDiagnostics` 仅在本轮有效授权内使用，固定21对象/63请求；`collected` 只表示收集完成，另看 `all_decoded`、失败/留样计数及卸载清理。`--ntfs-record-replay` 匹配原失败码不代表解码成功。原始记录及完整身份仅留本机 ignored artifacts，公开归档只保存白名单摘要。
- NTFS 受限原始读取修改运行 `--ntfs-mft-map-tests`、`--ntfs-raw-volume-tests`、`--ntfs-raw-comparison-tests`、`tests/NtfsRawComparisonEvidence.Tests.ps1` 与 `tests/NtfsFixtureCodeEvidence.Tests.ps1`。隔离脚本 `-RawRecords` 仅在本轮明确授权内使用，先锁自有新建VHD，固定record0及21对象、同句柄对齐读取，解锁后复核；锁或任何校验失败即停止，不强制卸载。`verified` 必须同时核对解锁、卸载清理及占用未知项；不等于活跃卷快照、生产后端或整盘性能。原始记录只在内存，不能自动沿用为个人卷读取许可，范围见[受控方案](ntfs-raw-record-plan.md)。
- NTFS 读取会话模型运行 `--ntfs-read-session-tests`，只用合成输入，不访问卷。全会话累计最多4096条、2次重启、每批256条；来源断言不是实际验证器，`CompletedSyntheticSession` 不代表MFT覆盖或真实快照完成。映射/来源失效、已观察高水位、全部异步入口代际、取消/重入、未知值及溢出按[会话契约](ntfs-read-session-plan.md)检查，不据此启用VSS、真实卷读取或产品入口。
- NTFS 有界MFT范围运行 `--ntfs-mft-enumerator-tests`、`--ntfs-bounded-mft-tests`，共享改动另跑map/raw-volume/fixture-source检查及 `tests/NtfsMftEnumerationEvidence.Tests.ps1`、`tests/NtfsFixtureCodeEvidence.Tests.ps1`。`-MftRange` 独立于旧22目标模式，仅本轮另获范围授权后操作新建VHD，最多4096槽/4MiB+bootstrap/256次读取尝试。`range_classified` 只表示严格四类覆盖与21对象/关闭核验，不代表全格式、完整占用或速度；invalid/unsupported不得当空闲、零占用或磁盘损坏。见[有界方案](ntfs-bounded-mft-plan.md)，当前许可不延续到以后、个人卷或VSS。
- 有界MFT分类诊断当前使用v2摘要；`CodeCounts`只接收32种固定Status/Code配对，`AllZeroInvalid`只是严格Invalid中的全零观察，不改变Free或证明未分配。更改诊断合同需同步上述C#/PS检查、版本及深白名单，保留历史revision，不输出原始记录或任意异常文本；来源关闭/失败隔离仍适用，见[分类方案](ntfs-classification-diagnostics-plan.md)。
- NTFS 位图模型运行 `--ntfs-bitmap-model-tests`，仅合成输入，最多4096槽/512字节；结果固定Synthetic，三态位图声明与四类解码独立，缺失或未覆盖保持Unknown，不凭全零改成Free。真实MFT记录位图与卷簇位图不得混用，模拟绑定不是真实来源验证器；不由此新增Native读取或产品入口。外部集合/Memory访问的异常不可信，统一净化并用传入token检查取消，不能因异常类型看似受控而透传任意文本。见[模型方案](ntfs-bitmap-model-plan.md)。
- NTFS 受限BITMAP属性定位运行 `--ntfs-bitmap-attribute-tests`，改动另核对Map/枚举/位图模型回归；只从1024字节合成record0经严格Map/USA校验提取<=512字节普通驻留payload。Missing/Partial/Deferred/Invalid明确区分，非驻留不返回读取地址。Complete只表示字节容量，结果Synthetic不证明真实位序/来源/分配；不序列化payload，不默认接Native、v2或产品。见[属性方案](ntfs-bitmap-attribute-plan.md)。
- NTFS 自有来源形态观察运行 `--ntfs-bitmap-source-tests`、`tests/NtfsBitmapBootstrapEvidence.Tests.ps1`；真实入口为隔离脚本 `-BitmapBootstrap`，只复用原bootstrap，旧读取预算不扩。驻留/非驻留超限码分别表达，不由容量推断分配状态。见[来源说明](ntfs-bitmap-source-plan.md)。
- NTFS 受控端到端运行 `--ntfs-bitmap-prefix-tests`、`--ntfs-fixture-index-tests`、`--ntfs-fixture-fast-scan-tests`，共享改动另查raw-volume/bounded/fixture-source/bitmap-source回归及 `tests/NtfsFixtureFastScanEvidence.Tests.ps1`、`tests/NtfsFixtureCodeEvidence.Tests.ps1`。真实 `-FastScan` 仅在本轮明确授权内新建VHD：最多4096槽/4MiB+1536字节/256次请求，最多512字节位图读取，两个固定自建/删除witness。旧profile不扩，新权限不延续。`fixture_scanned` 仅表示固定夹具树与原扫描器一致；14对象Native补值、未知/严格拒绝、scope及非性能边界如实说明，不能当作个人整盘支持或WizTree级收益。见[合并范围](ntfs-fixture-fast-scan-plan.md)。
- NTFS 自有来源适配器运行 `--ntfs-fixture-source-tests`、`tests/NtfsSourceEvidence.Tests.ps1`、`tests/NtfsFixtureCodeEvidence.Tests.ps1` 及原隔离守卫。真实脚本 `-SourceAdapter` 只在本轮明确VHD许可内运行，固定record0+21对象；新模式为 `fixture-source`、成功为 `source_verified`，不与旧raw成功混用。准备期先验证fixed/no-parent、实际VHD/卷/marker归属再调用旧准备路径；实际来源factory另开自持句柄、私有token/map及固定Native后验，不能由caller bool、任意路径/句柄或模拟来源伪造。结果envelope关闭失效，旧immutable副本不能当当前证明。仅当前受控静态来源，不是全MFT/VSS/活跃卷验证，详见[来源方案](ntfs-fixture-source-plan.md)。
