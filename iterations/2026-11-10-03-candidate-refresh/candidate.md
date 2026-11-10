# v1.5.0 重冻候选包

源码提交 `2d54a681986127e3c966eca03b1d72ef3a33f9aa`，清单 SHA-256 `198d6dcf860ede30d418ac674a6c4c91fd3773d41c526d4d49d57da177968fce`。资产位于本机忽略目录 `artifacts/releases/refrozen-2d54a68/v1.5.0/`，未创建标签、上传或公开发布。相同版本的旧候选单独保留，不能混用清单或证据。

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| AsukaLens v1.5.0.zip | 54691080 | `22e2a583920ba8a527b85939706e163c0639b4590d5d74e6384ce56e1ef1c9c3` |
| AsukaLens v1.5.0.exe | 40334431 | `4ce6ef2e768bbfb34d0277658c00cd984eac15bbcb2258e6b74bd3aabb00375a` |
| AsukaLens v1.5.0-source.zip | 8856352 | `b53cfa67dc9d21250e8b504351c24d040e7b32450452b75655d1333f45d035f3` |
| AsukaLens v1.5.0-source.tar.gz | 8719202 | `7304cd926931964072703ecded5b12683b59cd72fd99c41cbb3f47b7346b99c9` |

同目录另有 `SHA256SUMS.txt` 和 `release-manifest.json`。新源码 ZIP 的中英文 README 均指向通用发布准备清单；源码 ZIP/tar 的 292 文件及便携载荷的 283 文件经静态清单检查。与上一候选比较，`AsukaLens.exe`、`DiskAtlas.dll` 和 `build-info.json` 的哈希发生变化，故上一包的 Windows 10 虚拟机观察不能标作本包通过。

开发机上新包完成 21 项包级检查，详见 `package-acceptance.json`：包括两份源码包独立构建、包内生产程序集和运行时的主要流程、非管理员安装/合成升级/降级拒绝/失败恢复/卸载及用户数据保护。此结果不代表新包真实 EXE 在 Windows 10 的桌面操作或两个系统的干净环境已验收。双语预备说明见 `candidate-notes.md`；`release-acceptance.json` 保守保留未完成门槛。

源码 ZIP 的 288 个文本条目检查本机用户及工作目录前缀和常见凭据标记未命中；两个应用二进制检查 UTF-8/UTF-16 路径前缀未命中。检查不保证识别所有形式的敏感数据。包级测试的 1,220 个临时文件、独立干净检出和本轮便携编译器已清理；未修改原工作区的用户未跟踪文件，既有候选保留。

待完成：新包 Windows 10 真实使用及安装核验、两种系统干净环境验收、隐私与许可最终人工审阅、正式授权后的远程标签/草稿上传及下载核验。当前两个系统的干净验收门槛均为 pending；未创建标签或 Release。文档证据提交不改变上面的资产源码提交。
