# AsukaLens 开发检查点

2026-11-12；最近核对基线 `73c184e6b581a2232529b0480ec784960809b366`（第16轮已提交/推送）。第17轮属于本窗口独立改动，开发验收和清理已完成；恢复核验与上传结果以本轮files.json、归档及Git历史为准。事实入口：docs/project-status.md、docs/development-plan.md、iterations/2026-11-12-17-bounded-mft/README.md。

## 本次目标与授权

用户要求连续开发2小时，窗口10:09:17–12:09:17 UTC。先批准第16轮256MiB新建自有VHD、仅22目标；随后明确另批第17轮有界MFT范围（系统/空闲槽、最多4096槽/4MiB+1024字节/256次读取尝试）。只限此窗口，自有新镜像测试后卸载清理，不个人盘/VSS/新增原始留样/生产接入/发布。不要重复询问已批准范围，不将许可延续到以后。

## 已完成与证据

- 第16轮commit73c184e已推送main，来源适配器/准备期真实归属与无父链、私有token/map、后验和关闭通过；旧22目标模式保持独立。immutable归档 iterations/2026-11-12-16-fixture-source。
- 第17轮已实现 NtfsMftMap.PlanRange、NtfsMftEnumerator、NtfsBoundedMftSource、新固定RawLease profile、独立guard mode factory、Live/wrapper及深白名单。只保留21目标；一个managed页与一个原生64KiB+padding分配复用，decoder有每记录副本，不称全进程内存上限。
- C#构建0warn/error；map199、enum46、rawlease101、bounded65、guard206+旧source63、原NTFS147及rawcomparison回归通过。PS新摘要372、外层模式284，旧guard/source/raw摘要回归通过。独立只读审查无阻塞。
- 真实mft-range473检查通过：256槽/4页/262144有效字节；67 decoded、0 free、6 unsupported、183 invalid；lookup24、unknownAllocated17；21目标全匹配、14目标分配未知。ReadCalls5/RequestedBytes263168（含bootstrap）。来源关闭、解锁、后验、卸载和清理均true。不是格式全支持、全占用、物理I/O或吞吐结论；183项尚未细分原因，不称磁盘损坏/空闲/零占用。
- 旧fixture-source473、raw-records860实际兼容通过，仍22条/21目标/14未知、22次22528请求字节，均清理。一次选定PowerShell宿主不存在导致启动前失败，无VHD创建，已改用发现的可用宿主；不是Native失败。
- 三份Native白名单摘要已按SHA一致复制到第17轮archive。当前无VHD或ntfs-test目录。旧6条kernel样本会话8文件/9683字节保持不变，不上传。三份本轮结果副本已在归档SHA核对后删除。

## 代码归属与当前状态

全部C17改动属于本轮，无其他用户改动。attribute_probe负责Map/Enumerator四文件，special_contract负责RawLease/BoundedSource四文件；均封板并已统一构建。special_tests_plan负责MftEnumerationEvidence/Tests并只读审查共享入口。Root负责guard、Live、Program、wrapper、FixtureCodeEvidence、docs/AGENTS及archive。当前无运行中Native进程、exec或UAC请求。

## 收尾与下一步

第17轮链接/隐私/变化范围已检查；恢复补丁与files.json在收尾验证，按授权显式暂存、fetch核对、提交推送main（不force、不tag/Release）。基线73c184e；补丁不要递归包含自己、manifest或旧archives。仅日常事实写references，不改Skill正文。

下一建议先解释严格分类的未解析原因，增加固定受控错误类别计数与合成矩阵，不放松decoder、不上传原始字节。方案已写docs/ntfs-classification-diagnostics-plan.md，用户已同意在本窗口剩余时间实施；待本轮独立提交后开始第18轮，仍沿用本窗口有界VHD许可；VSS实际来源、4096真实槽、其他几何/系统、碎片化/属性扩展、建树/补查融合及资源基准仍未完成。未接生产，软件高速速度未改变。
