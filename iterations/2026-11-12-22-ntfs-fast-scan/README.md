# 自有测试卷NTFS端到端流程（合并迭代）

2026-11-12，基线 `99fb9980d64fc5e83580b99aaeba3b7e18514af4`。用户先确认30–60分钟[形态观察](../../docs/ntfs-bitmap-source-plan.md)及本轮新建256MiB VHD，随后要求减少拆轮、接下来两小时集中NTFS，并明确批准[合并端到端范围](../../docs/ntfs-fixture-fast-scan-plan.md)：额外只读最多512字节位图前缀，总4195840字节/256次请求、MFT4096槽；在自有盘内额外创建/删除最多2个文件核验释放槽，本时段按需复验后卸载清理。不个人盘/VSS/原始留样/生产入口或发行。全部步骤统一归档、收尾和提交。

## 完成结果

新测试CLI已经连通严格bootstrap/MFT读取、非驻留位图前缀、在用与释放槽核验、夹具目录索引、原扫描器逐项对照。真实 `fixture_scanned` 473项检查通过：21文件对象、21目录、27入口，逻辑21233664字节与分配917504字节均一致；7对象分配来自MFT，14来自前后验证的Native补值。有效前缀32字节、对齐读取512字节；Range5次/263168字节加prefix1次/512，总6次/263680请求字节。来源关闭、后验、卸载及清理通过。

这只证明静态自有测试盘的固定目录流程，不是个人磁盘快扫、全部格式/全卷统计、活跃卷一致性或速度收益。原生产扫描入口未改，未生成包、标签或Release。

## 关键实现与正确性

- `NtfsBoundedMftSource`：私有固定profile与专用factory/Run入口，共享同一个执行核心；RangeOnly不调用新定位器，形态观察不读额外块。新fast模式的Range v2扣除prefix计数，Resources等于二者实际总和；关闭/后验/释放和最后取消检查之后才返回报告。
- `NtfsVerifiedFixtureContext`：固定mode、准备期真实归属/无父链守卫；witness在固定nonce目录CREATE_NEW，两文件同时存在，同句柄取Native身份并设删除，锁前释放所有目录/文件句柄。确认不同slot且不与21目标冲突，释放后要求bitmap0、严格Free、同slot sequence=旧值+1；旧seq限定1..65534。微软[记录头文档](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header)说明free时递增，不猜wrap。
- `NtfsMftBitmapPrefix` / `NtfsRawVolumeLease`：最多1MiB元数据、128runs，验证整个列表的有符号delta/覆盖/初始化/EOF/物理界限/自重叠/MFT DATA重叠后，才给单个512字节计划；只解释所需前缀。新固定profile4195840/256，旧2MiB和4MiB+1024预算保留，调用者不能任意传预算。
- `NtfsMftIndexCollector` / `NtfsFixtureDirectoryIndex`：最多4096对象、16384名称、1048576 UTF-16字符；原枚举器仍仅保21目标。固定scope父链/身份、DOS、硬链接、未知与Native补值来源分开；原Scanner对照逐相对路径/类型/完整身份/逻辑大小，分配按唯一对象汇总并对照根总量，不固定alias归账位置。
- `BitmapBootstrapEvidence` / `FixtureFastScanEvidence`及wrapper：模式、revision与旧v2独立，深白名单/类型/计数关系/预算、失败清空与Synthetic拒绝；只运输验证，不把JSON标签当真实来源证明。正文、文件树、引用/路径、token、LCN/run或任意异常文本不进入公开摘要。

## 实际检查

最终统一Release构建0警告0错误；12 collector+43 fast source、94 prefix、96 index、108 lease、68 old bounded、113 shape source、309 guard+63 old source全部通过，无跳过或遗留失败。新PS fast摘要312、形态319、外层模式/语义420通过；旧v2摘要477、隔离guard36也通过。见[validation.json](validation.json)。

合成边界包括mapping后续run错误/128与129run、1MiB限制、旧profile、新总预算、scope/父链/复用/名称/硬链接、40项外部访问异常伪装与取消、witness碰撞不删旧项、删除失败/取消不成功、slot/sequence/bit不符、主错误与关闭取消优先、缓存/位图清空。独立只读审查无阻塞。实际Native仅正常闭环，取消/资源故障仍为替身验证；不据此宣称真实异常环境全覆盖。

| 真实阶段 | 结果 | 含义 |
| --- | --- | --- |
| 首次形态 | 473、Deferred/BitmapPayloadBudget、关闭/清理通过 | code不足以区分形态，未当最终验收 |
| 修正后形态 | 473、Deferred/BitmapNonresidentPayloadBudget、关闭/清理通过 | 确认非驻留且超512处理cap，不读正文 |
| 端到端 | 473、fixture_scanned、21/21/27及两大小一致，2释放槽通过，关闭/清理通过 | 当前固定scope的完整对照 |

首份证据见[initial-observation.json](initial-observation.json)，精炼后见[observed-shape.json](observed-shape.json)，最终见[fast-scan-native.json](fast-scan-native.json)。初次报告属于提交前的开发版本，最终新白名单不再接受其泛化预算码；保留它解释实际发现，不把它伪装为当前版本通过。中间一次管理员启动被取消，未创建VHD，用户确认重新发起后完成形态复验，见[launch-not-started.json](launch-not-started.json)。

## 限制、清理与交付

14对象Native补值不是纯MFT占用收益，原始请求数不是物理I/O。未测阶段耗时、吞吐、CPU或峰值内存；不能宣称比原扫描器/WizTree更快。其它无效/不支持记录仍保留，21正例+2释放槽不证明全部槽或182全零分配状态均独立核验。未知文件系统开销/命名流范围遵守原大小口径，不等于删除释放量。

规模、其它NTFS格式/几何、Windows10新路径、活跃卷/VSS、提权助手及主窗接入仍待后续工作块；不要把本轮临时VHD许可变成长久授权。合并后的接续顺序见[开发计划](../../docs/development-plan.md)。

三个实际新建VHD均卸载且工作目录已清理；结果副本在归档SHA核对后删除。无新原始样本，旧kernel六样本（8文件/9683字节）保留在本机ignored位置，不上传。正常开发构建保留，无新预览/发行产物。关键文件以本基线的 `changes.patch` 正向恢复并逐文件核对，`files.json`记录必要文件SHA-256（不递归自身或旧归档）；统一提交/推送绑定以Git历史为准。
