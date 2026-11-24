# 目录范围元信息重复读取审查

后续状态：C69原报告保留下方审查时点的事实。用户随后批准仅目录枚举祖先复用候选开发，C71已完成合成验证，尚未默认采用或做真实卷测试；见[候选实现与限制](ntfs-namespace-ancestor-candidate.md)。

2026-11-24。开始HEAD`856fb1b`，被审查的产品源码基线`2781ebb`。用户要求先审查、不改界面；接续前的三处文档删除经用户确认保留，独立提交为`30bbec9`。本次只审查源码、核对官方接口语义并运行现有纯合成基线；未实施缓存、删核验或重新扫描个人目录。

## 结论

**确认有重复的路径属性查询，主要候选是同一次快照内的共有祖先校验。** 但目录实际打开/权限检查、最终句柄的身份和重解析点检查应保留。硬链接补查已经按完整对象身份去重；内存字段检查、页面解析和树校验不能算成重复磁盘读取。

建议下一轮只评审“目录枚举阶段的已验证祖先状态复用”候选，先用纯合成计数/反例验证，保持原核验路径回退。大小补查、并发、MFT预算、界面和默认开关不在这个建议范围内；根的常数级重复读取不值得优先单独开发。当前方案不是实施授权。

用户已选择“只评审目录枚举祖先复用”。因此本报告给出这一小范围的具体候选与验收条件；没有开始实现或真实读取，也不将选择方案范围解释为同意开发。

## 调用链与证据

`NtfsPersonalScan → NtfsSnapshotNamespace.Read → NamespaceBackend.OpenDirectory → Resolve → NativeCursor`

