# AsukaLens 开发检查点

2026-11-12；合并迭代C22基线 `99fb9980d64fc5e83580b99aaeba3b7e18514af4`。实现、必要回归、独立审查、三次真实VHD观察/端到端与清理已完成；恢复/提交绑定以archive22及Git为准。工作区全部为同一用户任务，不拆多个小轮，不打包/标签/Release。正式版仍v1.5.0。

## 用户目标和授权

先30–60分钟真实BITMAP观察（12:08:48UTC开始），用户随后要求加快并确认接下来两小时集中NTFS尽量连通流程；新窗口12:21:51–14:21:51 UTC。用户明确批准 docs/ntfs-fixture-fast-scan-plan.md：本时段按需新建256MiB隔离VHD，仍走归属/格式化守卫；位图额外对齐读取最多512字节，总raw<=4195840字节/256次，MFT<=4096槽；在自有盘内CREATE_NEW/Delete最多2文件核验释放槽。测后卸载清理，不个人盘/VSS/新原始留样/生产或发布，许可不延续。

## 本轮完成与证据

- 独立形态模式OpenBitmapObservation/RunBitmapObservation只复用同owner bootstrap；旧RangeOnly不执行locator，旧v2/预算不改。首次Native473闭环通过但generic BitmapPayloadBudget不足判断形态，记录initial-observation.json，不当最终功能通过。
- locator内部Form保留旧code/revision及不序列化，Source只公开两固定预算码。修后Native473确认非驻留超512，5reads/263168，21对象及关闭/清理通过，observed-shape.json。中途一次UAC启动取消未建VHD，用户明确重试后成功。
- 端到端OpenFastScan/RunFastScan真实473通过，fast-scan-native.json：21目录/21对象/27入口逐项身份/逻辑/分配均和原DiskScanner一致；逻辑21233664、分配917504字节。7对象分配来自记录、14经前后验证Native补值，DOS忽略33。prefix非驻留32有效字节/512对齐请求，21在用+2已释放严格Free槽通过；总6raw读取尝试/263680请求，关闭/后验/卸载清理true。
- 新固定Lease profile4195840/256，旧2MiB和4MiB+1024保留；Prefix最多1MiB元数据/128runs，完整验证signed LCN/覆盖/EOF/init/物理边界/自重叠/MFT DATA交叉，最终只出单512slice。地址不出报告，原始正文不落盘。
- Witness固定nonce目录CREATE_NEW，两文件同时存在、同句柄读取完整Native身份和删除；目录pin/文件句柄锁前全部释放。旧seq1..65534，free期望同slot+seq+1，官方FILE_RECORD_SEGMENT_HEADER说明free时递增；不猜wrap、不用旧ID等值。真实两槽通过。
- Collector单页/4096records/16384names/1048576 UTF-16字符，旧enum仍只保21。索引只FixedFixtureDataRoot，父链/slot/硬链接/DOS/未知与Native来源分开；逐alias不固定AllocatedSize归账，分配以对象去重和root总量对照。
- 最终唯一build0warn/0error；C#：collector12、fast source43、prefix94、index96、lease108、oldbounded68、shape113、guard309+oldsource63，全过无跳过。PS：fast312、shape319、外层/语义420、oldv2 477、隔离guard36过。独立Source/witness及PS/模式只读审查无阻塞，最后文档只读核查收尾。

## 文件归属与清理

attribute_probe：prefix/lease四文件与小locator Form/Tests；special_contract：Source/Tests、Guard/Tests、collector/Tests；special_tests_plan：Index/Tests、BitmapBootstrapEvidence/Tests；Root：Program/Live/wrapper/代码白名单及Tests、FastEvidence/Tests、docs/AGENTS/记忆与archive。全部已封板，当前无编译/Native/UAC/exec运行。

归档 iterations/2026-11-12-22-ntfs-fast-scan，基线99fb998。3个VHD均卸载且目录清理，全部本轮结果副本均SHA核对归档后删除；旧kernel六样本会话8文件/9683B未动不上传。正常测试构建保留，无新预览目录/发行产物。下一步仅必要恢复patch/SHA/隐私/远端核对与统一commit/push main，不force。

## 报告和交付含义

fixture-fast-scan-v1 / ControlledFixtureCompared七根：Range旧v2扣prefix、Bitmap9字段21pos+2free、Index15字段、Comparison11字段、Resources3字段totals=range+bitmap且Closed。Native标签由真实factory建立，pure/fake仍Synthetic，PS只运输校验不认证物理来源。旧各mode成功不互相别名，失败/取消/清理失败清空成功证据。不直接序列化index entries/tree/ref/token/LCN/run/payload或任意异常消息。

可运行开发入口：pwsh管理员 scripts/ntfs-prototype/Invoke-NtfsFixture.ps1 -ResultPath <fresh artifacts result> -FastScan，仅当前轮明确VHD许可；纯CLI --ntfs-fixture-fast-scan-tests/--ntfs-bitmap-prefix-tests/--ntfs-fixture-index-tests。见scripts/prototype README。不把这当普通用户个人盘扫描入口。

## 未完成和接续

仅静态自有固定夹具对照完整；取消/资源故障主要fake，真实只正常闭环。21正例+2释放槽不证明所有槽/182全零分配均独立验证；其余invalid/unsupported不忽略，不汇总全卷。14 Native补值不是纯MFT收益；raw次数不是物理I/O。没有吞吐、阶段耗时、CPU/峰值内存基准，不能宣称比原Scanner或WizTree更快。

更大受控数据/紧凑索引与端到端资源基准、其它格式/几何/Windows10新路径、活跃卷/VSS一致性、正式权限助手/可靠回退/主窗接入仍未完成。下一块合并安排，不再把内部helper单独建轮；权限/数据/预算变化才询问，已有授权不重复问。当前范围达到验收后收尾，不为凑时间继续加旁支。Skill正文不改，日常事实写references。
