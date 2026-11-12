# NTFS 合成记录原型

本目录仅编入测试程序，不进入软件程序集、扫描策略或 UI。默认合成检查不访问磁盘。2026-11-12 新增受控 VHD 比对入口，仅用于管理员运行的隔离测试：IOCTL对照只读取本轮合成文件的对应记录，原始模式另读该自有卷record0以建立映射；不枚举个人磁盘或整卷 MFT。使用与边界见[测试盘说明](../../../scripts/ntfs-prototype/README.md)。

## 有限原始读取原型

`NtfsMftMap` 从严格校验的原始record0建立不可变映射，限定一个未命名普通非驻留DATA、最多128区段；验证VCN、带符号LCN增量、大小及物理范围，不支持属性扩展或稀疏MFT。`PlanRecord` 生成有界连续目标切片，不假定MFT在磁盘上连续。

`NtfsRawVolumeLease` 只借用已经核实归属的自有卷句柄，成功锁定后才进行对齐只读；每次最多64KiB、共128次ReadFile尝试/2MiB，串行保护seek/read、取消后丢弃结果、解锁失败不能验收成功。同步调用中的取消不能立即打断内核I/O，ReadCalls也不代表物理I/O次数。

`NtfsRawComparison` 固定record0与矩阵21对象，经同一持锁句柄读取并走原严格`Decode`。默认流EOF、完整引用、名称/父关系、可见硬链接及限定普通分配量均与Native上下文对照；压缩/稀疏的14个分配量保持未知。解锁后重新核对主对象、硬链接别名、固定命名流和卷元信息；锁前后的不同时间点不构成活跃卷快照。原始字节不落盘，不扩大旧6条kernel样本留存。

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-mft-map-tests
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-raw-volume-tests
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-raw-comparison-tests
pwsh -NoProfile -File tests/NtfsRawComparisonEvidence.Tests.ps1
pwsh -NoProfile -File tests/NtfsFixtureCodeEvidence.Tests.ps1
```

前三项只用合成记录或注入的Native替身，不打开原始卷。真实入口另需本轮授权，见[方案及实际证据](../../../docs/ntfs-raw-record-plan.md)。当前不支持任意卷输入、整卷扫描或生产接入，IOCTL的复杂USA失败仍保留。

## 纯合成读取会话

`NtfsReadSession` 约束批次代际、失效、有限重启、终态和未知值，执行不含I/O。全会话最多接受4096条模拟记录、每批256条、最多2次重启；重启清空暂存且不返还累计工作预算。来源/映射/长度不再可信时丢弃本代结果，旧代通知和完成回调不改变新代。纯模型的不可变来源断言没有实际验证器，LiveUnproven不会完成；结果`CompletedSyntheticSession`只表示调用方的模拟收集结束，不证明全MFT覆盖、真实VSS或活跃卷可靠性。

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-read-session-tests
```

该入口运行核心与独立反例检查，不创建文件、快照或VHD。具体合同、重入边界及后续真实来源要求见[会话计划](../../../docs/ntfs-read-session-plan.md)，不替换已有解码器/统计/生产扫描路径。

