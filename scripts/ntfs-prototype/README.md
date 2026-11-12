# 隔离 VHD 的 NTFS 记录比对

这是开发测试入口，不是 AsukaLens 产品功能。2026-11-12 用户明确授权本轮创建 256 MiB 一次性 VHD、管理员挂载并在其内部格式化 NTFS、生成合成文件、只读比对，结束后卸载清理。以后执行仍需遵循项目的测试范围授权，不默认对个人磁盘操作。

VHD 是 `artifacts/ntfs-live-<nonce>/fixture.vhd` 普通文件，占用所在磁盘的可用空间；不调整宿主分区。脚本通过 Windows `CreateVirtualDisk` 创建固定大小镜像，无源盘、无父盘，不复制个人磁盘。先由镜像反查虚拟磁盘，核对容量、总线、非启动/系统盘和稳定身份，才允许初始化和格式化。没有盘号、盘符或现有镜像参数。

新分区在无盘符状态下格式化为 NTFS，完成后再分配临时盘符，避免未格式化卷的系统弹窗。C# 读取器还用 VHD 的物理设备路径与卷 extents 核对归属，合成文件通过卷 GUID 路径写入；IOCTL对照只读自身创建文件的指定记录，原始模式另读该自有卷record0建立映射，不扫描宿主卷和整个测试卷。虚拟磁盘在流程中短暂可见，完成后卸载并删除本轮镜像。

## 执行

先构建测试项目。脚本需要 64 位 PowerShell 7.4+、Storage 模块和管理员权限，本身不自动请求 UAC。以管理员 PowerShell 在项目根目录运行：

```powershell
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath $result
Get-Content -LiteralPath $result
```

结果文件必须是 `artifacts` 直下的新 `ntfs-live-result-<32hex>.json`，拒绝覆盖。非管理员会在创建镜像前退出。输出只保留阶段、固定错误类别、原生错误码、检查数和清理状态，不保留路径、卷身份或原始记录。

复杂记录诊断使用同一入口加 `-KernelDiagnostics`，仍需本轮有效VHD授权。固定21个合成对象、63次请求，分别比较三类文件的名称长度、额外硬链接、命名流以及关闭/重开/刷新时点；不修改主机8.3策略。报告 `collected` 与 `all_decoded` 分开：失败记录仍严格失败，收集完成不是解码通过。普通不带此开关的comparison路径保持独立。

最多6条原始正文与schema2绑定清单仅保留在本机 `artifacts/ntfs-record-evidence-<nonce>`，总上限512KiB；最终样本约9.5KiB，已在VHD卸载后经独立进程重放。它们为下一步研究保留，不上传、归档原始内容或自动扫描其他目录；可分享结果仅取白名单矩阵摘要。空会话清理失败有独立状态，不抹掉已收集的严格失败；中途基础设施异常时可能保留部分会话，需核对归属后处理，不能删除未知文件。具体结果与限制见[矩阵记录](../../iterations/2026-11-12-13-kernel-matrix/README.md)。

`passed` 还必须同时检查 `detached=true` 与 `cleanup_completed=true`。失败时不伪装成功；若子进程无法退出或虚拟盘无法确认卸载，保留工作目录并报告清理失败，不能直接删除仍在使用的镜像。普通退出时，运行日志和 fixture manifest 随工作目录清理；结果摘要由调用者按本轮需要归档或清理。

## 验证范围

