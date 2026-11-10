# v1.5.0 正式发布记录

2026-11-10，用户明确要求继续正式发布；[AsukaLens v1.5.0](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/tag/v1.5.0) 已公开，非草稿、非预发布。标签绑定源码提交 `4d473747f88370a1b411129743e2169229c78dcc`。本记录基线为 `8e78881d83c41b6d0805eb6b55030fed6e0588ea`。

最终候选与验收范围见[候选验证](../2026-11-10-06-release-waiver/host-verification.md)。最终资产在 Windows 11 开发机完成包级检查和实际桌面主要流程检查；此前 Windows 10 虚拟机验证的是另一候选，最终资产未在该虚拟机重测。干净 Windows 10/11 环境未验收，维护者明确同意作为例外发布，发布说明保留此限制。

GitHub 将资产名称中的空格转换为点号。最初草稿的下载核对因此失败，资产内容未变。修复时先验证原候选与验收绑定、标签、草稿正文，以及全部六个远程资产的哈希；下载并核对后，仅替换草稿中的清单与校验文件并调整双语下载链接，再重新下载六个文件逐项核对。发布后确认公开状态和六个远程 SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| AsukaLens.v1.5.0.zip | `ebde9611fc9320b18ce057cca6c9f2c63e37ed41226a899a7d61f7aa42bc7c10` |
| AsukaLens.v1.5.0.exe | `d5cd5d59ad31198cfcfc16451f2dad31c9b5798d474adbc9e9e395d526a54d8d` |
| AsukaLens.v1.5.0-source.zip | `3f6107458a28e1aeebe9c1d942dc44bb7db3873e156f89ffccdaecdb876458c8` |
| AsukaLens.v1.5.0-source.tar.gz | `dbc9f025512bfeb2f4a6057386e72cc0dced7539055030a9ef1b74312466b82a` |
| release-manifest.json | `282cf2aa83468ebbefb9e8dade5e914bc80dcb956e46f43710b86e9fd16a54b9` |
| SHA256SUMS.txt | `2103ddd4a7ee2aa1c9059bfb5ae9a2c1d1dcb72d8cd856ded6bf541522d74029` |

`repair-draft.ps1` 是本次专用脚本的历史证据，归档移动后不能直接运行，且只能操作当时未公开的特定草稿；SHA-256 为 `e26650baafd1f52fef45441a20043c8c64b42bb76c96b554302b60dd89c6b879`。通用上传脚本未修复该文件名问题，后续发行前需设计并测试通用处理，不应复用此历史脚本。临时下载目录由脚本清理；候选及历史资产保留。