构建测试项目后运行：

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-prototype-tests
```

## 输入与输出

- `NtfsRecordDecoder`：限定 1–64 KiB 记录、512 字节 fixup stride、显式卷格式版本和记录槽号；簇大小合成默认 4096 字节，支持 512–65536 的二次幂。真实测试入口传入经核对的卷几何和外部槽号。拒绝未知版本、未知属性类型及无法验证的布局。
- 原始字节经私有副本完成 USA 修复与属性解析，调用方输入不变；输出区分有效、空闲、损坏和不支持，错误码不包含名称或路径。
- 普通未命名 DATA 的逻辑/分配长度从数据属性读取，并对非驻留数据的 runlist、VCN、LCN 和普通分配簇几何进行核对；不使用 FILE_NAME 中的大小副本。真实 VHD 比对发现非空驻留 DATA 的分配大小不能按独立数据簇的 0 值对齐 NativeStorage，已改为 `ResidentAllocation` 待补查；空 DATA 保留经本轮夹具验证的 0 值。
- `NtfsPrototypeIndex`：按卷身份、完整文件引用识别对象，保留多个目录入口并排除 DOS 别名；逻辑大小按入口累加，实际占用按对象去重，对齐现有扫描器的基本口径。它只提供合成输入的内存汇总，不是整卷快照或持久缓存。
- 索引默认上限 10 万条记录（显式调整上限最多 100 万）、20 万个名称入口、800 万个名称字符；每记录最多 256 个名称，每名称最多 255 个 UTF-16 单元。超限拒绝，不悄悄截断后声称完整。

## 保守处理与未实现内容

属性列表和扩展记录尚未拼接，标为待补查并保持大小未知；解码器不把压缩/稀疏 DATA 的表面长度套作实际占用。重解析/云占位、加密、缺失默认数据或标准信息也要求补查。命名流不并入默认流大小，并保留范围提示。除下文限定的普通驻留、压缩和稀疏补查外，其他类型仍只有待补查标记，尚未连接产品回退。

2026-11-12 NTFS-01 第 1 轮新增 `NtfsSizeLookup`，仅对普通文件的 **单一 `ResidentAllocation` 原因**实施句柄补查。上述其他原因仍不处理。输出同时记录逻辑/分配值的来源（记录、句柄补查、未知），不修改原始解析记录。补查连续读取两次，核对卷与完整文件引用、逻辑大小、可见链接数、分配大小、修改时间及打开写句柄状态；失败保留分配未知，不缓存旧结果。

补查按批次单路执行，无工作队列或跨批缓存；最多 4096 个唯一对象，重复对象、槽位复用、路径冲突及名称超限在读取前拒绝。取消在批次和回调前后检查，丢弃部分结果；不能保证打断阻塞中的系统调用。调用方仍须验证文件范围、输入新鲜度和树的一致性，两次句柄结果相同不等于活跃卷快照。

`NtfsLookupExperiment` 在隔离 VHD 内比较选择性补查与“每对象两次句柄读取”的稳定性检查对照：6 轮交替顺序，预读/预热与结果比较不计入耗时。报告字段中的 `NativeCalls`/`NativeMilliseconds` 表示整个 reader/NativeStorage 回调的次数与耗时，一次包含多项系统查询，不能视作物理 I/O 次数。它不包括 MFT 读取、文件创建、建树或 UI，也不是现有扫描器的性能对照；对象比例仅代表人工夹具。

2026-11-12 后续新增 `NtfsResidentLease` 和 `ResolveWithLease`：同一只读句柄仅分享读取权限，句柄存活期间读取两组身份、标准信息和修改时间；不在分享冲突或权限失败后改用宽松分享。只有两组一致且与普通驻留候选的完整身份、EOF 和可见链接数相符，才发布分配量。每个成功候选打开一个句柄、读取两组快照；这两个计数不代表单个 Win32 调用或物理 I/O。

`NtfsLeaseExperiment` 保留原双回调路径作对照，6 轮交替比较单句柄与双打开，预热和结果验证不在计时内。`lease_experiment` 通过独立字段白名单后才进入夹具摘要；失败或清理失败不保留成功实验摘要。

单句柄限制数据写入和删除分享，但不能排除仅改属性的操作或记录读取前后的变化；它不证明文件仍驻留、父目录归属或整卷新鲜度。调用者仍须提供已核验且足够新鲜的记录。两组相同观察不是活跃卷快照，取消也不能打断已进入的同步系统调用。仅测试原型采用这条路径，生产扫描器不受影响。

文件句柄与集成边界测试使用自己的随机临时目录，与纯内存合成入口分开：

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-resident-lease-tests
pwsh -NoProfile -File tests/NtfsLeaseEvidence.Tests.ps1
```

原始 MFT 名称引用计数包含独立 DOS 名称属性；它与排除 DOS 别名后的 Windows 可见硬链接数分开校验。本轮真实文件比对据此修正了原合成夹具的头部计数。

重复记录、槽位复用、目录入口冲突、过期父引用、目录循环、未知大小和总量溢出不能成为完整成功。未提供跨目录的硬链接占用归属或生产图表；目录索引及 NTFS 系统开销不计入文件数据总量。即使 `CompleteSyntheticIndex` 为真，也只意味着这批合成记录满足原型约束，不能证明活跃卷一致。

未实现整卷 MFT 批量读取、NTFS 3.0、不同 fixup stride、持续写入的日志协调、产品权限提升助手、真实性能对比与主程序集成。没有驱动、服务、系统日志修改或发布包。实际记录验证仅覆盖本轮 Windows 11 主机的 NTFS 3.1 VHD、512 字节扇区/4096 字节簇；Windows 10 及其他几何仍未实测。

