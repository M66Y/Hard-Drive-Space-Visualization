# 下一步建议：绑定自有来源的BITMAP形态观察

2026-11-12，基于第20轮纯内存定位器。本文件只有方案，尚未实施；本次2小时窗口的VHD许可不自动延续。现有v2有界范围、位图模型与定位器均不证明182个全零槽未分配，也未接入产品扫描。

## 先回答哪个问题

先在另获允许的新建隔离VHD中，观察同一真实record0内记录位图的形态和容量，再决定是否需要非驻留读取器。不能假定合成驻留夹具就是实际布局，也不能将Deferred当成失败已修复或位图全零。

`$Mft` 的未命名 `$BITMAP` 描述MFT记录是否在用；卷簇位图及 `FSCTL_GET_VOLUME_BITMAP` 是另一口径，不替代本来源。参见微软[MFT说明](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table)与[卷位图接口](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap)。

## 建议第一阶段范围

- 另给固定测试模式，例如 `-BitmapBootstrap` / `bitmap-bootstrap` / `bitmap_observed`，不改变旧mft-range v2或22目标模式的成功含义。模式工厂、摘要revision与深白名单独立，不能凭caller bool或模拟token声称真实来源。
- 继续仅自有新建256MiB VHD，准备期验证固定/无父链/镜像与卷归属；同一owner锁成功后，复用它已经读取的1024字节bootstrap交纯定位器，不新增非驻留块读取或扩展记录读取。保留4096槽/4MiB+bootstrap/256次旧预算、21对象对照及关闭/后验/卸载清理。
- 真实报告只观察属性存在性、驻留/Deferred类别、字节容量及受限长度元数据。来源owner与bootstrap归属证据由真实工厂建立；定位器的Synthetic结果不靠改字段升级为Native。来源真实与“已验证位图解释”分开表达。
- 普通驻留容量足够也只报告ResidentCapacityOnly；Missing/Partial/非驻留/扩展等分别保留Unknown或Deferred，不调用位图模型推断真实分配，不跳过全零槽，不改四类decode结果。原始记录、位图正文、引用/路径/LCN/run和token不落盘或进入分享摘要。
- 锁、输入、目标对照、后验或清理失败则清空成功报告；不强制卸载或自动改用个人盘。所有状态为固定字段/代码，关闭期间取消仍检查。

`bitmap_observed` 只表示受控来源的形态观察及闭环完成，不代表位图已解释、182槽已确认、全格式/全占用或性能通过。如果实际是非驻留，此阶段到Deferred即结束，不临时扩大读取范围以获得“通过”。

## 建议接口与摘要合同

### 实现取舍（仅建议，未实施）

对照当前 `NtfsBoundedMftSource` 后建议方案B，仍需用户确定下一轮范围：

| 方向 | 结论与理由 |
| --- | --- |
| A：复制一个新owner | 不推荐；会复制锁、Map、取消和关闭错误优先逻辑，两套生命周期容易分叉 |
| B：同owner的私有固定profile及专用入口 | 推荐；复用当前bootstrap、范围/21目标与关闭闭环，旧模式不执行新定位器 |
| C：在Live外另开/另读record0 | 不推荐；产生新handle/代际及额外读取，不能继承原token/map证明 |

最小触点为Initialize完成bootstrap映射反核验后做形态观察、专用真实factory与Run入口、私有共享执行核心，以及Guard/Live/wrapper的独立模式及新摘要。示意入口 `OpenBitmapObservation` / `RunBitmapObservation` 尚不存在；profile由私有固定枚举选择，禁止caller传模式字符串、预算、handle或验证bool。

当前 `RunEnumeration` 已执行 `CloseAndVerify→Dispose`，不能先调用它再补观察或再读record0。新入口须在同一个私有执行核心中完成范围与形态观察，一次关闭、后验、释放和最后取消检查后再构造报告；旧 `RunEnumeration` 保持原v2合同。错profile入口在额外读取前拒绝并进入明确关闭路径；初始化失败/Dispose清空暂存元数据，不因新观察而保留原始正文。

新测试替身入口固定Synthetic；真实工厂仍只收对应manifest，绑定准备守卫/无父链和同owner。先验证新路径不改变旧RangeOnly行为、预算及已通过的主错误/取消优先级，再做另获授权的真实验收，不复用旧测试结果替代。

真实入口只收新建夹具manifest，不接收volume handle、物理偏移、caller验证标志、任意预算或位图正文。新mode使用固定工厂；测试替身始终输出Synthetic，即使所有模拟校验通过也不能通过真实摘要入口。

