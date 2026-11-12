# AsukaLens 开发检查点

2026-11-12；本次验证代码提交 `a3fefba0931b5d2f365f39528cc4011c6151a745`（第20轮已推main）。C20的14文件正向恢复、15条committed SHA通过，五轮manifest均按各自提交复核。当前只做21号接续文档记录，不是新功能实现。事实入口docs/project-status.md和docs/development-plan.md。

## 用户目标与时效

连续开发2小时窗口10:09:17–12:09:17 UTC。C16自有新建256MiB VHD22目标已批；C17另批有界有效范围（系统/空闲槽、最多4096槽/4MiB+1024请求字节/256次尝试），C18固定原因诊断沿用窗口VHD；C19纯合成位图模型、C20纯内存属性定位均另批，不新增真实操作。仅自有新镜像测后卸载清理，不个人盘/VSS/新原始留样/生产或发布。许可不延续到以后；下一轮真实来源方案仍未获实施授权。

## 本窗口完成与证据

- C16 `73c184e` 已推main：真实固定来源owner、准备守卫/无父链、22目标闭环；archive16不改。
- C17 `e01f672` 已推main：32文件正向恢复、33条committed SHA通过。有界分页、私有token/map与独立profile/guard完成；实际256槽/4页、67decoded/0free/183invalid/6unsupported、21匹配14目标unknown，5reads263168请求字节，关闭/后验/卸载清理通过。旧source473/raw860兼容仍22条，不把不同工作量5对22当加速。archive17不改。
- C18 `643c11e` 已推main：19文件正向恢复、20条committed SHA通过；v2 CodeCounts/AllZeroInvalid32固定配对。真实473通过，256槽=67Decoded+182全零Invalid/Signature+1Invalid/SparseMappingFlags+6Unsupported/RecordFlags；21匹配14目标unknown、5reads263168、闭环清理通过。不是182槽未分配/其他7槽损坏/全格式或性能证明。archive18不改。
- C19 `cc4a091` 已推main：14文件正向恢复、15条committed SHA通过。纯合成位图模型最多4096槽/512B，48core+58独立反例及旧enum91/bounded68通过；全部Synthetic，3态声明×4类decode独立，缺失/未覆盖Unknown，padding忽略。模拟对象只比引用，LSB-first不背书真实格式。Count/Memory/indexer伪造异常隐私边界经12注入组合修复并复核。archive19不改。
- C20纯内存定位完成：NtfsMftBitmapAttribute及core/adversarial新增三文件，Program入口 --ntfs-bitmap-attribute-tests。最多1024B record0/4096槽/512B payload；同一私有源交既有Map.Parse严格核验，再恢复私有USA定位。只取unique/unnamed/plain resident；Missing/Partial/Deferred/Invalid分开，nonresident只有3受限长度、无读取地址。既有Map/decoder/fixtures不改，不接model/v2/Native/prod。
- C20构建0warn/error；43core+60独立反例通过，旧Map199/enum91/model48+58通过。跨510尾字512B payload、raw damaged/mixed/restored拒绝、512/513上限、copy双向隔离、Deferred/畸形/取消均过，独立只读审查无阻塞。所有Synthetic；ResidentComplete只表示容量min(expectedSlots,payloadBytes*8)，不验证bit值/位序/来源/分配。payload不自动序列化，CopyPayload再复制。

## 当前归属与清理

C20作者封板：attribute_probe locator+core、special_contract独立反例、special_tests_plan只读复核；Root Program/docs/AGENTS/记忆与archive20。本轮仅纯内存，不需VHD或临时目录；当前无运行中exec/UAC/Native，无VHD/ntfs-test目录。本窗口Native结果副本均SHA核对归档后删除；旧kernel六样本会话8文件/9683B未变，不上传。

C20归档 iterations/2026-11-12-20-bitmap-attribute 已封板推送。最后doc-only记录 iterations/2026-11-12-21-bitmap-source-design 基线a3fefba，只细化Native来源方案与记忆；不改代码或重跑测试，文档恢复/隐私检查后提交，不force/tag/Release。正常开发构建保留，无新预览目录/发行包。原自动/高速路径未改，v1.5.0仍最后正式发行。

## 下一轮已备方案但未实施

见 docs/ntfs-bitmap-source-plan.md：另获本轮许可后，仅新建自有VHD复用同owner已有bootstrap观察BITMAP形态；独立mode/factory/revision与字段白名单，21目标及关闭/后验/卸载清理仍需通过，不增加非驻留块读。真实来源与位图解释分开，ResidentCapacityOnly/Missing/Partial/Deferred不生成VerifiedAllocation；实际非驻留到Deferred即停，不临时扩大预算。当前没有该轮实施授权或新VHD许可。两个独立只读评估建议B：既有BoundedSource的私有固定profile/专用factory+Run共享同一执行核心，不复制owner或在Live重开；因为现RunEnumeration已CloseAndVerify→Dispose，不能运行后补观察。推荐尚待下轮选择，触点/风险和独立摘要合同已在方案中细化。

非驻留run映射/块读取、真实位序/释放槽对照、同来源位图解释/后验、182槽实际分配、RecordFlags/SparseMappingFlags支持、4096真实槽/其他系统几何、VSS/活跃卷、完整建树/补查融合/资源基准和产品入口仍未完成。下一步不把纯模型/定位器改标签当Native证明，不把记录位图与卷簇位图混用。窗口结束后等待下一次明确任务；Skill正文不改，事实写references。
