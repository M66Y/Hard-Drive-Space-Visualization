# 自有测试盘MFT有效范围的有界分批遍历

2026-11-12，基线 `73c184e6b581a2232529b0480ec784960809b366`。用户在本2小时窗口内另行确认[有界方案](../../docs/ntfs-bounded-mft-plan.md)，明确允许新建256MiB隔离VHD遍历其MFT有效范围（系统及空闲槽，最多4096槽/4MiB+bootstrap），不沿用第16轮仅22条的权限解释。不个人卷/VSS/原始留样/生产接入或发行，许可只限本窗口。

## 范围与状态

本轮已完成独立mft-range/range_classified实现、相称回归和本机隔离VHD验收；旧模式预算与成功含义保留。完整cursor覆盖不代表全部格式支持或占用统计完整；四类decoded/free/unsupported/invalid分别计数，未知值与补查理由保留。不汇总全卷字节，不把严格拒绝当空闲、零占用或磁盘损坏。

PlanRange按受限映射生成<=64KiB窗口；Enumerator复用一个managed页并只留最多21目标记录；Native source用固定新增256调用/4MiB+1024预算与ReadInto复用aligned缓冲，旧Acquire/Read仍2MiB/128。源归属/prepare guard与模式工厂分开，不能通过caller arbitrary limits、source bool或旧factory静默扩大范围。

固定21目标必须来自此次范围并通过完整引用/序列、名称/父关系、链接/大小及关闭后的固定Native核验；任一目标free/unsupported/invalid/缺失均不发布成功结果。非目标严格拒绝可与范围分类完整同时出现，结果如实保留计数，不输出目标路径/引用、源token或原始数据。

## 关键文件与决定

- `NtfsMftMap` / `NtfsMftEnumerator`：纯映射和分类，顺序cursor必须覆盖全部受限有效长度，超过4096槽前置拒绝，不返回部分成功。
- `NtfsRawVolumeLease` / `NtfsBoundedMftSource`：新固定profile与旧profile隔离；一个managed页、一个原生缓冲加对齐填充复用，decoder仍有每记录副本。上述数值不是进程峰值内存。
- `NtfsVerifiedFixtureContext` / `NtfsLivePrototype`：准备阶段先验证真实卷归属，新工厂只接受mft-range，不把旧fixture-source许可扩大。目标对照成功后，还须解锁、Native后验、fresh facts和backend关闭。
- `MftEnumerationEvidence.ps1` / 外层脚本：深白名单、四类计数等式、固定21对象、Native来源与关闭状态分别校验；旧成功状态不作新模式别名。

理由是先验证范围读取及结果含义，再考虑复杂格式、建树和真实资源收益；不通过放宽decoder或忽略未知值制造完整成功。

## 验证证据

测试项目Release构建0警告、0错误。合成/替身检查通过：map199、enumerator46、raw lease101、bounded source65、guard206与旧source63；原NTFS147及raw comparison回归通过。新深摘要372、外层模式/语义白名单284通过，fixture guards、旧source摘要和旧raw摘要回归通过。详见[validation.json](validation.json)。未改生产扫描逻辑，不扩大为UI或性能验收。

本机Windows 11 / NTFS 3.1、512字节逻辑和物理扇区、4096字节簇、1024字节记录，依次新建独立256MiB VHD：

| 模式 | 检查 | 真实结果 | 关闭与清理 |
| --- | --- | --- | --- |
| mft-range | 473 | 256槽/262144有效字节、4页；67 decoded、0 free、6 unsupported、183 invalid；21目标匹配、14目标分配未知；5次ReadFile请求263168字节，含bootstrap | 来源关闭/后验、解锁、卸载、清理均通过 |
| fixture-source兼容 | 473 | 原22条严格解码、21对象匹配、14特殊分配未知；22次/22528请求字节 | 关闭、后验、卸载、清理通过 |
| raw-records兼容 | 860 | 原22条严格解码、21对象匹配、14特殊分配未知；22次/22528请求字节 | 解锁、后验、卸载、清理通过 |

本轮另有一次旧模式复验启动在创建VHD之前因选定PowerShell路径不存在而失败，修正为已发现的可用宿主后成功；这不是卷读取失败，不并入Native通过计数。见[launch-not-started.json](launch-not-started.json)。读取次数与请求字节不是物理I/O，不是同工作量性能对比；4页范围比22目标工作量不同，不能用5对22宣称加速。

## 健壮性审查与限制

合成覆盖4096槽/64页、4097槽拒绝、页尾/EOF/跨run/负LCN、缓冲复用、四类与未知值、目标缺失/序列变化、预算、新旧模式隔离、短读、第二页原生失败、取消和释放。第二页失败不发布先前页，保留受控原生类别/错误码；取消检查贯穿关闭，已有主要错误不被清理时取消覆盖。同步Native调用不能立即中断，复制后才取消时私有页可能已改变，但source不发布该页或成功摘要。

独立只读审查未发现阻塞；公开摘要不包含路径、目标引用、卷/设备身份、token、原始数据或任意异常文本。非目标183 invalid与6 unsupported仅反映当前严格decoder的分类，尚未细分原因；不解释为空闲、零占用或磁盘损坏。14目标分配未知保留；本轮没有全卷占用汇总。

真实来源仍仅此静态小测试盘；4096真实槽、其他系统/几何、碎片化/属性扩展、VSS/活跃卷、全量用户盘吞吐、完整建树与生产回退仍未验。生产自动/高速策略未改变。本轮不打包、不创建标签或Release。

## 归档与清理

`range-native.json`、`source-compatibility.json`、`raw-compatibility.json`仅保存白名单结果；原始记录不落盘、不新增样本。此前6条kernel原样本的本机8文件/9683字节会话保留不变。三个自有VHD均卸载且工作目录已清理；结果副本在归档SHA-256核对后清理。`changes.patch`以本轮基线正向恢复并逐文件核对，`files.json`记录必要文件SHA-256（不递归包含自身），随本轮提交上传。具体提交与推送结果以Git历史为准。