`DecodeKernelReturned` 只用于已核对来源和身份的 IOCTL 记录，接受全体尾字为 USA 标记或全体为已还原保存值的形式；微软未公开承诺该接口的 fixup 表示，其他形式保守失败。原始入口仍严格验证保护标记，不允许 raw 校验失败后自动当作已修复记录放行。

## 压缩与稀疏默认流补查

`NtfsSpecialLease` / `ResolveSpecialWithLease` 仅接受 **单一 Compressed 或单一 Sparse 原因**、基记录、已知 EOF/未知分配的候选。叠加驻留、命名流、属性列表、扩展、加密或重解析等仍拒绝，不能通过掩去理由扩大范围。普通驻留原入口不变；共有批次守卫仍限制 4096 个对象、重复/槽复用/路径冲突、取消及来源标记。

每对象打开一个默认流只读句柄，仅分享读取；每组依次读取 Basic/Identity/Standard/Compression/Basic，两组对照完整身份、EOF、可见链接数、非删除/目录、属性、修改/变化时间及分配量。压缩只接受 LZNT1，稀疏只接受未压缩格式；不以移位字段推导分配，也不要求分配一定小于逻辑长度。组内前后 Basic 需一致，组间观察也需一致。失败保留未知；成功分配来自 `HandleLookup`，原解析记录不变。

两组观察并非原子快照。已有数据写入/删除句柄导致打开失败，但只改属性的操作、两组之外的变化及读取记录到打开句柄之间的同 EOF 改写仍不能排除。未证明驻留性、父目录归属或活跃卷新鲜度，也未模拟原生调用内部的精确竞争时点；取消只在系统调用前后生效。句柄在全部返回、异常和取消路径释放。

`special_lookup` 对照可压缩零文件、随机压缩文件、全孔稀疏、两端已写入稀疏四类，另外三个命名流组合必须保持未知。`special_cost` 在这四个支持对象上预读/预热后交替六轮，比较一次 NativeStorage 回调与整个严格补查批次；外部逐项结果验证不计时，批次内部必要校验计时。两条路径的稳定性保障不同，不据此计算加速倍数。每批回调/成功句柄/快照组数不代表 Win32 调用数或物理 I/O；不包含 MFT、建树、UI、冷缓存或资源测量。

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-special-lease-tests
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-special-lookup-tests
pwsh -NoProfile -File tests/NtfsSpecialEvidence.Tests.ps1
pwsh -NoProfile -File tests/NtfsSpecialCostEvidence.Tests.ps1
```

首项会创建并清理自有随机临时 NTFS 文件，能力缺失明确跳过；第二项及摘要白名单测试只用合成输入。真实 VHD 中新增特殊硬链接、较长命名流名称时曾出现 `FixupMismatch`，刷新夹具未消除；受限短名形态通过不覆盖失败形态。严格解码未放宽，根因和更一般的内核表示支持仍待研究，见[失败与成功证据](../../../iterations/2026-11-12-09-special-lookup/README.md)。

## 合成夹具与依据

`NtfsRecordEvidence` 提供本机有界正文留样与严格无卷重放；最多六条、每条64KiB，清单64KiB、会话总计512KiB。拒绝字段注入、重复/大小写变体字段、路径越界、哈希变化、重解析及未知文件。schema2新增CaptureKernel，绑定IOCTL外层引用/长度/返回量与经过核对的Native上下文；正文原样保存，严格兼容schema1，离线不证明历史稳定性。清理仅针对实例自有平坦会话；重放匹配原错误码不代表解码通过。

`NtfsKernelMatrix` 已接入受控Live诊断模式，固定A9/B6/C6共21对象，三时点63请求；基础设施错误中止，严格解码失败继续计数并限量留样。schema/摘要和实际VHD结果见[矩阵记录](../../../iterations/2026-11-12-13-kernel-matrix/README.md)。生产未接入；崩溃孤儿恢复、并发对抗和活跃卷一致性仍未实现。

`NtfsRecordFixtures` 在内存创建记录；测试覆盖 fixup、属性边界、UTF-16、空/驻留/非驻留文件、硬链接与 DOS 名称、簇几何、特殊类型的补查标记、取消和索引冲突，另有 512 次固定种子的有限变异检查。合成数据不是磁盘镜像；测试不是完整 NTFS 格式一致性认证。

格式参考 Microsoft Learn 的 [FILE_RECORD_SEGMENT_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header)、[MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)、[ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)、[FILE_NAME](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name)。部分头部字段在公开结构中被保留，本原型的合成布局尚需未来独立来源和真实记录交叉验证；文件身份采用外部提供的槽号加记录序列，不猜测保留字段。
