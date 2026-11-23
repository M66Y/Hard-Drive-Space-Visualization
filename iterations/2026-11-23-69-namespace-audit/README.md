# C69 目录范围元信息审查：暂停检查点

2026-11-23，代码基线`2781ebb`，开始与暂停前核对工作区均干净。用户仅要求先审查目录范围核验的重复读取、暂不调整界面，随后明确要求“记录当前项目状态，然后暂停任务”。本文件为未完成审查的交接，不是优化实施或验收完成记录。

## 已完成的只读审查

- 已沿`NtfsPersonalScan → NtfsSnapshotNamespace.Read → NamespaceBackend.OpenDirectory → Resolve → OpenNativeDirectory`核对目录枚举链路。
- `NtfsPersonalSnapshotSource.Resolve`每次从所选快照根重新查询属性，再逐级查询相对路径各段；不同子目录会重复查询共有祖先。深度d的目录有d+1次File.GetAttributes调用，这是静态调用次数推导，不是物理I/O次数或实测时间。特殊大小补查的ReadNativeSize也调用Resolve。
- NativeCursor打开最终目录后查询身份和属性，保证用于枚举的句柄对应预期目录且不是重解析点；上层再次取cursor.Identity/Attributes只是访问缓存字段，不能当成又一次Native读取。
- Source.Create中绑定快照祖先后又通过ReadNativeSize读取所选根，存在常数级重复读取候选，尚未评估是否值得优先处理。
- SizeIndex先按完整FileIdentity归组，再按NeedsNative生成补查任务；已看到硬链接别名去重结构，尚未完成全部补查/并发/异常分支核实，不能宣称该方向全部排除。
- ReparsePolicy及SizeIndex对条目/路径/身份的再次遍历主要是内存校验，不等同于重新读取磁盘元信息。

## 接续位置

重点文件：`src/Scanning/Ntfs/NtfsPersonalSnapshotSource.cs`的Create、NamespaceBackend、ReadNativeSize与Resolve；`NtfsSnapshotNamespace.cs`的Read/NativeCursor；`NtfsSnapshotSizeIndex.cs`的identity归组、pending及worker部分。还需核实快照所有权/生命周期、路径及重解析点边界，区分可复用的祖先验证和必须保留的句柄核验；必要时再用合成调用计数核对，不将次数推导直接换算为提速。

审查完成后先给用户证据与针对性方案，再确认是否实施。不能直接删除路径/句柄校验或用目录枚举的缓存大小代替实际大小；未获新一轮个人卷、VSS/raw或VHD授权，不沿用C68许可。

## 暂停状态

没有修改C#、程序、界面或用户设置，没有构建、测试、个人目录扫描、提权、快照或原始页读取。没有启动仍需等待的审查/测试进程，没有本轮临时产物需要清理。只保存状态与交接文档；C67已接受正常程序、C55及C68本机报告保持。收到用户明确恢复消息后再继续，不自动执行下一步，不打包发布。
