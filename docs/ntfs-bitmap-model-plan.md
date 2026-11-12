# 纯合成MFT分配位图校验模型方案

2026-11-12，第18轮识别出182个全零Invalid槽；当前没有MFT记录分配位图证据，不能据此改成Free。用户确认本方案后，已完成纯合成模型及相称验收，结果见[第19轮](../iterations/2026-11-12-19-bitmap-model/README.md)。

微软区分 `$Mft` 的未命名 `$BITMAP`（哪些MFT记录在用）与卷级 `$Bitmap`（哪些簇在用）；`FSCTL_GET_VOLUME_BITMAP`取的是后者，不能拿来替代记录分配证据。来源：[MFT说明](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table)、[卷位图接口](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap)。

## 本轮建议仅做模型

不读取卷或文件、不新增Native/Live入口、不解析真实 `$BITMAP` 属性。输入为最多4096槽的无名称合成观察、最多512字节合成位图和模拟来源/映射绑定；全部结果明确标为Synthetic，模拟token不成为真实来源证明。位序采用模型中明确约定的LSB-first协议，真实NTFS格式及来源以后另验。

位图缺失或明确声明仅覆盖部分槽时，未覆盖部分保持Unknown。Provided位图必须严格满足声明的覆盖长度，不能把截断/超量/错绑定当有意部分覆盖；预计槽数、完整观察列表、槽号唯一与边界均检查。

输出保留Allocated/Unallocated/Unknown三态声明，与Decoded/Free/Invalid/Unsupported四类交叉计数；三态及四类分别守恒。原始解码状态不改写，全零本身不提供分配证据；位图和已观察记录头在用标志不同仅记录不一致，不自动修复、不判断损坏、不统计大小或释放量。

## 验收与边界

合成测试覆盖全零但声明已分配、非零但声明未分配、有效Free、缺失/部分覆盖、声明截断、7/8和4095边界、末字节填充、来源/映射不符、重复/遗漏槽、取消及输入不变。所有错误为固定代码，不输出原始位图/逐槽身份、路径或任意消息；模型不接入现有v2/产品统计。

完成独立归档、文档与提交；本轮无VHD操作或发布。真实位图提取、驻留/非驻留与属性扩展、绑定同一持锁来源、前后核验和182槽的真实分配状态，仍需后续另给方案与验证。

## 已实现合同与验收

模型使用两个模拟对象的引用身份及槽数作绑定，不调用其值相等逻辑。所有报告固定为 `Synthetic`；不输出token、位图正文或逐槽观察。HeaderInUse可空，提供的Decoded/Free标志须与分类自洽；Invalid/Unsupported的标志只是输入观察。AllZero=true限定Invalid且无有效header，仍不推断空闲。

Provided严格覆盖长度内按LSB-first读取，末字节高padding位忽略，不加入计数，也不缩减未覆盖槽的Unknown。最多4096槽/512字节在复制前检查，完整唯一观察列表保证没有遗漏；固定12交叉格保留零值。

构建0警告/0错误，48核心与58独立反例通过；旧枚举91、来源68回归通过。审查中修复了外部访问器可伪造取消/模型异常绕过净化的问题：Count、Memory复制、indexer三个边界统一捕获外部异常，先检查传入token，再抛固定输入错误。12项注入/取消组合确认不透传任意消息或伪造Code，不返回部分报告。没有访问卷或文件，也不证明实际182槽的分配状态。
