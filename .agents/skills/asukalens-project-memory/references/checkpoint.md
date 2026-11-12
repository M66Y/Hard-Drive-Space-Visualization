# AsukaLens 开发检查点

2026-11-12；第19轮基线 `643c11ed7c31de8ad4246cfbf72e5d0537fccdf4`（第18轮已独立提交并推送main）。C19开发/相称检查/审查完成，归档恢复与提交绑定见本轮files.json及Git。事实入口docs/project-status.md、docs/development-plan.md。

## 用户目标及授权

连续开发2小时窗口10:09:17–12:09:17 UTC。C16新建256MiB自有VHD22目标已批；C17另批有界MFT有效范围（系统/空闲槽，最多4096槽/4MiB+1024/256次尝试），C18固定分类诊断沿用窗口VHD许可；C19另批纯合成位图模型，不新增真实操作。仅自有新镜像测后卸载清理，不个人盘/VSS/新原始留样/产品或发布。许可不延续到以后；C19不需要VHD。

## 本窗口已完成

- C16 `73c184e` 已推main，固定来源owner/归属/无父链/准备守卫及22目标闭环，archive16不改。
- C17 `e01f672` 已推main，32文件正向恢复与33条committed SHA通过；有界分页/新固定lease profile/私有token-map/独立mode完成。真实256槽/4页、21匹配14目标unknown，5reads263168请求字节，67decoded/0free/183invalid/6unsupported；关闭/后验/解锁/卸载清理通过。旧source473/raw860兼容仍22条，不用不同工作量5对22宣称加速。archive17不改。
- C18 `643c11e` 已推main，19文件正向恢复与20条committed SHA通过。v2 CodeCounts/AllZeroInvalid32固定配对诊断完成。真实473检查，256槽=67Decoded+182全零Invalid/Signature+1Invalid/SparseMappingFlags+6Unsupported/RecordFlags。21匹配14目标unknown、5reads263168、闭环清理通过。不是182槽未分配/其他7槽损坏/格式全支持/完整占用或性能证明。archive18不改。
- C19新增 NtfsSyntheticBitmapModel/核心Tests/独立AdversarialTests及Program入口 --ntfs-bitmap-model-tests；最多4096槽/512B，模拟SourceToken/MapEpoch只比对象引用，全部报告Synthetic。三态位图声明与四类解码12交叉格独立；缺失/未覆盖Unknown，Provided截断/超量拒绝、padding忽略，完整唯一观察不可遗漏。
- C19构建0warn/error；48core+58独立反例过；旧enum91/bounded68过。AllZero仅Invalid且header=null；Decoded/Free提供header时须自洽，其他header只是模拟观察，冲突只计数不修复/不判断损坏。模型LSB-first不证明真实NTFS位序。
- C19审查修复外部Count/Memory/indexer抛伪造OCE/ModelException绕过净化。ReadExternal捕任何外部异常，先传入token取消，否则新固定BitmapModelInput，无原Message/Code/Inner；12注入组合过，最终独立只读复核无阻塞。无模型文件/卷I/O，不接v2/Native/生产统计。

## 当前工作与清理

C19作者封板：attribute_probe新增Model+coreTests，special_contract独立反例，special_tests_plan只读复核；Root负责Program/docs/AGENTS/记忆与archive19。当前无运行中exec/UAC/Native，无VHD或ntfs-test目录，无本轮临时目录。C18结果已SHA归档后删除副本，旧kernel六条样本会话8文件/9683字节不变、不上传。

C19归档 iterations/2026-11-12-19-bitmap-model，基线643c11e；显式stage本轮必要文件，patch不递归旧archives/自身，fetch检查后commit/push main，不force/tag/Release。原自动/高速扫描未改变，v1.5.0仍最后正式发行。

## 下一步边界

真实MFT记录BITMAP提取/位序/驻留和非驻留映射、同持锁来源绑定/后验、182槽实际分配、RecordFlags/SparseMappingFlags支持、4096真实槽/其他几何系统、VSS/活跃卷、完整建树和资源收益仍未实现。MFT记录位图与卷簇位图不能混用。

Root在评估“受限BITMAP属性定位/提取纯原型”的下一轮最小范围，用户已确认docs/ntfs-bitmap-attribute-plan.md纯内存范围，C19独立提交后实施第20轮；不把普通继续/过去VHD许可扩展为真实位图读取，不为时限放宽解析。用户窗口12:09:17UTC结束；新的真实操作需有效范围授权。Skill正文不改，事实写references。
