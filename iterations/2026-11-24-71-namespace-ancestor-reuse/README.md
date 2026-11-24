# C71 目录枚举祖先验证复用候选

2026-11-24。基线`7e6f21d`，产品C#此前为`2781ebb`，开始工作区干净。C70为规则分流，不覆盖其改动或用户确认保留的C69删除。

用户批准C69候选：目录枚举阶段复用已验证祖先，保留实际打开、权限/句柄核验、容量、失效及原路径回退；补查、界面暂不动，先合成验证收尾，真实磁盘另询问，不发布。

## 结果与取舍

- 新增`src/Scanning/Ntfs/NtfsNamespaceAncestorValidation.cs`，私有Source创建可释放的Namespace租约。候选最多8192条不可变证明，绑定源代际、完整身份、父级和现有相对路径；不开新长期句柄、不缓存绝对路径。
- 只有父证明可用且未满容量时才省祖先属性查询；末级属性预查、实际CreateFile/ACL、句柄ID和标签、分页/EOF仍在。缺失/容量满回原Resolve；矛盾/失败不当未命中吞掉，取消/关闭/租约结束失效。重复打开不替换证明，避免额外保留旧证明链。
- 工厂`CreateNamespaceBackend(reuseValidatedAncestors: true, token: token)`显式选择候选；正常调用保持默认false，仅加using收尾。未验证真实来源前不默认启用，没有新增UI/命令行入口。
- `ReadNativeSize`、`LookupSize`、原Resolve、NativeCursor及大小索引/元信息读取保留基线逻辑，特殊文件补查和根创建常数级重复不做。说明见[候选文档](../../docs/ntfs-namespace-ancestor-candidate.md)。

## 验证

应用与测试构建均0警告/0错误。最初默认restore因沙箱无法读取用户级NuGet配置失败；项目/依赖未改，使用已有还原资产和`--no-restore`完成构建，未修改用户配置或申请真实读盘。

| 检查 | 通过项数 |
| --- | ---: |
| 新增 namespace-ancestor | 94 |
| 原 snapshot-namespace / personal-source / personal-index | 84 / 52 / 27 |
| reparse-policy / large-profile / snapshot-size | 36 / 25 / 46 |
| fast-scan-bridge / coordinator | 45 / 68 |
| 核心 / advanced | 38 / 25 |
| 合计 | 540 |

新增检查只用合成回调/句柄/目录页，直接覆盖生产末级路径辅助方法，核完整入口和相对路径等价、共享祖先、64层链、容量耗尽、父级/重解析点/身份反例、取消、来源关闭/代际、重入、读失败/关闭失败和资源释放。其余核心/advanced使用自建临时文件，bridge仅本机管道。原路径的完整核验和实际NativeCursor代码与基线比较未变；未改变UI/补查链路。详见verification-summary.json与source-basis.json。

只有根、201目录共享树、65目录链的合成属性回调分别为1→1、501→201、2145→65；每组目录打开和句柄身份/标签回调次数不减。这不是物理I/O、实际Native调用或实际提速证明。

## 尚未验与收尾

未创建VSS/VHD、未读个人目录/原始页；真实VSS上的私有来源兼容、权限/清理和实际收益待单独授权测试。候选没有设为默认，已接受正常程序/C55及UI不替换，未打包、建标签或Release。

本轮构建输出隔离在ignored artifacts/namespace-ancestor-C71；保留精选校验与压缩日志，清理可再生临时应用输出、早期重复日志和补丁重放目录，不删活跃demo、正常程序、C55或历史发行快照。关键文件的SHA-256、日志压缩验证、基线及补丁恢复核对随归档提供；Git提交/推送状态以本轮提交为准。
