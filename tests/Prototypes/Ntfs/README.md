# NTFS 合成记录原型

本目录仅编入测试程序，不进入软件程序集、扫描策略或 UI。默认合成检查不访问磁盘。2026-11-12 新增受控 VHD 比对入口，仅用于管理员运行的隔离测试：它只读取本轮合成文件的对应记录，不枚举个人磁盘或整卷 MFT。使用与边界见[测试盘说明](../../../scripts/ntfs-prototype/README.md)。

构建测试项目后运行：

```powershell
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --ntfs-prototype-tests
```

## 输入与输出

- `NtfsRecordDecoder`：限定 1–64 KiB 记录、512 字节 fixup stride、显式卷格式版本和记录槽号；簇大小合成默认 4096 字节，支持 512–65536 的二次幂。真实测试入口传入经核对的卷几何和外部槽号。拒绝未知版本、未知属性类型及无法验证的布局。
- 原始字节经私有副本完成 USA 修复与属性解析，调用方输入不变；输出区分有效、空闲、损坏和不支持，错误码不包含名称或路径。
- 普通未命名 DATA 的逻辑/分配长度从数据属性读取，并对非驻留数据的 runlist、VCN、LCN 和普通分配簇几何进行核对；不使用 FILE_NAME 中的大小副本。真实 VHD 比对发现非空驻留 DATA 的分配大小不能按独立数据簇的 0 值对齐 NativeStorage，已改为 `ResidentAllocation` 待补查；空 DATA 保留经本轮夹具验证的 0 值。
- `NtfsPrototypeIndex`：按卷身份、完整文件引用识别对象，保留多个目录入口并排除 DOS 别名；逻辑大小按入口累加，实际占用按对象去重，对齐现有扫描器的基本口径。它只提供合成输入的内存汇总，不是整卷快照或持久缓存。
- 索引默认上限 10 万条记录（显式调整上限最多 100 万）、20 万个名称入口、800 万个名称字符；每记录最多 256 个名称，每名称最多 255 个 UTF-16 单元。超限拒绝，不悄悄截断后声称完整。

## 保守处理与未实现内容

属性列表和扩展记录尚未拼接，标为待补查并保持大小未知；压缩/稀疏分配空间保持未知，不把 DATA 的表面长度套作实际占用。重解析/云占位、加密、缺失默认数据或标准信息也要求补查。命名流不并入默认流大小，并保留范围提示。这里的“待补查”只有标记，尚没有实际补查器或自动回退连接。

原始 MFT 名称引用计数包含独立 DOS 名称属性；它与排除 DOS 别名后的 Windows 可见硬链接数分开校验。本轮真实文件比对据此修正了原合成夹具的头部计数。

重复记录、槽位复用、目录入口冲突、过期父引用、目录循环、未知大小和总量溢出不能成为完整成功。未提供跨目录的硬链接占用归属或生产图表；目录索引及 NTFS 系统开销不计入文件数据总量。即使 `CompleteSyntheticIndex` 为真，也只意味着这批合成记录满足原型约束，不能证明活跃卷一致。

未实现整卷 MFT 批量读取、NTFS 3.0、不同 fixup stride、持续写入的日志协调、产品权限提升助手、真实性能对比与主程序集成。没有驱动、服务、系统日志修改或发布包。实际记录验证仅覆盖本轮 Windows 11 主机的 NTFS 3.1 VHD、512 字节扇区/4096 字节簇；Windows 10 及其他几何仍未实测。

`DecodeKernelReturned` 只用于已核对来源和身份的 IOCTL 记录，接受全体尾字为 USA 标记或全体为已还原保存值的形式；微软未公开承诺该接口的 fixup 表示，其他形式保守失败。原始入口仍严格验证保护标记，不允许 raw 校验失败后自动当作已修复记录放行。

## 合成夹具与依据

`NtfsRecordFixtures` 在内存创建记录；测试覆盖 fixup、属性边界、UTF-16、空/驻留/非驻留文件、硬链接与 DOS 名称、簇几何、特殊类型的补查标记、取消和索引冲突，另有 512 次固定种子的有限变异检查。合成数据不是磁盘镜像；测试不是完整 NTFS 格式一致性认证。

格式参考 Microsoft Learn 的 [FILE_RECORD_SEGMENT_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header)、[MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)、[ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)、[FILE_NAME](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name)。部分头部字段在公开结构中被保留，本原型的合成布局尚需未来独立来源和真实记录交叉验证；文件身份采用外部提供的槽号加记录序列，不猜测保留字段。