bootstrap仍由同一私有owner读入，来源/map/有效长度改变即废弃该代；定位器只消费该私有bootstrap副本，不能用调用方传入的新记录替换。观察数据在内存暂存，到21对象、解锁、后验、fresh facts、backend关闭及最终取消检查全部通过之后才组装公开摘要。父脚本仍以卸载和清理完成为最后成功条件。

第一阶段不需要保存payload：定位完成立即提取固定形态/容量元数据，释放对原始bootstrap及定位器结果的引用；不把CopyPayload放入缓存、队列或报告，也不新增持久状态。owner关闭或初始化失败清空暂存观察与map绑定，旧观察只可作为本次历史结果，不成为后续读盘的当前证明。资源和同步取消边界沿用已验证的自有来源生命周期，不宣称能即时打断系统调用。

建议把现有v2范围摘要作为独立受检字段，新增的属性观察摘要仅有以下受控内容，不能直接序列化内部定位器对象：

外层可用独立revision `bitmap-bootstrap-source-v1`，阶段固定 `ObservedShapeOnly`；Range字段保持整个原v2报告并复用其深白名单，Bitmap字段只包含状态、代码、预期槽、覆盖容量、payload字节数和可空非驻留长度元数据。Invalid定位结果不能进入成功报告。此处字段名/结构均为待实现合同，不是当前软件已支持的格式。

| 字段类别 | 约束与含义 |
| --- | --- |
| 来源与阶段 | 真实owner建立的来源类别；阶段固定ObservedShapeOnly，无VerifiedAllocation或SnapshotComplete值 |
| 状态/代码 | 成功只允许固定Missing、ResidentPartial、ResidentCapacityOnly、Deferred及已审查代码；Invalid只作整体失败，不接任意字符串 |
| 预期与容量 | 预期槽数等于范围摘要，1..4096；payload字节0..512，驻留容量=min(预期槽,payload字节×8)，未提取则0 |
| 非驻留元数据 | 只在对应Deferred代码下允许三个长度整数，范围/大小关系严格校验；其他情况必须null，不含映射或地址 |
| 禁止内容 | bitmap/record正文、逐槽行、真实引用/路径、token、LCN/run、异常文本、真实分配总数与“已修复”标志 |

来源真实只证明受控bootstrap从哪里来，不证明位图格式/bit解释已完成。公开摘要不得提供从容量Complete推导实际Free的捷径。新helper对字段、类型、排序/关系及额外字段深白名单，版本不得混用；旧v2文件快照不改名或重写。

预算不会因复用定位器而增加：没有新的原始读取调用，仍仅bootstrap及原有有界范围；定位/复制的CPU和内存成本另记，不把Raw ReadCalls等同物理I/O，也不承诺实际调用数恒为5。若需要新增读取或不同几何，退出此阶段另给方案。

## 第一阶段验收

纯替身验证来源/模式隔离、一次bootstrap私有来源不换代、容量语义、各Deferred、失败/取消/释放和白名单注入；保留旧Map、枚举、来源及模式回归。若另获当前轮VHD许可，再完成一次真实形态观察、21对象及清理；没有实际许可就只做合成开发，真实项保持待验收。

| 场景 | 必须观察到的行为 |
| --- | --- |
| 位图缺失/空/容量不足 | 保留对应状态，不生成零分配或Verified字段 |
| 普通驻留容量足够 | 只为ResidentCapacityOnly，来源和关闭核验仍独立 |
| 非驻留/扩展/命名/多重/特殊 | Deferred不包含读取地址，不能触发额外读取 |
| 目标缺失、读取/Map失效、形态定位Invalid | 整体失败并清空成功证据，不发布先前页或半个属性摘要 |
| 取消与关闭/清理同时发生 | 复用既有主错误优先规则，最后取消检查阻止成功，卸载不明不删镜像 |
| 旧模式/替身/未知字段或版本混用 | 严格拒绝或保留旧模式，不伪装真实来源或位图完成 |

## 形态明确之后另行决定

1. 若驻留：先设计可核验的位序/已用与释放槽合成对照、同来源绑定和后验，不只验证21个正例或把模拟模型标成真实证明。
2. 若非驻留：先实现纯合成run映射验证（符号delta、VCN覆盖、物理边界/重叠、初始化/EOF、上限及溢出），再明确最多读取多少对齐字节/调用、如何绑定同一owner和清理，单独确认扩大预算及真实操作；当前长度描述符不是读取地址。
3. 属性扩展/压缩/稀疏/加密、额外BITMAP仍保守Deferred，另立范围。真实位图可用后，才讨论与记录四类交叉核验、RecordFlags/SparseMappingFlags支持、完整建树、扩大规模与资源基准。

非驻留与扩展的格式依据见微软[属性记录头](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)和[属性列表](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry)。此方案不包含VSS、活跃个人卷、产品入口、安装打包或发布。
