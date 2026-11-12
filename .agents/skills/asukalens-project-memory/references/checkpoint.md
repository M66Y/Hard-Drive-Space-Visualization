# AsukaLens 开发检查点

2026-11-12；C23基线5209462cb2f2b4aa9118415bd3b1671730f775e8。C23规模/自有VSS实现与真实验收已完成，提交绑定以archive23/Git为准。用户同一请求继续C24；新工作区文件不混入C23补丁。正式版仍v1.5.0，不打包/Release。

## 用户目标与当前授权

用户要求规模/实际速度，并尽快个人卷与主窗。C23批准：新1GiB owned VHD10k/100k，262144槽/raw256MiB+64KiB/8192calls；另新owned1GiB VSS小探针，不host配置/个人卷。C23三项最终合并Native全过并清理。

用户随后已批准 docs/ntfs-scale-and-product-plan.md 最后一节下一合并块：同snapshot完整namespace、compact sizes、bounded parallel Native，允许本轮新1GiB VHD组合10k/100k VSS对照，同原预算；正确性/收益成立再接普通权限主窗实验入口/短命助手/可靠fallback。另指定个人卷只查FS/能力/MFT长度，**不读其records/names/content、不建个人snapshot、具体结果不入repo**。盘符以最新用户答复为准，不推断其它卷。当前没有指定时长，不沿用旧14:21/两小时窗口。

## C23结果

最终Release测试DLL0warn0error，SHA256 651e52caf631ecc42b8206af33c9861e67fa0506efcfcd210abe49ea5141a929，SDK10.0.403；冻结binary后无再build。Scale146(12prefix/7lease/8collector/90index/29orchestration)，VSS80(60probe/20guard)；旧fast12+43/prefix94/lease108/Guard314+source63。PS Scale102、VSS372、driver84、old/Scale guards各36全过。独审无剩余阻塞。

10k/100k最终真实各6次fingerprint一致，10000/100000对象、10010/100010入口、100目录，逻辑8234240/82250240、分配32800000/328000000、unknown0。MFT中位1079.4705/10309.1579ms；八路Directory913.0836/9148.8443ms。没有提速；Directory连续warm样本53.8965/606.2948ms保留，不能只挑慢样本。锁卷影响缓存(官方flush并NTFS视dismount)，未量化各因素；不等未来VSS或WizTree结果。

VSS最终vss_verified，3files/4strictRawRecords，4raw calls/4096bytes；live改写后snapshot Native/32bytes内容/raw均保持原值；本次snapshot创建删除、同source属性/geometry、读者关闭、卸载清理全true。所有本轮owned VHD dirs0；无Native/UAC/exec仍运行。

最初10k runner0但PS硬断言resident0拒摘要，已存initial-transport-failure.json不当通过；已核历史Native logical32=>alloc32。PS改实测有界并补非0case。中途管理员启动not-started，用户未看到提示；未建盘。用户明确retry后一次admin driver三项全过。

archive iterations/2026-11-12-23-ntfs-scale-and-source 已有README/validation/5JSON，待root恢复patch/manifest/cleanup/统一commit push。native副本仍artifacts中待核hash后删。VSSroutes已恢复，临时ntfs-vss-routes.tmp已删。旧kernel六样本8files9683B仅ignored本机保留不动。

## 当前C24分工/未验收文件

Root收尾C23并负责C24共享源码迁移/程序集接入、VHD+VSS规模driver、mainUI/IPC条件组装。暂不build，等C23commit。三agent只新增C24文件，不动C23冻结文件，不实际query/Native。

- attribute_probe新增 NtfsVolumePreflight.cs/Tests：Run(selectedRoot)只本地盘符根，GetVolumeInformationW无label/serial输出，access0 CreateFile+0x90064最多128B，不ReadFile。fake parser；未实际预检。随后负责提取VssOwner/Reader共享代码，等root边界放行。
- special_tests_plan新增 NtfsSnapshotNamespace.cs/Tests：GetFileInformationByHandleEx19/20，无BytesReturned；清零64KiB页面用NextEntryOffset/UTF16len闭合，error18结束；BFS root0/entry parent/name/fullID/attrs，handle identity/pin、重复页/name/cycle/预算；不信cached size/不mint真实source；reparse当前whole fallback。
- special_contract新增 NtfsSnapshotSizeIndex.cs/Tests：消费完整inventory，只retain选中ID sizes；严格全MFT decode不保全卷name；同snapshot单次Native补，8worker/queue128/32768对象，ID/EOF/link/dir校验。mapUnsupported丢整个attempt，不硬跳系统槽。三方正在锁API，无联编验收，仍唯一builder待root通知。

## 必须保留的正确性/下一步

CompleteWithinObservedRecords不能证明个人scope，任何在用无法解析且不能积极排除的记录都PotentiallyInScope。当前MainForm返回任意ScanResult即覆盖旧完整，Live.Attach只一次。产品快路需要namespace完整入口authority，缺页/unknown/reparse/引用/预算失败则清理后独立session常规fallback，用户取消不自动重扫，cleanup未确认不启动下后端。snapshot结果不复用live journal checkpoint；UI定位原路径须核当前identity。

当前生产仍普通遍历，个人快扫/主UI/IPC/Win10新路径未实现。不能因VSS小探针过就宣称个人磁盘支持。C24真实preflight结果不放记忆/仓库；个人snapshot未授权。完成当前已批准范围和必要检查后收尾，不为每helper拆轮/审批。
