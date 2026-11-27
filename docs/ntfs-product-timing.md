# NTFS 完整主窗计时工具

## C77 大小补查耗时口径

产品主窗按收到阶段通知的时间计时。第一个对象发起大小补查时，助手发出 `Supplementing`。其余对象共用同一个阶段。所有补查工作结束后，程序还要遍历唯一对象、核对大小和链接数、计算合计并生成索引字典。索引返回才发出 `Building`，随后生成界面的文件树。因此，C76 记录的这段时间覆盖了并行查询、结果校验和索引收尾。它可以用来比较用户等待的阶段，无法换算成每次系统查询的耗时。[阶段起止](../src/Scanning/Ntfs/NtfsPersonalScan.cs#L111) · [并行补查与汇总](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L185) · [客户端计时](../tests/NtfsProductTiming.cs#L178)

### 哪些对象可能需要补查

| 已有证据 | 可以判断什么 |
| --- | --- |
| [固定文件生成规则](../tests/Prototypes/Ntfs/NtfsScaleExperiment.cs#L76)及[实际准备入口](../tests/Prototypes/Ntfs/NativeVssScaleExperiment.cs#L261) | 十万个独立文件中，一万个为 32 字节，一万个为空，八万个为 1,024 字节。另有十个硬链接别名，均指向 1,024 字节文件。生成器没有专门制造压缩或稀疏文件。 |
| [记录解码](../src/Scanning/Ntfs/NtfsRecordDecoder.cs#L159)及[补查判定](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L152) | 若普通非空文件的默认数据直接存放在 NTFS 记录内，记录能给出文件大小，但当前解码器将实际占用留待系统查询。空的驻留数据在相关字段完整时可直接取零。记录不支持、大小字段不全、链接数不明等也可能触发补查。 |
| [按文件身份归组](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L96)及[完成校验](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L221) | 成功扫描中，每个待补查的唯一对象调用一次补查。增加硬链接名称不会使同一对象多次入队。一次补查仍包含路径属性核验、打开元信息句柄和多项查询。[实际查询](../src/Scanning/Ntfs/NtfsSnapshotMetadata.cs#L24) |
| [C24 同规模原型的公开数据](../iterations/2026-11-12-24-snapshot-scan/scale-100000.json) | 当时三个快扫样本各记录了一万次补查，均覆盖一万个唯一对象。工作量栏的 `ResidentFiles` 按生成规则填写；实际调用数单独采集。历史总量支持优先检验 32 字节这一组，但没有对象类别和补查原因的交叉统计。 |

C76 成功样本显示两次快扫都进入了补查阶段。内部索引已有补查对象数、调用数及并行工作耗时，主窗计时摘要只保存客户端阶段和进程资源，没有这些索引字段，也没有按原因分类的数据。现有报告无法确定有多少个 32 字节文件实际驻留、它们占用了多少补查时间，或各原因是否重叠。[索引报告](../src/Scanning/Ntfs/NtfsSnapshotSizeIndex.cs#L12) · [主窗摘要字段](../scripts/ntfs-prototype/ProductTimingFixtureEvidence.ps1)

按 C76 每一对原始耗时做上限估算，即使整段补查与索引收尾耗时全部扣除，其他耗时固定，两次快扫仍各自慢于配对的普通扫描。这说明只改补查不足以扭转该组端到端结果。若继续分析整体提速，建议优先查看请求权限、启动助手和建立快照的开销；若要判断该优化哪类文件，可先在测试工具中按唯一对象记录补查原因，并单独计量队列完成后的索引汇总。多原因对象须保留重叠关系，并行任务的耗时不能简单相加。后续实施范围另行确定。[C77 验收记录](../iterations/2026-11-27-77-ntfs-supplement-audit/README.md)

C74 已在一次性隔离盘上使用该主窗计时入口完成首对对照。使用 `scripts/ntfs-prototype/Invoke-NtfsProductTimingFixture.ps1` 需先确认当轮测试盘和快照范围。主窗使用普通权限，管理员辅助进程持有测试盘。结果指纹一致、快照清理确认并且窗口退出后，才接受耗时比较并清理镜像。[隔离盘方案](ntfs-product-timing-fixture-plan.md) · [C74 实测记录](../iterations/2026-11-27-74-ntfs-product-timing-fixture/README.md)

2026-11-22，C68在原后台计时工具上补齐真实主窗交付与阶段/资源观察。普通启动、扫描策略、统计契约及默认快扫开关不变；计时入口只位于测试apphost，不是正式发行功能。

## 入口与测量范围

先构建测试项目。`tests/bin/Release/net10.0-windows/DiskAtlas.Tests.exe --ntfs-personal-timing-ui`从stdin读取顺序及目录两行；目录不得放入argv、脚本正文或公开归档。原`--ntfs-personal-timing`保留旧后台口径，新入口必须单独命名；两者不能混作同一组。真实目录、VSS/raw读取和预算按本轮明确授权执行，不从工具存在推导永久权限。

新入口每条路线创建真实MainForm，采用当前默认C57/DIB配置和隔离的默认外观、不读取用户设置、不开启磁盘活动/诊断。初始化与图片解码在计时前完成，从主窗发起扫描计时：普通使用自动策略强制完整，快扫复用生产桥接与协调器核验，回退视为失败而不是合格快扫。扫描完成后等待查询就绪，完成现有失效区域的同步最终绘制后停止；没有增加第二次查询或额外Native审计。协调器通过测试注入接入MainForm，DWM实际呈现时刻与连续帧率不在测量内。

原有内存指纹在计时外比较范围、身份、两种大小、根统计和排除项，禁止将不同工作量比较发布为提速。跨时点大小不一致不等于已证明统计错误，也不能证明所有差异来自外部写入。只有一对时仍是诊断；新UI入口不会给稳定提速结论，不自动启动正式组。关闭测量窗口请求取消，等待当前任务退出后关闭；超时/清理不确定停止后续路线，不强杀助手或删除未知快照。

## 阶段与资源的准确含义

- 新增桥接观测点为LaunchStarting、LaunchReturned、Connected、ResultDecoded，仅经受测试apphost校验的ScanForTiming传入，普通生产Scan不启用。无需修改通信版本或发送额外用户数据。
- 阶段是客户端接收固定状态的区间，可聚合重复Reading等阶段。区间不重叠，嵌套bridge/publication指标另列，不相加冒充总时间。最多保留128条详细转换，超出只累计时长/丢弃计数，不使扫描失败。
- LaunchStarting至LaunchReturned包含管理员确认和系统启动，不能精确拆出人工时间。Snapshot不只是纯VSS内核耗时；Cleanup至ResultDecoded仍合并清理、传输和解码，不伪装成独立精确字段。
- 主进程与助手各250ms采样CPU累计、工作集和可取得的进程I/O；记录起止基线和采样最大值，未取得为null。助手窗口从启动返回到结果解码，不包含更早初始化/更晚退出；退出可能使最后工作集不可用。进程I/O包括管道/缓存语义，不代表物理盘吞吐；分进程峰值不能相加当成同时整机峰值。
- 普通的首个预览由100ms UI观察器记录，不代表逐帧精确时刻；快扫尚未提供完成前预览，保持null。前一路紧凑指纹会保留到比较，资源基线明确包含该影响。

## 本轮检查和诊断

`--ntfs-product-timing-tests`使用固定时钟和自有小目录，检查区间覆盖/合并/容量、白名单、借用句柄、两次真实MainForm、同步交付与原错误/清理状态传播；不访问原始卷。原`--ntfs-personal-timing-tests`与bridge/coordinator/wire检查继续保留。

C68已按当轮许可执行快扫和普通各一次，两个结果返回、快照清理确认，无回退；范围身份未观察到增删变化，但大小/占用及根统计可比检查不通过，因而按约定停止性能判定、不追加扫描。C28同快照正确性记录不因此重开。阶段表用于定位范围核验与特殊补查的成本，不能替代等工作量收益证据。

实测时暂停开发侧写盘，stdout先写到被测范围以外；结束并核对清理后才归档。实际时间、资源和比较细节仅在本机ignored artifacts中，公开只保留检查码、布尔与边界，不公开目录、文件清单、身份/指纹、原始记录或真实目录计时。完整证据见[C68](../iterations/2026-11-22-68-ntfs-product-timing/README.md)。

## 计时工具变更检查

- 计时工具修改运行`--ntfs-product-timing-tests`、原`--ntfs-personal-timing-tests`及受影响bridge/coordinator/wire检查。`--ntfs-personal-timing-ui`只由测试apphost从stdin接受顺序/目录，包含真实MainForm查询/同步最终绘制，不测DWM；正式个人实测必须当轮范围授权，先将输出放到被测目录之外并暂停开发侧写盘。不可比/回退/清理不确定即停止结论、不追加扫描；一对不作稳定倍数，实际数字/资源仅本机，公开只保留白名单状态。见[计时工具](ntfs-product-timing.md)。