已在 Windows 11 的隔离 NTFS 3.1 VHD 上完成 140 项比对：空文件、非空驻留数据、普通非驻留数据及硬链接、压缩/稀疏文件、命名流、目录，检查原生句柄前后身份与大小稳定性、记录槽号、序列号、默认流逻辑大小，以及已知分配量；非空驻留/压缩/稀疏分配仍明确需要补查。默认合成检查不需要管理员：

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-prototype-tests
pwsh -NoProfile -File tests/NtfsFixtureGuards.Tests.ps1
```

IOCTL对照使用 `FSCTL_GET_NTFS_FILE_RECORD`，返回记录可能是更小的在用槽，故必须先校验返回槽号再解析。输出记录数据从 offset 12 开始。微软文档没有承诺 USA fixup 表示，本入口对全体 raw/全体已还原两种形态分别校验，不能用该接口失败证明磁盘损坏。原始模式另经持锁卷句柄读取并只走严格raw入口。参考 [记录 IOCTL](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)、[输出结构](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_file_record_output_buffer)、[卷几何](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer)、[VHD 创建](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-createvirtualdisk)。

这不是整卷读取、稳定快照、吞吐或资源占用验收，也不证明已接近 WizTree。尚未覆盖 Windows 10、其他卷格式/几何、碎片化 MFT、属性扩展、持续写入和日志协调。

## 驻留补查实验（NTFS-01 第 1 轮）

本轮再次取得用户同意后扩展夹具，覆盖 1/7/8/9 等小尺寸及向非驻留数据转换的较大文件。真实记录先与 NativeStorage 核对；仅将普通直接可用或需要驻留分配补查的唯一对象送入实验，压缩/稀疏/命名流等排除数量单独报告。

新版成功摘要必须包含 `lookup_experiment`：25 个候选、其中 20 个驻留补查和 5 个直接取值、另有 3 个特殊对象排除；每轮选择性查询 40 次、全部对象双读对照 50 次，6 轮交替顺序的原始耗时和去名称的驻留长度/分配示例均保留。该次数是整个 NativeStorage 回调，不是单次 Win32 调用或物理 I/O。真实过程检查数因新增夹具与验证而变为 862，旧 140 项证据保留原范围。

对照是每对象两次句柄读取以做稳定性检查，不等于当前生产扫描器；生产完整扫描通常逐入口做一次 NativeStorage 读取，且硬链接入口数也可能不同。因此不能把 40 对 50 的差值当作相对生产扫描的节省。实验已预热，不包含 MFT 获取和建树；没有 CPU、峰值内存或物理 I/O 测量。小样本成本用于选择下一步方案，不是整盘提速证明。

摘要在脚本中经过字段、类型、范围与关系校验，仅成功且清理完成后透传；拒绝额外路径等字段。检查入口：

```powershell
pwsh -NoProfile -File tests/NtfsLookupEvidence.Tests.ps1
```

## 单句柄对照（NTFS-01 后续）

新增 `lease_experiment`，对同一普通候选比较原双打开与单句柄内两组快照；6 轮交替，逐项结果与工作量检查在计时之外。每批分别记录回调、成功打开句柄和快照组数；不将这些计数当作 Win32 调用数或物理 I/O。白名单入口：`pwsh -NoProfile -File tests/NtfsLeaseEvidence.Tests.ps1`。

2026-11-12 本小时另获用户允许按需重复使用已审查隔离流程，本次通过 1,327 项，20 个驻留候选的 lease 每批成功打开 20 个句柄、读取 40 组快照，镜像已卸载清理。该授权不扩展为以后默认许可。真实生产扫描、活跃卷一致性、其他特殊类型及不同系统仍未因此验收。

## 压缩/稀疏限定补查（NTFS-01 后续）

后续一小时开发另获用户允许重复本流程。夹具增加随机压缩、带写入区的稀疏、压缩/稀疏叠加命名流；当前 34 个文件入口、32 个唯一对象。原驻留实验仍为 25 个候选（20 驻留 / 5 直接），特殊排除数随夹具增加为 7，历史 3 个排除对象的证据保持原范围。

`special_lookup` 核对四个支持对象的逻辑/分配量及来源；三个命名流组合保持未知。四次补查打开四个句柄、读取八组快照。`special_cost` 在四个对象上预热后交替六轮，保留一次 NativeStorage 读取和更严格双快照补查的批次耗时。稳定性保障不同，不能解释为等保证的加速比较，更不是整卷扫描基准。两项摘要均经独立深层白名单校验，整轮或清理失败不保留成功摘要。

本轮还发现并保留三次严格 USA 校验失败：额外特殊硬链接及较长命名流名称的记录，返回的两个扇区尾字只有一个匹配保存值。对自有文件/命名流执行持久刷新仍失败；原因未证明，不能声称磁盘损坏。用户选择保持严格解码，先完成单名称特殊对象与短名命名流组合的限定验证；移除新增特殊别名、使用短名仅隔离测试目标，不代表修好了广泛记录支持。首次限定正确性对照通过 1,441 项，后续成本结果及每次失败见[本轮记录](../../iterations/2026-11-12-09-special-lookup/README.md)。

```powershell
pwsh -NoProfile -File tests/NtfsSpecialEvidence.Tests.ps1
pwsh -NoProfile -File tests/NtfsSpecialCostEvidence.Tests.ps1
```

以上不新增生产入口、原始卷写入或默认管理员要求；VHD 授权仅限本小时。其他形态和系统、实际组内竞争、属性列表/扩展记录与活跃卷协调仍待验证。

## 受控原始记录对照

本模式按已确认的[受控方案](../../docs/ntfs-raw-record-plan.md)实施，只有本轮另行允许时才运行。它与 `-KernelDiagnostics` 互斥，不接受现有镜像、盘号或任意卷输入。先使用既有归属守卫创建256MiB新VHD，完成矩阵Native/IOCTL观察并关闭文件，然后成功锁定该虚拟卷；同一个只读卷句柄读取record0和21个已知对象。解锁后先复核文件，再重新打开元信息句柄核对卷绑定与几何，最后父进程卸载清理。

```powershell
# 已构建测试DLL，且本轮明确获准使用隔离VHD时，在管理员PowerShell执行：
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 `
    -ResultPath $result -RawRecords
```

