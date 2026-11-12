# 当前交接点：第16轮已验收，准备提交；第17轮已获单独确认

2026-11-12，用户要求连续2小时，10:09:17–12:09:17 UTC（本地18:09–20:09）。起点/当前HEAD fdc5aa040c308ae660dd07604f250e39c55ce2d3，main。当前约10:54UTC，仍有约75分钟，不能提前结束。第16轮完整改动未提交；本轮开始工作区干净，所有新改动归此轮。

## 第16轮授权与完成事实

用户明确批准 docs/ntfs-fixture-source-plan.md，允许本2小时按需重复256MiB新建自有VHD，只record0+固定21对象；不个人卷/VSS/新留样/全MFT/发布，许可不延续以后。

- 新 NtfsVerifiedFixtureContext(.cs/Tests)：独立manifest/nonce/严格JSON/固定VHD subtype2+NO_PARENTS/actual extents+MBR分区/geometry/marker守卫，自持新volume/image句柄；固定Case路径由数据根派生。Create严格要求数据根；VerifyPreparation允许尚不存在，仍先核真实归属并关闭自己句柄。最后Stat后取消检查已加，最终pure guard189。
- 新 NtfsFixtureSource(.cs/Tests)：Open(manifest)无caller bool/targets/handle注入，私有owner/map/token；ReadKnown返回绑定envelope，Value/ValidateResult跨source/关闭后拒；不可变副本无法撤回但不作当前证明。RunFixedComparison control固定63/21/3stage，CloseAndVerify固定Native后验、fresh facts、cleanup；Dispose触发cancel后再次检查，主验证错误仍优先。Fake只SyntheticFixture/GuardOwnedfalse，最终source63。
- Root集成Program --ntfs-fixture-source-tests；Live新mode fixture-source，准备阶段先VerifyPreparation（在旧marker/volume访问之前）、旧OpenVirtualDisk仅新mode NO_PARENTS，matrix(retainFailures:false)后关闭旧volume再Open新source。catch只取Guard/Source固定Code，新source失败只输出有界native_error/enum native_operation。
- wrapper新-SourceAdapter互斥三switch，status source_verified、独立source_adapter/source_verified；失败/cleanup失败清摘要，旧raw状态不混用。SourceAdapterEvidence8字段+Comparison复用raw深白名单，Synthetic拒。FixtureCodeEvidence有固定Source/Guard码与新成功None，Native字段边界/错误操作枚举拒任意文本。

## 实际验证（不需无故重跑）

测试Release最终0warn/error；C#189 guard+63 source；PS source evidence137、code/实际wrapper流程248、fixture guards36。原合成147、map174、raw lease84、raw comparison130通过；无无关UI/性能基准。

4次已启动VHD全部卸载清理：source-native.json首次472checks（22strict/21matches/14unknown/22ReadFile/22528bytes）；comparison.json旧1583；raw-compatibility.json旧860(raw true/source false)；source-final.json最终473checks、source_verified，closed/后验/父脚本detach+cleanup全true。一回StartProcess管理员启动被取消未创建VHD，用户后确认后重启成功，单列launch-not-started.json，不算Native失败。当前无运行中exec或挂载VHD。

归档 iterations/2026-11-12-16-fixture-source/ 已有README、validation、4安全结果+launch摘要。4原结果副本已哈希核对后删除；0遗留VHD工作目录，旧6条kernel样本仍1 ignored session。无新原始字节落盘。

## 待收尾与下一授权

Root已更新AGENTS、现状、计划、原型README、脚本README。仍需本轮隐私/链接/白字符、必要文件清单/SHA及基线fdc正向恢复changes.patch、公开Git身份/远程检查后提交推送。不要忘新docs/ntfs-vss-source-research.md（只读官方研究，不实际VSS）及本轮批准方案；不改旧归档。

已准备 docs/ntfs-bounded-mft-plan.md 并发异步问题：是否剩余窗口实施有界MFT遍历并允许同样新VHD从0到VDL最多4096槽（系统/空闲槽，4MiB+bootstrap）。此前只22目标不能自动扩大；用户已回复“按方案实施，允许本轮有界VHD遍历”，第17轮已开始独立分工；读取范围最多4096槽/4MiB+bootstrap仅此窗口自有新VHD。当前先固定第16轮提交，不误暂存第17轮半成品。

第17轮建议若获准：独立新mode/状态/摘要，游标全覆盖，visited=decoded+free+unsupported+invalid，decoded unknown/lookup另计，不汇全占用；只保留21目标对照。新专属256调用/4MiB+1024预算，旧模式2MiB/128不改；ReadInto配一个aligned native缓冲+一个64KiB managed页，decoder另有每记录副本，不预取/不留全MFT。生产/VSS/活跃卷与速度仍未验。各Agent已完成第16轮，下一轮需followup_task重新分工（send_message不触发idle agent）。

恢复先查时间、Git、用户最新回复与文件事实。第15轮/14轮证据仍独立，模型不证明真实来源/覆盖。窗口结束、暂停或交接前同步这里，不用重复测试凑时，不自动发布。