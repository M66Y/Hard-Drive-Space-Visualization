# 隔离 VHD 的 NTFS 记录比对

这是开发测试入口，不是 AsukaLens 产品功能。2026-11-12 用户明确授权本轮创建 256 MiB 一次性 VHD、管理员挂载并在其内部格式化 NTFS、生成合成文件、只读比对，结束后卸载清理。以后执行仍需遵循项目的测试范围授权，不默认对个人磁盘操作。

VHD 是 `artifacts/ntfs-live-<nonce>/fixture.vhd` 普通文件，占用所在磁盘的可用空间；不调整宿主分区。脚本通过 Windows `CreateVirtualDisk` 创建固定大小镜像，无源盘、无父盘，不复制个人磁盘。先由镜像反查虚拟磁盘，核对容量、总线、非启动/系统盘和稳定身份，才允许初始化和格式化。没有盘号、盘符或现有镜像参数。

新分区在无盘符状态下格式化为 NTFS，完成后再分配临时盘符，避免未格式化卷的系统弹窗。C# 读取器还用 VHD 的物理设备路径与卷 extents 核对归属，合成文件通过卷 GUID 路径写入，仅读取自身创建文件的指定记录；不扫描宿主卷和整个测试卷。虚拟磁盘在流程中短暂可见，完成后卸载并删除本轮镜像。

## 执行

先构建测试项目。脚本需要 64 位 PowerShell 7.4+、Storage 模块和管理员权限，本身不自动请求 UAC。以管理员 PowerShell 在项目根目录运行：

```powershell
$result = Join-Path (Resolve-Path artifacts) ('ntfs-live-result-' + [guid]::NewGuid().ToString('N') + '.json')
pwsh -NoProfile -File scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath $result
Get-Content -LiteralPath $result
```

结果文件必须是 `artifacts` 直下的新 `ntfs-live-result-<32hex>.json`，拒绝覆盖。非管理员会在创建镜像前退出。输出只保留阶段、固定错误类别、原生错误码、检查数和清理状态，不保留路径、卷身份或原始记录。

`passed` 还必须同时检查 `detached=true` 与 `cleanup_completed=true`。失败时不伪装成功；若子进程无法退出或虚拟盘无法确认卸载，保留工作目录并报告清理失败，不能直接删除仍在使用的镜像。普通退出时，运行日志和 fixture manifest 随工作目录清理；结果摘要由调用者按本轮需要归档或清理。

## 验证范围

已在 Windows 11 的隔离 NTFS 3.1 VHD 上完成 140 项比对：空文件、非空驻留数据、普通非驻留数据及硬链接、压缩/稀疏文件、命名流、目录，检查原生句柄前后身份与大小稳定性、记录槽号、序列号、默认流逻辑大小，以及已知分配量；非空驻留/压缩/稀疏分配仍明确需要补查。默认合成检查不需要管理员：

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-prototype-tests
pwsh -NoProfile -File tests/NtfsFixtureGuards.Tests.ps1
```

真实测试读取记录使用 `FSCTL_GET_NTFS_FILE_RECORD`，返回记录可能是更小的在用槽，故必须先校验返回槽号再解析。输出记录数据从 offset 12 开始。微软文档没有承诺 USA fixup 表示，本入口对全体 raw/全体已还原两种形态分别校验，不能用该接口失败证明磁盘损坏。参考 [记录 IOCTL](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)、[输出结构](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_file_record_output_buffer)、[卷几何](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer)、[VHD 创建](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-createvirtualdisk)。

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
