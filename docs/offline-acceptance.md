# 在另一台 Windows 电脑上离线验收

这套工具为验收者准备隔离样本、核对包并记录观察结果。支持 **Windows 10 22H2 x64 和 Windows 11 x64、64 位 Windows PowerShell 5.1**；目标电脑不需要 PowerShell 7、Git、现代 .NET SDK 或编译器。它不会安装或卸载软件、创建虚拟机、上传数据或改变发布门槛。两个系统分别验收并记录，不能用一个系统的结果代替另一个。

当前已完成工具在开发机上的合成检查；本机有现代 .NET，**不代表干净系统或正式候选包通过**。实际发行 EXE 的既有本机证据见[第 16 轮记录](../iterations/2026-11-07-16-executable-workflow/README.md)。

## 准备输入

从已核对的项目提交复制 `scripts/acceptance/` 中的两个 `.ps1` 文件，以及本说明。脚本使用 UTF-8 BOM，复制时保留编码，避免 Windows PowerShell 5.1 误读中文。工具不修改系统执行策略。

开发机先按[发布说明](releasing.md)生成并校验包，再将包含以下六个文件的目录拷到目标电脑：四类资产、`release-manifest.json`、`SHA256SUMS.txt`。开发机必须验证源码包内容一致、安装器身份等事项；此工具仅核对四类资产的完整文件哈希，并深入核对便携 ZIP 的清单、运行时声明、许可文件和构建身份，不替代源码构建、安装器或 tar.gz 内容验收。

另行取得已在开发机核对过的 `release-manifest.json` SHA-256。不要从未经确认的同一目录现算一个值就当作信任依据。哈希用于绑定输入内容，不能代替确认文件来源。

以下在 **Windows PowerShell** 中执行；示例目录需换成自己选定的位置，父目录须已存在。`$work` 必须是尚不存在的本地绝对目录，不能放在资产目录内；不要使用卷根、网络共享或目录链接。

```powershell
$tool = '.\scripts\acceptance\Invoke-Acceptance.ps1'
$assets = 'C:\AsukaLens-acceptance\assets'
$work = Join-Path $env:TEMP ('AsukaLens-acceptance-' + [guid]::NewGuid().ToString('N'))
$manifestHash = '替换为独立核对过的64位SHA256'
& $tool -Action Prepare -Assets $assets -ManifestSha256 $manifestHash -Workspace $work
& $tool -Action Inspect -Workspace $work
```

准备完成后，`portable/` 是核对过的便携程序，`sample/` 含中文嵌套目录、零字节文件、小型二进制和合成背景，`data/` 是程序的隔离数据目录。元数据文件只在工作目录内记录归属与本轮进程。上限为清单 8 MiB、便携载荷 1 GiB / 10,000 文件、四类资产合计 2 GiB；超出时停止，不临时放宽限制。

工具报告中的 `modern_dotnet.status` 为 `detected`（发现已安装证据）、`not-detected`（未发现已知安装）或 `unknown`（检查不完整且没有阳性证据）。检查 PATH、标准位置、注册位置及 DOTNET_ROOT，不扫描整盘、不执行 `dotnet`。`not-detected` 不能证明任意位置都没有私有安装；Windows 自带 .NET Framework 不属于此处的现代 .NET SDK/共享运行时。

## 启动并实际操作

```powershell
& $tool -Action Start -Workspace $work
```

启动真实的 `portable\AsukaLens.exe`，仅为该进程设置 `ASUKALENS_DATA_DIR`；主窗应显示“隔离数据”。父进程、用户级和系统级环境变量不变。再次启动前必须正常退出当前窗口。每次启动重新核对提取后的载荷，文件变化则拒绝启动。

以下每项都需要实际观察。选择 `sample/` 扫描，修改与删除只在这份合成目录中进行；不要扫描个人目录或导入个人图片。样本较小，来不及检查取消和扫描反馈时应记为 `not-tested`，不能仅凭程序打开就记通过。

| 记录标识 | 人工检查内容 |
| --- | --- |
| `portable_start` | 直接 EXE 打开、显示隔离数据、无启动错误 |
| `scan_and_navigation` | 选择样本、完成扫描、进入子目录及返回，名称和大小符合样本 |
| `views_and_metrics` | 空间分布、大文件、类型视图与两种统计口径可操作，限制说明清楚 |
| `search_sort_file_helpers` | 搜索与排序、复制路径、资源管理器定位准确 |
| `refresh_and_cancel` | 样本新增/修改后普通与强制刷新正确；扫描中反馈、取消与恢复实际可观察 |
| `background_and_restart` | 使用合成图片调整焦点/缩放和配色，检查取消、保存、正常退出后再次 Start 的恢复 |
| `diagnostics_and_notes` | 查看、预览导出、清空诊断；只用合成文字新增、编辑并重启检查问题记录 |
| `normal_exit` | 正常关闭后进程退出；工具会拒绝在进程仍活动时将此项记为 passed |
| `installer_workflow` | 按发布手册人工完成安装/启动/升级/卸载；本工具不执行安装器，也不检查这项声明是否真实 |

