# VSS来源：只读可行性研究

2026-11-12，基线fdc5aa0。本文只整理官方合同与下一步待验内容，没有调用VSS、创建快照、提权或读盘，不构成实施许可。现有读取会话的ValidatedImmutable仍只是模型条件。

## 对当前路线的影响

VSS值得作为稳定来源候选，但先验证生命周期和文件访问，再验证快照设备的原始NTFS读取；不能从“快照文件可访问”推导所有DASD/FSCTL接口可用。本轮建议先完善自有锁定fixture来源适配器，让来源归属与有效期有真实实现，之后再用同一读取边界评估VSS，不把快照设成现有扫描的默认依赖。

## 官方约束

- [创建细节](https://learn.microsoft.com/en-us/windows/win32/vss/shadow-copy-creation-details)与[上下文](https://learn.microsoft.com/en-us/windows/win32/api/vss/ne-vss-vss_snapshot_context)：FILE_SHARE_BACKUP无writer、非持久并自动释放；该上下文不照搬writer备份流程，AddComponent/PrepareForBackup/BackupComplete不适用。
- [来源加入](https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nf-vsbackup-ivssbackupcomponents-addtosnapshotset)与[能力查询](https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nf-vsbackup-ivssbackupcomponents-isvolumesupported)：VHD有嵌套限制，不同时将VHD和其宿主卷加入同一集合。支持查询不是当前创建一定成功的证明。
- [差异区合同](https://learn.microsoft.com/en-us/windows/win32/api/vsmgmt/nf-vsmgmt-ivssdifferentialsoftwaresnapshotmgmt-adddiffarea)：VHD源的差异区必须位于同一VHD卷。该配置方法的最低客户端栏为None supported，不能将主动配置方案当成Windows客户端已验证能力。
- [默认空间规则](https://learn.microsoft.com/en-us/windows/win32/backup/registry-keys-for-backup-and-restore)：未配置全局最小值时，小于500MB卷的初始最低差异区为32MB，大于500MB为320MB。256MiB不能直接判定必败；建议后续另行确认1GiB夹具留余量，成功仍需实测，不为测试修改全局注册表。
- [Requester权限](https://learn.microsoft.com/en-us/windows/win32/vss/security-considerations-for-requestors)：通常需要管理员、Backup Operators或Local System权限及合适COM安全配置。本项目不为绕过权限修改系统VSS访问控制，也不将主窗口默认提权。
- [属性](https://learn.microsoft.com/en-us/windows/win32/api/vss/ns-vss-vss_snapshot_prop)和[访问方式](https://learn.microsoft.com/en-us/windows/win32/vss/requestor-access-to-shadow-copied-data)：仅使用本次创建并返回的snapshot/set/provider身份、原卷和device object绑定来源，不接收任意设备字符串。原始记录、映射及Native对照必须来自同一快照时间点。
- [对象生命周期](https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nl-vsbackup-ivssbackupcomponents)、[删除](https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nf-vsbackup-ivssbackupcomponents-deletesnapshots)和[异步取消](https://learn.microsoft.com/en-us/windows/win32/api/vss/nf-vss-ivssasync-cancel)：一次操作一份owner，先结束读者并关句柄再释放来源；只清理本次拥有的对象。取消可能遇到操作已完成，必须核对是否已生成快照，不能假定没有资源。

## 后续最小实验（待独立确认）

1. 新建自有1GiB NTFS VHD，只生成受控文件；仅将它加入无writer、自动释放的单卷集合，等待异步最终状态。
2. 核对本次快照属性及差异区归属；不添加/缩放/删除主机卷关联，不修改全局设置。先改写自有源文件，验证快照保留原受控内容。
3. 保持COM owner存活，再独立探测快照设备的只读打开、卷数据查询与严格原始记录读取；失败明确停止，不改用live路径混合对照。
4. 覆盖创建/取消/超时/进程退出、快照消失与清理失败，核对仅本次快照已删除后才卸载清理VHD。公开摘要只保留类别/计数/错误码，不输出实际快照、provider、设备或机器标识。

可约束程序不创建、删除或调整主机卷快照及配置；VSS服务、系统日志及VHD文件本身仍涉及系统活动，不能承诺宿主零写入。需要连系统活动也隔离时应在虚拟机验收。当前没有测量创建成本、差异区峰值、端到端速度或Win10/11实际支持，不作秒数或提速承诺。
