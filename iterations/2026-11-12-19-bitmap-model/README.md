# 纯合成MFT分配位图校验模型

2026-11-12，基线 `643c11ed7c31de8ad4246cfbf72e5d0537fccdf4`（第18轮已独立提交并推送）。用户确认[本轮方案](../../docs/ntfs-bitmap-model-plan.md)，只实现模型和测试，不新增读取器/Native/VHD，不接现有v2摘要、生产扫描或发布。

模型及相称验收已完成。输入仅为合成声明，任何模拟来源/map对象都不具备真实NTFS来源证明。

## 预定合同

最多4096槽、512字节位图；Provided必须与声明CoveredSlots精确匹配，Missing必须空输入，有意部分覆盖之外保持Unknown。完整且唯一的无名称观察保留四类解码结果，位图的Allocated/Unallocated/Unknown声明三态单独统计，12格交叉计数守恒，不改写原解码分类或计算大小。

HeaderInUse是可空合成观察，比较不同只计证据不一致，不断言损坏或自动修复。AllZero只是调用方的合成观察，不能推断空闲；模型限定AllZero=true时必须Invalid且没有有效HeaderInUse，避免自相矛盾输入。LSB-first是模型协议，真实NTFS格式/来源仍未验。

## 验证与收尾

Release测试构建0警告、0错误；新入口 `--ntfs-bitmap-model-tests` 48核心+58独立反例通过，旧枚举91、来源68回归通过。合成检查覆盖12格交叉、缺失/部分覆盖、7/8/4095边界、最大4096槽、末字节padding忽略、错绑定/截断/超量/重复/遗漏/自相矛盾输入、输入不变、取消及仅聚合输出。细节见[validation.json](validation.json)。

审查发现并修复外部输入可抛伪造取消/模型异常绕过净化的问题。三个访问点（Count、Memory复制、indexer）现在捕获任何外部异常，先检查传入token取消，再抛固定BitmapModelInput；不相信外部异常类型/Code/Message。12项边界×异常×取消组合覆盖，既不透传私密文本，也不返回部分报告。模型自身的固定校验与传入token取消保持正常行为。

方法先检查4096槽/512字节预算及形状，再私有复制；观察列表完整唯一，三态/四类/12格总数守恒。Provided的覆盖尾部padding不进入计数；未覆盖Unknown不因padding减少。读者可空header只是合成观察，冲突不自动修复。输入并发变化不构成真实来源稳定性证明，模拟token仅比较对象引用。

本轮没有Native/VHD/原始记录或逐槽信息留存，不改变v2、生产扫描或速度。不生成包、标签或Release。无本轮临时目录需清理，正常开发测试构建保留；此前本机6条kernel样本未变，不上传。

真实MFT记录分配位图提取、位序/格式、同持锁来源绑定/后验、182槽的实际分配状态、RecordFlags/SparseMappingFlags支持、完整建树与资源收益仍未完成。`changes.patch`从本轮基线正向恢复并逐文件核对，`files.json`记录必要文件SHA-256，不递归自身和旧归档；提交/推送结果以Git历史为准。