安装后的 EXE 不由本工具启动。需按[既有隔离启动方法](releasing.md#直接发行-exe-的本机使用流程)使用另外选定的数据目录，并自行完成清理；不要把本工具的工作目录交给其他程序持续使用。没有完成的安装步骤仍保留 pending 或 not-tested。

## 记录和导出

```powershell
& $tool -Action Record -Workspace $work -Check portable_start -Result passed
& $tool -Action Record -Workspace $work -Check refresh_and_cancel -Result not-tested
& $tool -Action Inspect -Workspace $work

$report = 'C:\AsukaLens-acceptance\observations.json'
& $tool -Action Export -Workspace $work -Output $report
```

结果只能填写 `pending`、`passed`、`failed`、`not-tested`；可重新记录修正。字段没有自由文本，不自动把其他项目一并改成通过。启动次数是进程启动事实，不是功能通过证明。导出需使用工作目录外的新文件，拒绝覆盖已有文件，避免清理时丢失报告。

报告仅包含固定字段：版本、提交、清单哈希、是否 LOCAL TEST、系统类别与架构、已知运行时证据类别、计数和人工状态。它不包含本地目录、账户/机器名称、进程 ID、真实文件清单、背景图片、诊断原文或笔记。工作目录内的数据和终端显示的本地操作路径不等于可公开报告，不要整体上传工作目录。

环境字段记录准备时的快照；在验收期间更改系统环境后应建立新工作目录重新验收。在确实使用新装系统或已知干净快照、且探测没有已知现代 .NET 安装时，可另行声明来源：

```powershell
& $tool -Action Record -Workspace $work -Provenance fresh-install
# 已知干净快照使用 known-clean-snapshot；未确认使用 unconfirmed。
```

声明时会再做一次环境探测；发现已安装证据或探测不完整即拒绝。用户声明仍不是工具对整个磁盘的证明。报告始终为 `release_gate=not-evaluated`，与正式上传模板不是同一种格式；没有 `clean_windows_no_dotnet=passed` 的自动生成逻辑。维护者须在真实正式候选资产完成全部验收后，另行审阅并填写发布门槛。

## 清理与失败恢复

正常关闭本轮程序，先导出需要保留的结果，然后：

```powershell
& $tool -Action Cleanup -Workspace $work
```

仅删除带本工具归属标识、绝对位置匹配的工作目录。源资产、原始图片和目录外的报告不受影响。程序仍活动、进程状态无法确认、出现目录链接或归属不符时拒绝；不会强行结束程序。不要移动、改名或手改工作目录的元数据。

| 状态或错误码 | 处理方式 |
| --- | --- |
| `ACCEPT_MANIFEST_HASH` / `ACCEPT_ASSET_HASH` | 重新核对来源与复制结果；拒绝发生在创建工作目录之前 |
| `ACCEPT_WORKSPACE_EXISTS` | 不覆盖已有目录；确认旧目录归属后用 Cleanup，或选择另一个新目录 |
| `ACCEPT_PREPARATION_INCOMPLETE` | 准备被中断或清理未结束，禁止启动；关闭相关程序后 Cleanup，再重新 Prepare |
| `ACCEPT_PROCESS_RUNNING` / `ACCEPT_PROCESS_UNKNOWN` | 正常退出本轮窗口，确认进程状态后重试，不终止未知进程 |
| `ACCEPT_REPARSE_POINT` / `ACCEPT_OWNER_MISMATCH` | 停止操作并核查工作目录；不要对未知目标递归删除 |
| `ACCEPT_OPERATION_FAILED` | 文件占用、权限或其他 I/O 失败；先保留现场并检查权限/剩余空间/占用。错误输出不会直接打印异常全文 |

状态更新使用同目录临时文件与原子替换，写失败保留旧状态。中断的准备保留 `preparing` 标识供 Cleanup；清理遇到文件占用时先保留归属标识与 `cleaning` 状态，释放占用后可重试。导出失败不覆盖已有报告。脚本没有联网操作；其合成回归入口为 `tests/OfflineAcceptance.Tests.ps1`，需在 Windows PowerShell 5.1 中执行。
