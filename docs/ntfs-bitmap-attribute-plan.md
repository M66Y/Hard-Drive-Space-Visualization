# 受限BITMAP属性定位原型方案

2026-11-12，第19轮模型已完成，尚无真实位图提取。用户确认本纯内存范围后，已完成实现和相称验收，见[第20轮](../iterations/2026-11-12-20-bitmap-attribute/README.md)。不读取卷或文件，也不获得以后VHD授权。

## 建议范围

先复制有界合成record0，再用既有 `NtfsMftMap.Parse` 核验严格USA、record0/名称及MFT数据映射；定位器只在自己的副本恢复USA并遍历已验证属性，不修改decoder/Map行为。最多1024字节输入记录、4096预期槽、512字节位图payload。

仅接受唯一、未命名、无特殊标记且无属性扩展的驻留 `$BITMAP`。可返回供测试内部使用的私有payload副本与明确覆盖/不足状态；缺失或不足不当全零，不能直接宣称已完成分配核验。

非驻留只返回Deferred和有界长度元数据，不给可读地址或执行run读取；属性扩展、命名、多重、压缩/稀疏/加密等形式也保持Deferred。格式畸形固定拒绝，不把未实现形态当损坏。整个结果固定为合成/未验证来源，不接入第19轮模型、v2报告或产品统计，不序列化位图正文或原始记录。

## 验收与边界

新增定位器与核心测试，独立反例检查payload边界、跨USA扇区尾字、缺失/多重/命名/属性扩展、非驻留Deferred、畸形偏移/长度、取消和输入不变；构建后跑新入口及受影响旧Map/枚举回归，文档与独立恢复归档后提交。不新增Native、VHD、文件/卷I/O或发行。

真实位序/来源、非驻留run完整验证与只读读取、同持锁owner绑定/前后核验、182槽实际状态仍未完成。若窗口不足，不为赶时间扩大支持或放松校验，先记录明确状态。

格式依据：[属性记录头与驻留/非驻留](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)、[属性列表与扩展记录](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry)、[MFT记录位图](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table)。

## 已实现与验证

新入口 `--ntfs-bitmap-attribute-tests` 通过43核心、60独立反例；测试构建0警告/0错误，旧Map199、枚举91、模型48+58回归通过。跨第一扇区510尾字的512字节payload在严格验证后私有恢复，损坏、混合或全已恢复的raw输入仍拒绝；不存在kernel fallback。

结果只有Synthetic内部属性；payload通过CopyPayload独立复制访问，不自动序列化正文。`ResidentComplete` / `ResidentPartial` 仅指提取字节相对预期槽数的容量覆盖，不验证bit值、真实位序、来源或实际分配。非驻留始终Deferred，最多返回受限长度元数据，不提供LCN/run/offset地址。本轮没有真实位图或新磁盘操作。