| 位置 | 已核实行为 | 判定 |
| --- | --- | --- |
| [Resolve](../src/Scanning/Ntfs/NtfsPersonalSnapshotSource.cs#L194) | 每次查询所选快照根，再逐段File.GetAttributes；多个子目录反复经过同一祖先 | 真正的路径属性调用重复；可以研究复用，尚无耗时占比证明 |
| [NamespaceBackend](../src/Scanning/Ntfs/NtfsPersonalSnapshotSource.cs#L142) | 每个目录先Resolve，再打开实际枚举句柄 | RootIdentity/RootModified来自owner.RootStorage内存记录，不是每次重新读根元信息 |
| [NativeCursor](../src/Scanning/Ntfs/NtfsSnapshotNamespace.cs#L254) | 打开目录，读取该句柄的身份和属性，验证后分页枚举 | 保留真实打开/ACL和句柄核验；父目录页中出现该名称不等于已成功打开它 |
| [Namespace.Read](../src/Scanning/Ntfs/NtfsSnapshotNamespace.cs#L140) | 再取cursor.Identity/Attributes，BFS与seenDirectories拒绝循环/重复目录，分页结束确认覆盖 | 两个属性是构造时缓存的只读字段；重复比较没有新增Native查询。成功目录只打开一轮，不是整棵树扫两遍 |
| [LookupSize / ReadNativeSize](../src/Scanning/Ntfs/NtfsPersonalSnapshotSource.cs#L173) | 特殊对象补查再次Resolve，再开元信息句柄查询Basic/Standard/Id，特殊文件另查压缩/稀疏分配量 | 祖先重复也存在；额外句柄和字段承担大小/修改时间/删除状态等不同职责，不能全部删掉 |
| [Source.Create](../src/Scanning/Ntfs/NtfsPersonalSnapshotSource.cs#L90) | 快照祖先绑定已读所选根元信息，随后ReadNativeSize("",true)又读根 | 常数级候选，发生在来源建立阶段，不是Discovering阶段的主要循环 |
| [SizeIndex归组](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L96) | FileIdentity归组；pending来自唯一对象，逐对象入队，成功时检查calls==pending.Length | 成功构建每个待补查对象一次；未发现逐硬链接别名反复补查或内部重试循环 |
| ReparsePolicy、SizeIndex、Materialize | 多次遍历已在内存的条目/路径/身份及结果 | 有CPU/分配成本，但不等于再扫磁盘，不能混作Native读取次数 |

目录在范围枚举后若还需要特殊元信息补查，可能再次打开同一目录；前者主要拿可枚举句柄及身份/标签，后者查Basic/Standard/Id等额外字段。若为了合并句柄而给所有目录预先读取完整元信息，反而可能增加当前不需要补查的目录工作。本轮不建议这么改。

`RunTimingWorker`没有附加独立全对象Native验收回调；C68计时外的指纹只是内存比较，不能将独立验收工具的额外读盘归入产品重复读取。见[桥接入口](../src/Scanning/Ntfs/FastScanBridge.cs#L199)。

## 计时归因与静态次数

[Discovering阶段](../src/Scanning/Ntfs/NtfsPersonalScan.cs#L83)包含Namespace.Read以及随后的ReparsePolicy.Filter，前者又有目录打开/属性/分页、解码、字符串和索引集合处理。所以C68整个范围核验时间不能全部归因于Resolve；本轮没有新增真实计时或估算具体节省秒数。

令d为相对所选根的目录深度，根为0。当前成功Resolve有d+1次File.GetAttributes调用；D为包含根的、实际打开的普通目录数，则当前目录阶段属性调用为Σ(d+1)。这是C#静态调用计数，不是底层系统调用次数、物理I/O次数或速度倍数。

如果候选保留末级预查、复用已验证祖先且所需状态都能留在容量内，目录阶段可研究降为D次属性查询；实际目录打开D次、身份/标签查询各D次、全部目录分页/EOF覆盖仍保留。以下仅为合成数学示例，候选尚未实现：

| 合成目录形态 | D | 当前属性调用 | 候选条件满足时的属性调用 | 不变的目录打开 / 身份和标签查询合计 |
| --- | ---: | ---: | ---: | ---: |
| 只有根目录，包含大量文件 | 1 | 1 | 1 | 1 / 2 |
| 根下100组，每组再一个子目录 | 201 | 501 | 201 | 201 / 402 |
| 根到第64层的一条目录链 | 65 | 2145 | 65 | 65 / 130 |

扁平大目录可能几乎不受此优化帮助；缓存未命中/到上限时必须回到原校验，不能把条件计数当保证。本轮未读取用户目录统计，不对真实目录形态或收益作推断。

## 推荐候选的边界

1. 只在当前私有NamespaceBackend、本次有效VSS来源内复用已经成功验证的祖先状态。创建时的来源/卷/根绑定和路径词法检查继续保留；不能跨扫描、跨快照或应用于普通活跃目录。
2. 状态绑定来源代际、目录身份、父子关系及规范相对路径，不以同名字符串或一个布尔值当证明。每个目录仍实际打开并核验预期身份、目录类型和重解析点；拒绝/未知/失败不缓存成成功。
3. 建议初始最多8192条有界状态，复用现有相对路径引用，不另存绝对路径副本；满时回退原校验。来源关闭/失败/取消及Namespace生命周期结束使状态失效，不保留全局缓存或新增长期句柄。
4. 保留原完整路径核验供回退与对照。先合并完成合成的共享祖先、深链/扁平、伪造父级、重解析点、身份不符、关闭/取消、跨来源复用和容量上限反例；核对减少的是祖先查询，目录打开/句柄检查/完整覆盖不减少。
5. 原namespace、source、index以及相关重解析点/预算回归必须通过。若需要真实卷或隔离VHD再确认当轮范围；本轮既无新读取授权，也不因C68曾通过而直接实测。验证通过后再谈是否采用或真实提速。

这个候选不改变文件逻辑大小/实际占用、硬链接归账或链接排除，不用目录枚举中的大小字段替代权威大小，不调整补查并发或界面。

## 官方语义核对

- [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)及[重解析点操作](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations)说明打开方式与重解析点的区别。这里保留句柄核验，并将祖先链证明作为单独条件，不把一个打开标志解释为完整路径已获验证。
- [FILE_ID_EXTD_DIR_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_extd_dir_info)包含身份、属性、时间和大小字段；现有项目契约及毒化大小反例明确不拿其中EOF/Allocation作权威占用。不能借本次优化悄悄改变这一契约。
- [VSS工作过程](https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service)说明完成后的影副本只读；[上下文说明](https://learn.microsoft.com/en-us/windows/win32/vss/shadow-copy-context-configurations)区分持久/非持久。代码限定系统提供程序、完成状态和来源身份；这些是局部复用的依据，不证明任意来源或活跃卷都适合缓存。

上面的候选是从本项目所有权/边界约束与接口语义推导的建议，尚不是已经验证等价的实现。

## 本轮验证范围

被审查C#未改变，复用且哈希核对为C68的测试程序集。现有纯合成namespace84、personal-index27、personal-source52项通过；它们支持当前基线约束，不等于候选已通过。没有构建、修改正常程序/界面、个人扫描、raw/VSS/VHD操作、软件打包或Release。审查与来源/计数证据见[C69](../iterations/2026-11-24-69-namespace-audit/README.md)。
