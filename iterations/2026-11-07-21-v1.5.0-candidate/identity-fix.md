# 正式身份编译修正

第一次冻结提交 `0edb5e27591534c57401677ce5dc53f4609d7177` 的正式候选构建被既有资产校验拦截：清单要求正式身份，但 EXE `ProductName` 为 LOCAL TEST。失败发生在本地静态验收，未安装、未形成候选快照、未上传；暂存产物及锁由脚本清理。

原因是命令行定义 `LocalTest=0` 到达预处理判断时可为字符串，非空字符串使原 `#if LocalTest` 进入测试分支。现在通过 `Int(LocalTest)` 明确转换，仅允许 0/1；默认、正式、测试三个实际编译结果及 2、-1、非法文本拒绝均用真实固定编译器检查，不能用 PowerShell 对同名变量的布尔转换代替。

转换契约依据 [Inno Setup 官方 Int 文档](https://jrsoftware.org/ishelp/topic_int.htm)。`tests/InstallerIdentity.Tests.ps1` 编译生产 `.iss` 搭配小型合成载荷，只核对结果/拒绝状态，不执行这些测试安装器。六项真实编译检查通过，夹具随后清理。最终候选必须重新冻结提交并打包，首次失败提交不得作为资产通过证据。

`identity-fix.patch` 以首次冻结提交为基线，恢复与哈希记录在 `identity-fix-files.json`；本修正不改扫描或界面功能。原冻结前验证仍对应当时源码，最终包的复验结果单独记录。
