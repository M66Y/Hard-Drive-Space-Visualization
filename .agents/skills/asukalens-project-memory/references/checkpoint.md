# AsukaLens 开发检查点

2026-11-12；第18轮基线 `e01f672ec005c967f51a8afe1d5457777886d533`（第17轮已提交并推送main）。当前第18轮开发/验收和清理完成，恢复补丁/提交绑定以本轮archive和Git为准。事实入口docs/project-status.md、docs/development-plan.md。

## 本次目标及授权时效

用户要求连续开发2小时，10:09:17–12:09:17 UTC。已确认第16轮自有256MiB VHD的22目标；另行明确扩大到第17轮有界MFT有效范围（系统/空闲槽、最多4096槽/4MiB+1024请求字节/256次读取尝试），并允许第18轮固定分类原因诊断沿用本窗口VHD流程。仅自有新镜像，测试后卸载清理；不个人盘/VSS/新增原始留样/生产接入/发布。不反复询问已授权范围，不延续许可到以后。

## 已核实阶段

- C16 `73c184e` 已独立提交推送，固定来源适配器与准备期真实归属/无父链、私有token/map、后验/关闭通过；只限record0+21目标。immutable archive16。
- C17 `e01f672` 已独立提交推送，32文件正向恢复、33条committed SHA已核对。NtfsMftMap.PlanRange、Enum、BoundedSource、新固定Lease profile与guard factory/Live/wrapper完成。真实256槽/4页、21匹配14目标unknown，67decoded/0free/183invalid/6unsupported，5reads263168请求字节；关闭/解锁/后验/卸载清理通过。旧source473/raw860模式兼容仍22条，不用5对22宣称等工作量加速。immutable archive17。
- C18本轮v2已完成：新增AllZeroInvalid/CodeCounts，32种固定Status+Code配对，最多128项，四类小计/visited守恒、排序唯一，未知/注入拒绝。严格decoder/四类/预算不变，关闭后才发布安全聚合。
- C18 build0warn/error；enum91、bounded68、旧guard206+source63通过，PS477及outer284通过。独立只读审查无阻塞。
- C18实际新VHD473通过：256槽=67Decoded+182Invalid/Signature（全零）+1Invalid/SparseMappingFlags+6Unsupported/RecordFlags，Free0，AllZeroInvalid182；21匹配、14目标分配未知，5reads263168，closed/unlock/post/detach/cleanup全部通过。原始记录不保存。只解释拒绝分支，不证明182槽未分配或其他7槽损坏，更不证明格式支持/占用完整/速度。
- 原C17单页managed与64KiB+padding native缓冲复用仍保留，decoder有每记录副本，不称进程内存上限。同步I/O不能瞬时取消；取消/失败不发布部分成功。

## 工作区归属与清理

C18作者封板：special_contract仅Enum/BoundedSource及两Tests；special_tests_plan仅MftEnumerationEvidence/Tests；Root负责docs/AGENTS/记忆及archive18。当前无运行中exec/UAC/Native，无VHD/ntfs-test目录；C18结果已SHA核对归档range-native.json并删除副本。旧6条kernel本机样本会话8文件/9683字节未变，不上传。

C18归档 iterations/2026-11-12-18-mft-classification，基线e01f672；恢复补丁只含本轮必要文件，不递归旧archives或自身。显式暂存、fetch核对再提交main，不force/tag/Release。原生产自动/高速路径未改，v1.5.0仍最后正式发行。

## 下一建议与未完成

Root已写 docs/ntfs-bitmap-model-plan.md 并询问用户是否在剩余窗口做纯合成MFT分配位图模型：最多4096槽/512字节、明确部分覆盖与缺失Unknown、三态声明与四类交叉计数、模拟来源/map绑定；无I/O/VHD/真实BITMAP提取/产品接入。用户已确认实施，Root先完成C18独立提交；C19仅新增模型和测试，不增加真实操作。官方MFT文档区分MFT记录位图与卷簇位图，不能用FSCTL_GET_VOLUME_BITMAP替代记录分配证据。

真实位图提取/格式及绑定、182槽实际分配状态、RecordFlags/SparseMappingFlags支持、4096真实槽、其他系统/几何、碎片化/属性扩展、VSS/活跃卷、完整建树/补查融合及资源收益仍未完成。一次性VHD许可仍在12:09:17UTC终止，Skill正文不改，事实写references。
