# 自有测试卷来源适配器

2026-11-12，基线 `fdc5aa040c308ae660dd07604f250e39c55ce2d3`。用户要求连续2小时（本地18:09至20:09）推进后续开发，并确认[第16轮方案](../../docs/ntfs-fixture-source-plan.md)，允许本窗口内按需重复新建256MiB隔离VHD、有限读取后解锁/卸载清理。起始工作区干净，不沿用旧轮授权，不操作个人卷、不创建VSS快照、不新增原始留样、全MFT或发行。

## 问题与取舍

前一轮只是纯合成会话，不能把调用方布尔值当真实来源。旧raw对照虽然成功，但卷句柄、映射和时效由调用方组合。本轮新增有实际守卫与生命周期的测试专用owner，固定record0+既有21对象，保持128次ReadFile/2MiB/单次64KiB预算和14个特殊分配未知；不将实际名字塞NameToken模型冒充真实树。

真实factory从固定manifest/nonce数据根重新验证镜像、卷extents和Native上下文，自己持有句柄；Live先关闭准备阶段旧句柄再打开新来源。token/map绑定owner，单条结果以绑定envelope返回，关闭后不能再作为当前结果解包。最终对照与普通Dispose分开，只有关闭/后置校验及父脚本清理均通过才导出source_verified；旧verified/collected含义保留。

## 当前实施与证据

来源实现与最终真实复验均已完成。测试Release构建0警告0错误；来源入口189项guard+63项source通过；摘要137项、实际wrapper语义码/模式/有界Native错误字段248项、原隔离守卫36项通过。原合成147、MFT map174、raw lease84、raw对照130也通过，未跑无关UI/性能基准。

关键文件是新NtfsVerifiedFixtureContext/Tests、NtfsFixtureSource/Tests、SourceAdapterEvidence及测试，Root集成Live/Program/fixture脚本/语义码；入口与固定限制同步AGENTS/原型README。独立VSS研究见[来源说明](../../docs/ntfs-vss-source-research.md)，仅官方文档调查，不是已实现后端或快照许可。

## 实际VHD观察与健壮性审查

- source-native.json：首次新来源对照source_verified，基础runner检查472；22严格解码、21对象匹配、14特殊分配未知，22次ReadFile/22528请求字节。source关闭/后置核验、父脚本卸载与清理全部完成。后续又收紧了准备入口的守卫顺序，不能只以这份早期证据替代最终代码复验。
- source-final.json：准备期守卫顺序及最后取消检查收紧后的最终代码，source_verified、473项基础runner检查；22/21/14及来源关闭、后置核验、卸载清理均通过。一次管理员启动返回取消且未生成VHD，随后用户确认后重新启动成功；这不是Native校验失败。
- comparison.json：旧普通路径1583项通过并卸载清理。
- raw-compatibility.json：旧RawRecords 860项通过，raw_verified=true且source_verified=false，旧状态含义未被新模式混用；卸载清理完成。

审查修复关闭期间取消漏检，保留已有主验证错误；最终factory返回前也检查取消。来源结果使用绑定envelope，不能将已取出的不可变副本重新当作当前来源证明。纯假实现固定SyntheticFixture，真实白名单不接受。新增VHD Fixed subtype=2与NO_PARENTS核验、独立MBR/分区/几何重核；新模式准备期也必须先守卫，再触碰旧准备路径的marker/volume，避免验证顺序倒置。准备入口仅允许派生数据根尚未创建，不返回authority或降低完整Create的约束。

仅数值native_error与固定枚举native_operation可从新来源失败摘要传出，缺失/类型不符/任意文本或成功携带失败字段均拒绝；不转发异常Message、堆栈或源标识。假摘要只验证传输字段，不能独立证明真实来源。

## 收尾与边界

后续实际源生命周期、全MFT覆盖、批量读取/建树、活跃卷与VSS、整盘速度及生产入口仍未完成。本轮的有限静态夹具成功不能作为这些结论。旧6条kernel失败样本仍保持原本机范围，不公开原始记录、引用、卷/设备/机器标识或本机路径。

本轮必要代码、测试及脱敏摘要与SHA-256/基线恢复补丁统一留在本目录，不递归包含补丁自身或旧归档。四次已启动VHD均已卸载删除；原始数据只在内存，旧6条kernel样本保持原本机范围。结果副本核对归档后清理，正常测试构建保留用于复现。实际基线正向恢复并核对暂存字节、隐私/链接/白字符检查后提交main。普通构建不打包、不创建标签或Release。

用户随后另行确认[有界MFT方案](../../docs/ntfs-bounded-mft-plan.md)，将由第17轮独立实施；不将其4096槽/新预算并入本轮22目标的验收。
