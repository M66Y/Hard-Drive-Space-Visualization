# 受限MFT BITMAP属性定位原型

2026-11-12，基线 `cc4a09165de032426dcd63f27ce7e5da63d983f2`（第19轮已提交并推送）。用户已确认[本轮纯内存方案](../../docs/ntfs-bitmap-attribute-plan.md)，只处理合成record0，不新增I/O/Native/VHD，不修改已有Map/decoder/fixtures，不接model/v2/产品或发行。

实现与相称验收已完成，独立只读审查无阻塞。所有提取结果标为Synthetic，不证明真实位图格式、来源、位序或实际分配状态。

## 范围与预定验收

最多1024字节record0、4096预期槽、512字节驻留位图；先私有复制并用现有Map.Parse严格核验，再恢复私有USA定位属性。仅唯一未命名无flags/扩展的普通驻留形态可提取payload；缺失/不足保留明确状态，非驻留/命名/多重/特殊/扩展Deferred，无可读物理地址。畸形输入固定拒绝，不修改严格校验。

Release测试构建0警告、0错误；新入口 `--ntfs-bitmap-attribute-tests` 43核心、60独立反例通过，旧Map199、枚举91、位图模型48+58回归通过，见[validation.json](validation.json)。

独立夹具将512字节payload跨过首扇区510尾字，确认raw保护值与保存值不同，严格seed验证后返回恢复值；损坏、混合/全已恢复raw记录仍拒绝，无kernel fallback。覆盖缺失、空/部分/足够/额外容量、512/513界限、命名/多重/扩展/flags及非驻留Deferred、畸形偏移/长度、错误geometry/预期槽、取消及输入不变。

先在复制前检查1024记录/4096槽及geometry绑定，再用同一私有源交Map.Parse；Unsupported→Deferred、严格失败→Invalid，不能把它们说成磁盘损坏。随后只恢复自己的USA副本并定位，payload为新数组，CopyPayload再复制，输入/每次返回互不别名，默认结果序列化不带正文。Nonresident只允许受限长度元数据，不返回run/LCN/offset。结果中的Complete/Partial只表示容量min(expectedSlots,payloadBytes*8)，不是bit值/真实位序/来源或分配核验。

本轮无I/O、Native、VHD、原始样本/位图落盘或临时目录，正常测试构建保留；旧本机kernel六样本未变不上传。不更改生产自动/高速路径，不打包、标签或Release。真实读取、非驻留run验证/块访问、同来源绑定与后验、182槽实际状态、复杂格式/完整建树和资源收益仍未完成。

`changes.patch`以本轮基线正向恢复并逐文件核对，`files.json`记录必要文件SHA-256，不递归自身或旧归档；提交推送状态以Git历史为准。

已准备下一轮[绑定自有来源的形态观察方案](../../docs/ntfs-bitmap-source-plan.md)，仅方案未实施；它区分来源真实、容量与位图解释，不新增当前轮真实操作或以后默认VHD许可。
