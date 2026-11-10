# Windows 10 兼容候选包：本机结果

资产源码提交 `6f6bdc07815e6b7cee3af079724672be62c7b515`，清单 SHA-256 `d3c404122ee1df31bfde72957caa6928a9e4b97fadd7bb2b7d72c706e7785487`。候选文件放在本机忽略目录 `artifacts/releases/windows10-candidate/v1.5.0/`，未创建标签、上传或发布。此前 `artifacts/releases/v1.5.0/` 是旧候选，继续保留且不可混用。

| 资产 | SHA-256 |
| --- | --- |
| AsukaLens v1.5.0.zip | `69c69c12b7b8eb3f1ffa8d9b23360b923ea316e920a6e0ee9bb98690e98f508d` |
| AsukaLens v1.5.0.exe | `07232a9893f2287621e28c17c7fd417f8cefae559345f655391e8af9ad0ec72e` |
| AsukaLens v1.5.0-source.zip | `f556a7948e3f9a0d2a76a4c5549de9f91dff83e801fb2ada1973ae7d8bf751b8` |
| AsukaLens v1.5.0-source.tar.gz | `8fdd6f240e38685bd94be987e5219d0b983e243f41052a82d6c10fef4cd045f3` |

`release-manifest.json` 和 `SHA256SUMS.txt` 同目录；四类资产共含 266 个源码文件、283 个运行文件。新候选包通过静态检查；开发机 Windows 11 上的包级检查 21 项通过，包括两种源码包独立构建、内置运行时与主要程序集流程、安装/合成升级/降级拒绝、运行保护、失败回退及卸载。结果见 `package-acceptance.json`；双语候选说明见 `candidate-notes.md`。此包级测试不等于真实发行 EXE 的完整图形使用流程，也不等于干净系统验收。

本轮自动检查：应用及测试项目构建 0 警告、0 错误；离线工具 102 项、发布说明 47 项、发布脚本 48 项、上传离线替身 80 项、验收保护 32 项、真实 Inno 身份编译 6 项通过。未创建远程标签、草稿或 Release。

Windows 10 虚拟机真实 EXE 的部分使用观察见 `windows10-observations.md`；探针未发现已知现代 .NET 安装，但干净系统来源和完整验收场景仍未确认。干净 Windows 11 对同一资产单独验收；完成前不宣称完整验收或正式发布。当前候选说明为预备文本，不能视为已发布说明。本机包级夹具与解压副本已清理，候选及后续仍需的编译工具暂留。
