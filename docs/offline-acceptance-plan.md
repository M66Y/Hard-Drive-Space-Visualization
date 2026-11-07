# 干净系统离线验收工具方案

状态：待用户确认实施。核查日期：2026-11-07；基线：`e351cce7fb39f2327842b6345e8aafa964d18782`。本文件是下一轮的范围建议，不代表工具已实现、干净系统验收已通过或允许正式发布。

## 要解决的问题

便携版和安装版真实 EXE 的本机使用流程已通过。当前没有未安装 .NET 的独立 Windows 11 x64 环境；现有包级验收脚本又要求 PowerShell 7.4、测试项目及开发工具，不能直接作为干净电脑上的使用入口。其构建和源码验收职责继续保留。

建议先开发可在 Windows 自带 PowerShell 5.1 中运行的离线验收入口。接收已在开发机生成并校验的发行资产，准备隔离样本，帮助操作者逐项验收并留下不含个人信息的记录。本轮只验证工具和本机准备流程；真实干净环境通过状态继续留待实际执行。

## 本轮建议范围

| 步骤 | 行为与可观察结果 |
| --- | --- |
| 环境预检 | 检查 Windows 11 / x64、PowerShell 宿主、已知 .NET 安装证据；明确区分“发现已安装”“未发现已知安装”“检查不完整”。不将找不到 `dotnet` 命令直接判为干净系统 |
| 资产核对 | 输入资产目录及独立核对过的清单 SHA-256，检查四类资产的名称、大小、哈希与版本。验证便携 ZIP 路径和内容清单后才解压；发现不符则停止准备 |
| 准备样本 | 在新建的专属目录生成小型中文目录、零字节/嵌套文件及合成背景；不扫描真实下载目录，不生成 20 万文件或重复的大型安装包 |
| 启动验收 | 显式启动所选便携 EXE，按进程设置 `ASUKALENS_DATA_DIR`。显示操作清单；操作者使用真实界面进行检查，进程启动成功不等于整个流程通过 |
| 记录结果 | 自动事实与人工填写分开；每项为 pending / passed / failed / not-tested。报告绑定提交、版本及清单哈希；缺项保留 pending，不自动填满发布门槛 |
| 清理 | 核对专属目录标识、路径边界与本轮进程状态；程序仍在运行时拒绝清理。不自动结束进程，不删除来源包、用户原图或其他目录 |

计划文件集中放在 `scripts/acceptance/`，配套合成边界测试放在 `tests/`，使用手册放在 `docs/`。命令参数与报告字段以实施时的测试为准，不为本方案提前创建空目录。应用主界面、扫描策略、配置格式和现有正式上传门槛保持原契约。

本轮不自动安装虚拟机、修改 Windows 功能、下载系统镜像、卸载宿主 .NET、创建版本标签或上传发行资产。安装器仍按既有步骤由操作者选择执行，工具不新增静默安装/升级/卸载编排；源码构建继续在开发环境完成，不要求干净验证机安装 SDK。

## 环境与隐私约束

- 本方案的“未安装 .NET”指未额外安装现代 .NET SDK/共享运行时，不包括 Windows 自带的 .NET Framework。新脚本使用 Windows PowerShell 5.1，不调用 `pwsh`、`dotnet`、Git、编译器或需要 PowerShell 7 的公共脚本。当前电脑实际具有 64 位 Windows PowerShell 5.1，可用于兼容检查。[Microsoft PowerShell 版本说明](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_powershell_editions)
- .NET 安装证据结合 PATH 中的宿主、标准安装目录、架构注册位置及 DOTNET_ROOT 变量；读取失败归为未知。私有安装可能位于任意位置，有限探测不能证明整个磁盘不存在 .NET；正式环境结论还须记录新装系统或已知干净快照的人工来源确认。[安装版本检查](https://learn.microsoft.com/en-us/dotnet/core/install/how-to-detect-installed-versions)、[运行时查找位置](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch?pivots=os-windows)
- 不遍历整盘查找运行时，不收集账户名、主机名、硬件序列号、用户路径、真实目录清单和原始命令输出。公开报告只保留系统类别/架构、固定状态码、计数、版本、提交及资产哈希。操作记录和本地工作路径分开存放。
- 本机已有 SDK 的演练结果明确标为本机工具验证，不能导出为 `clean_windows_no_dotnet=passed`。报告不直接覆盖 `docs/release-acceptance.example.json` 或生成全通过的上传凭据。
- Windows Home 当前不支持官方 Windows Sandbox；没有可用环境时保留门槛，不借工具开发宣称解决环境缺口。[Windows Sandbox 适用版本](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)

## 验收方法

1. 在实际 Windows PowerShell 5.1 中执行语法与合成边界检查；测试本身不依赖开发用 PowerShell 7 的运行时能力。
2. 验证清单/哈希错误、缺少资产、版本不符、ZIP 越界/重复路径、已有输出目录、写入失败和中断后的可恢复状态；拒绝时不启动 EXE、不越界写入。
3. 模拟已安装、未知与未发现安装证据；本机真实探测应识别现有 .NET，不通过删除 PATH 或隐藏变量来制造干净结论。
4. 以小型合成数据检查报告默认待验收、人工结果与资产绑定、个人路径过滤；一项失败或未填写不能变成全部通过。
5. 验证清理只作用于本工具拥有的目录，活动进程阻止清理，来源资产和无关文件保留。
6. 若需真正的发行载荷演练，复用已批准的 LOCAL TEST 方式临时生成一次并清理；它是本机工具验收，不创建或发布正式版本。

## 其他选择与建议

也可立即规划本机 Windows 11 虚拟机，但会涉及虚拟化软件、系统镜像、许可证及额外磁盘空间，需单独确定环境方案。当前建议先完成上述小型离线工具和手册，再在实际可用的干净环境中执行；远程草稿上传放在正式候选资产与发布任务明确后进行。
