# 有界MFT分类原因诊断

2026-11-12，基线 `e01f672ec005c967f51a8afe1d5457777886d533`（第17轮已提交并推送）。用户已确认[本轮方案](../../docs/ntfs-classification-diagnostics-plan.md)，在本次2小时窗口剩余时间实施，沿用已批准的新建256MiB隔离VHD范围验证；不访问个人卷、不创建VSS、不保留原始记录、不接产品或发布。

本轮已完成实现、相称回归、本机v2真实验收和独立审查。上一轮v1摘要仍保持原样，不用作本轮v2通过证据。

## 范围与合同

新增 `CodeCounts`（Status/Code/Count）和 `AllZeroInvalid`，revision为 `bounded-mft-source-v2`。32种固定状态/代码配对白名单，最多128项，四类小计及总和必须吻合；按Decoded/Free/Invalid/Unsupported及Code的Ordinal顺序输出。Invalid中原始1024字节全零只是数据形态观察，不自动变成Free、未分配或零占用。

不修改严格decoder、四类主分类、21目标/关闭核验、4096槽/4MiB+bootstrap/256读取预算。只输出聚合计数，不输出槽号、路径、引用、原始字节或异常文本；旧v1历史仍保留，新摘要拒绝版本混用。

## 验证与收尾

测试项目Release构建0警告/0错误；枚举器91、来源68、旧guard206+source63通过。PS新摘要477、外层模式/代码284项通过。固定6记录矩阵覆盖全零、非零Signature、USA失败、未知属性、正常在用和有效空闲；4096全零合成检查保留Invalid，最多128种原因聚合，未知配对/注入/乱序/重复/版本混用/计数不符均拒绝。详见[validation.json](validation.json)。

在新建256MiB VHD实际通过473项，完整256槽/4页，v2结果如下：

| 状态/固定代码 | 数量 | 本轮含义 |
| --- | --- | --- |
| Decoded / Decoded | 67 | 当前严格decoder接受 |
| Invalid / Signature | 182 | 原始1024字节均全零，仍保持Invalid |
| Invalid / SparseMappingFlags | 1 | 触发现有映射标志校验拒绝 |
| Unsupported / RecordFlags | 6 | 记录标志超出现有支持范围 |

合计256；Free=0，AllZeroInvalid=182，21目标全部匹配、14目标分配未知。ReadCalls5/RequestedBytes263168（含bootstrap）；来源关闭、后置Native/facts核验、解锁、父脚本卸载与清理均通过。没有测量性能或物理I/O，不用本表证明磁盘损坏、182槽未分配或全部格式支持。

独立只读审查未发现阻塞。全零与错误码均来自同一次严格decode及原始页观察，不改变decoder、不跳过目标/关闭核验，不新增I/O。失败/取消仍不发布部分结果，新原因列表仅在成功关闭后外泄；同步I/O取消边界与第17轮相同。PS深重建输出，仅固定聚合信息，无槽号/路径/引用/token/原始字节或任意异常文本。

真实证据见[range-native.json](range-native.json)。本轮无失败Native尝试；一个自有VHD已卸载清理，结果副本在归档SHA一致核对后删除；此前kernel的6条本机原样本（8文件/9683字节）未变，不上传。生产代码未改变，不打包、不创建标签或Release。

MFT位图解释、格式支持、真实4096槽与更广系统/几何、活跃卷、完整建树和资源收益仍未实现。`changes.patch`从本轮基线正向恢复并逐文件核对，`files.json`记录必要文件SHA-256，不递归包含自身及旧归档；提交与推送绑定以Git历史为准。
