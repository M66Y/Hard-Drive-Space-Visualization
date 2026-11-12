# 本机验证与限制

测试程序集以 .NET SDK 10.0.403 从基线 `5209462` 加本轮工作区构建，最终零警告/零错误。程序集 SHA-256：`651e52caf631ecc42b8206af33c9861e67fa0506efcfcd210abe49ea5141a929`。此哈希用于辨认本次测试二进制，不是正式发行证明。

## 对应检查

| 检查 | 结果 |
| --- | --- |
| `--ntfs-scale-tests` | prefix 12、lease 7、collector 8、index 90、orchestration 29；共 146 |
| `--ntfs-vss-tests` | probe 60、guard 20；共 80 |
| 旧 fast / prefix / raw lease | 12+43 / 94 / 108 |
| 旧 fixture guard / source | 314 / 63 |
| `NtfsScaleEvidence.Tests.ps1` | 102 |
| `NtfsVssFixtureEvidence.Tests.ps1` | 372 |
| `NtfsFixtureGuards.Tests.ps1` 及 `-Scale` | 各 36 |
| `NtfsValidationDriver.Tests.ps1` | 84 |

核心边界包括 262,144 槽完整读取与超限预拒、32 KiB 位图多 run/负 LCN/不读取的坏尾仍拒、名称/字符独立容量、短读/取消/释放、旧 profile 不借用新预算、线性索引/硬链接/未知大小/对象身份/异常净化；VSS 按本机 SDK 核 x64 ABI、按值 GUID、实际 Cleanup 的 fake vtable、未知异步状态不准清理、精确本次 ID 删除及先确认不存在再释放。替身通过不代替实际能力：真实正常闭环另见三个 native JSON。

## 计时口径

每种数据量单独创建新 1 GiB 固定 VHD；100 目录，10% 非空驻留、80% 普通非驻留、10% 空文件，10 个额外硬链接入口。两算法各预热一次，然后 MFT/目录、目录/MFT、MFT/目录三对；创建和预热不算扫描用时。八路现有扫描器作基线。两方每次均重新建立来源，并输出完整 `DiskEntry` 树，包含统计汇总；MFT 还包含 Native 前/后补查、锁卷/映射/位图读取与关闭。两方的逐项统计指纹在计时外验证，包含名称、类型、完整身份、逻辑大小、对象分配及根总量；不强制硬链接入口的分配归账位置相同。

一万 raw 每次 162 次请求 / 10,488,320 字节 / 1,000 对象各前后补查；十万 1,573 次 / 102,774,272 字节 / 10,000 对象各前后补查。这些是逻辑读取尝试和完整 NativeStorage 回调计数，不是物理 I/O 或单个 Win32 调用。

十万 MFT 托管总分配约 391 MB / 次，目录约 63 MB / 次；这不是峰值内存。采样到的进程工作集约 MFT 224–266 MB、目录 171–233 MB，受前轮对象、GC、页回收及采样遗漏影响，不能将差额当独立阶段开销。采样使用专用线程，避免额外占用目录扫描 ThreadPool worker；20 ms 名义间隔并非所有相邻采样都精确 20 ms。

缓存没有强制清空或模拟冷盘；保留目录连续运行的快速样本。微软说明 [FSCTL_LOCK_VOLUME](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume) 会冲刷缓存，NTFS 把锁定卷当作卸载状态。因而交替顺序也不能消除该来源产生的缓存混杂；当前数据足以否定“此原型已稳定提速”的说法，不支持普遍性能排名或倍数。

## 实际 VSS 与清理

`vss-native.json` 为另新建卷、一个短时无 writer 自动释放快照，3 个固定自建文件。属性绑定本次 snapshot/set/system provider/原卷；在同一快照内取 Native、少量受控内容及严格 raw 记录。live 文件改写并扩长后，snapshot 保留原内容/大小。读者关闭后，只删除本次 snapshot，确认其不存在并释放 owner，然后卸载并删除 VHD。未调用个人卷、VSS 全局配置或宿主差异区配置。

失败/取消/缺失或畸形摘要、清理未确认时不准强制卸载；异常恢复标识仅留本机 ignored 工作区。真实故障、取消、崩溃恢复目前主要是替身和守卫测试，正常 VSS 通过不代表这些实际故障均已验。新路径 Windows 10、其它格式/几何、个人卷及产品 UI 仍待后续验证。

本轮四份结果副本 SHA 核对后归档，三个最终 VHD 及最初失败对应 VHD 全部清理；未成功的管理员启动没有创建镜像。普通测试构建保留，无新预览/安装器/Release。后续修改与实际预检不纳入本轮证据。