结果文件必须不存在并处于允许的artifacts范围；不能覆盖旧报告。成功状态为 `verified`，必须同时检查 `raw_verified`、`detached`、`cleanup_completed` 和 `raw_comparison`。`RawDecoded=22` 只说明这22条严格解码，`UnknownAllocationObjects=14` 如实保留特殊分配未知，不是整卷或占用统计全部完成。锁失败不读取，也不用强制卸载重试；失败/清理失败清空成功摘要。最多128次ReadFile尝试、2MiB请求、每次64KiB；实际受控夹具为22次/22528字节，不能当物理I/O或性能结论。

原始记录仅在内存，raw模式不创建留样会话；旧6条kernel样本仍仅本机保留。公开结果经过深层字段及语义代码白名单，不转发任意异常或标识。真实成功、失败尝试和未验收项见[本轮记录](../../iterations/2026-11-12-14-raw-records/README.md)。授权不延续到以后，不锁个人卷，不提供生产入口或打包。

## 受控测试卷端到端流程

已实现 `-FastScan`：自动创建新的256MiB隔离VHD，在原固定矩阵中生成21个测试对象及目录/硬链接；额外两个CREATE_NEW文件在同句柄身份核对后删除，用于核验释放槽。读取严格record0、受限MFT及所需位图前缀，建立固定夹具目录索引，并在解锁后用原 `DiskScanner` 逐项对照。它是开发验收入口，普通软件仍使用原扫描后端。

```powershell
# 开发机先构建测试DLL；本轮已明确允许隔离VHD后，在管理员PowerShell 7.4+执行：
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath $result -FastScan
```

该模式与其他实验开关互斥，不接受现有磁盘或镜像。新profile固定最多4096槽、4195840字节总请求/256次ReadFile尝试；位图最多512字节对齐读取，只解释覆盖本次槽数的前缀。映射元数据最多1MiB/128runs，即使只读首块也验证整个受限run列表。旧profile不变。首次使用本轮真实范围仍需明确确认，不把文档命令当以后自动提权/读盘许可。

成功需同时核对 `status=fixture_scanned`、`fixture_scanned=true`、`detached=true`、`cleanup_completed=true` 及 `fast_scan`：

- `Range`保留独立v2，记录仅MFT/bootstrap的请求量；`Bitmap`记录前缀、21正例与2已释放/严格Free槽，`Resources`为二者精确总量。
- `Index`只统计 `FixedFixtureDataRoot`，`Comparison`逐项核名称、类型、完整身份、逻辑大小；分配按唯一对象汇总并对照根总量，不要求硬链接alias的归账位置一致。
- 当前真实结果为21目录、21文件对象、27入口；逻辑21233664字节、分配917504字节，7对象分配来自记录、14来自前后验证的Native补值。非驻留前缀有效32字节，对齐请求512字节，总6次/263680请求字节。它们不是物理I/O、CPU/峰值内存或速度收益测量。

失败/取消、来源失效、任何后验或清理失败清空成功摘要。原始页/位图/身份/地址/文件清单不进入公开结果；对照树仅内存使用。全部Native句柄、目录pin和witness文件句柄在阶段边界释放，不强制卸载不明卷。新路径的故障/取消目前以替身测试为主，真实只做正常闭环。

本轮未接个人卷、活跃卷/VSS、主窗入口或发布；其它格式、Windows10新路径和规模收益仍待验证。可重复检查与证据见[合并记录](../../iterations/2026-11-12-22-ntfs-fast-scan/README.md)。旧6条kernel样本仍只本机保留，不新增原始留样。

## 真实位图形态观察模式

`-BitmapBootstrap` 对应 `bitmap-bootstrap` / `bitmap_observed`，复用同owner已读bootstrap，不新增原始读取。六字段观察与完整旧v2范围组成独立报告，只在21目标和完整关闭/后验之后发布。超512处理cap分别输出 `BitmapResidentPayloadBudget` 与 `BitmapNonresidentPayloadBudget`；仅表达形态，不解释分配。源码定位器内部Form不自动序列化，不向公有摘要加任意字段。本机复验473通过，确认非驻留超限；记录见同轮归档。

## 有界MFT范围模式

仅本轮另获有界范围许可时，入口加 `-MftRange`，与其他实验模式开关互斥。它仍只创建和操作新的256MiB隔离VHD，不接受现有磁盘/镜像输入。准备期先验证真实归属和无父链，新factory再次绑定独立owner，锁成功才读取。

```powershell
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath $result -MftRange
```

模式 `mft-range`，结果 `range_classified`，必须同时核对 `range_classified=true`、`detached=true`、`cleanup_completed=true` 和深白名单 `mft_range`。状态只说明受限MFT有效范围已分类、21目标对照及关闭验证通过，不是格式全支持、占用完整或性能通过。失败/清理失败不保留成功摘要；旧raw/source成功标志保持false。

最多4096槽/4MiB有效范围，加1024字节bootstrap；最多256次ReadFile尝试，每页64KiB，超限前置拒绝，不截断。一个managed页与一个aligned native缓冲复用，不保存原始记录。四类和必须等于visited，特殊分配未知仍保留。实际256槽分类与未验边界见[第17轮](../../iterations/2026-11-12-17-bounded-mft/README.md)。计数不是物理I/O，未测整卷吞吐。不延续本轮授权到以后、个人卷或VSS。

当前新版摘要revision为 `bounded-mft-source-v2`，新增 `AllZeroInvalid` 与最多128项 `CodeCounts`（固定Status/Code/Count）。32种精确配对按状态与Ordinal代码排序，四类小计、visited总数及全零子集均需吻合；helper不静默接受旧v1。全零计数不能代替MFT位图或分配状态核验；字段仍仅聚合信息，无槽号或原始字节。历史第17轮v1快照不修改，后续证据见[第18轮](../../iterations/2026-11-12-18-mft-classification/README.md)。

## 自有来源适配器模式

在本轮已明确获准重复隔离VHD时，使用既有新结果路径入口加 `-SourceAdapter`（与 `-RawRecords` / `-KernelDiagnostics` 互斥）：

```powershell
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath $result -SourceAdapter
```

模式为 `fixture-source`，成功状态 `source_verified`；必须同时核对 `source_verified`、`detached`、`cleanup_completed` 及 `source_adapter.Comparison`。新适配器先在准备期核对固定/无父链/真实卷归属，再由独立owner打开自己的句柄，成功锁定后读既有22记录并执行固定后验；不复用caller bool或任意句柄作为证明。真实结果token与映射只在内存，关闭后envelope失效；模拟Native报告不能通过真实来源摘要白名单。

新来源失败只允许固定类别、数值 `native_error` 和枚举 `native_operation`，任意异常文本/路径/身份不输出；失败/清理失败清空成功证据，旧 `raw_verified` 不为新模式别名。预算、占用未知与未接产品的边界见[第16轮方案](../../docs/ntfs-fixture-source-plan.md)。不因这个模式存在而获得以后VHD、全MFT、个人卷或VSS许可。
