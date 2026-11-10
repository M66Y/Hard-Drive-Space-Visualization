# 打包、验收与草稿快照

当前候选面向 Windows 10 22H2 x64 和 Windows 11 x64，提供 .NET 10 自包含便携 ZIP、当前用户 Inno Setup EXE 安装器、源码 ZIP / tar.gz，以及 SHA-256 与文件清单。两个系统的完整干净环境验收和正式发布仍未完成；具体结果见[当前候选记录](../iterations/2026-11-10-01-windows10/candidate.md)。微软 .NET 10 的 Windows 10 官方支持限于指定 LTSC / Enterprise 版本，普通版仅属于项目兼容测试范围。

## 工具与来源

- 普通开发仍按 [README](../README.md)，无需安装 Inno Setup。发布脚本需要 **PowerShell 7.4+**、Git、`global.json` 指定的 .NET SDK。
- 工具版本和校验值位于 [toolchain.json](../scripts/release/toolchain.json)：本轮 Inno Setup **7.1.1 x64**、.NET 运行时 **10.0.14**、RID `win-x64`。更新这些值时重新验证包，不自动漂移到新版本。
- Inno Setup 安装器来自 [官方固定版本](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1)。下载后核对 toolchain 中 SHA-256 和有效的 `Pyrsys B.V.` Authenticode 签名；官方支持 `/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="所选临时工具目录"`，无需添加系统关联。工具不纳入 Git，脚本不会自行下载或安装编译器。
- 自包含还原使用独立 [NuGet.release.config](../scripts/release/NuGet.release.config)，只允许所需微软运行时包从官方 nuget.org 获取；普通 `NuGet.Config` 保持无外部源。SDK 还原可能同时请求 ASP.NET 运行时包，但应用不引用或分发 ASP.NET。
- 随包保留 Core Runtime 的 LICENSE / THIRD-PARTY-NOTICES、Windows Desktop Runtime 包提供的 MIT LICENSE、Inno Setup 许可及本项目许可。没有把项目的非商业限制施加给第三方组件。
- 草稿上传需要已登录的 GitHub CLI。已准备官方 2.103.3 便携工具，并完成真实只读登录/仓库权限检查，方法见 [GitHub 发布工具](github-publishing-access.md)。脚本优先 PATH 中的工具，缺失时校验并使用项目忽略目录的便携工具；项目调用关闭 CLI 遥测。凭据使用系统存储，不写到脚本、日志或源码包。真正上传与资产下载验收仍待正式任务。

## 本机临时验收

下面的命令只用于**用户明确允许的临时验收**。`-LocalTest` 从当前工作区制作快照，清单记录真实 HEAD 基线和 `source_kind=working-tree`，不冒充已提交源码。安装器使用单独的 `AsukaLens.LocalTest` 身份，标题标记 LOCAL TEST，且上传脚本拒绝此类资产。程序的数据约定仍与正式程序相同，不能把测试安装身份误解成自动隔离个人设置。

```powershell
$commit = git rev-parse HEAD
pwsh -File scripts/release/New-Release.ps1 -Version v1.5.0 -Commit $commit `
  -Compiler "artifacts/release-tools/inno-7.1.1/ISCC.exe" -LocalTest

pwsh -File scripts/release/Test-Release.ps1 -Directory artifacts/releases/v1.5.0-local-test
pwsh -File tests/ReleaseScripts.Tests.ps1
pwsh -File tests/ReleasePackageGuards.Tests.ps1 -Directory artifacts/releases/v1.5.0-local-test `
  -Compiler "artifacts/release-tools/inno-7.1.1/ISCC.exe"

dotnet build tests/DiskAtlas.Tests.csproj -c Release -p:TargetPlatformDisplayName=Windows -p:NuGetAudit=false
pwsh -File tests/PackageAcceptance.ps1 -Directory artifacts/releases/v1.5.0-local-test `
  -Compiler "artifacts/release-tools/inno-7.1.1/ISCC.exe"
```

示例版本对应当前项目字段；未来从项目读取匹配版本，不机械沿用 `v1.5.0`。编译器路径是本轮约定的相对位置，实际可传另一处已核验的便携编译器。

生成目录默认为 `artifacts/releases/vX.X.X-local-test/`，正式包为 `vX.X.X/`。已经存在的目录拒绝覆盖；失败只清理本次拥有的 `.stage-*`，不改动已完成的快照。中断进程可能留下锁或暂存目录：先确认没有同一打包进程，再核对归属和路径后清理，不默认抢占锁。仓库内的输出只能放在 `artifacts/` 下，外部输出需明确指定。

`PackageAcceptance.ps1` 默认只允许 LOCAL TEST；显式 `-FormalCandidate` 才允许正式身份候选包，且拒绝把 LOCAL TEST 当正式候选。两种模式均检查 HKCU/HKLM 的 32/64 位注册项、当前/公共快捷方式、默认安装位置和恢复目录，发现任一已有状态即停止，不接管已有安装。失败清理前再核对注册的安装路径归属。它在用户数据目录只创建一个随机命名的合成标记，用于验证卸载保留数据，最后删除该标记；不读取个人配置。窗口流程在独立测试宿主中加载**包内生产 DLL 和包内运行时**，传入合成扫描目录、隔离背景和诊断位置。验收宿主不能以 SW_HIDE 隐藏方式启动，否则 WinForms SplitContainer 首帧可能发生 GDI 错误。

测试还会从同一载荷编译 `1.4.1` 的专属测试安装器来验证升级/拒绝降级机制；这不是历史二进制迁移验证。测试输出含本机路径，留在 `artifacts/` 供本轮检查，公开归档只保存无路径的汇总。测试结束后清理这些夹具、工具副本、临时包和下载缓存；保留当前普通开发预览和必要证据。

### 直接发行 EXE 的本机使用流程

实际入口验收直接运行便携 ZIP 或测试安装目录内的 `AsukaLens.exe`，不复制测试宿主、不替换 DLL。2026-11-07 已补齐这一流程，见[发行入口验收记录](../iterations/2026-11-07-16-executable-workflow/README.md)。包内运行时加载、正常退出、运行中拒绝安装/卸载也用真实进程核验；这仍是安装了 SDK 的本机证据。

为避免覆盖个人背景和诊断，入口支持显式的进程环境变量 `ASUKALENS_DATA_DIR`。必须指向已创建的绝对目录，不能是卷根目录；空白、相对路径、不存在的目录或文件会终止启动，不回退个人数据目录。该入口仅重定向背景、诊断与异常报告，窗口标题追加“隔离数据”；它不是系统沙箱，也不会把普通便携使用自动改成随程序存储。应自行核对目录归属和空间，不设为用户级或系统级环境变量。

下面从项目根目录启动已提取的验收载荷，数据放到本次新建的独立目录；根据实际载荷位置调整 `$payload`：

```powershell
$payload = (Resolve-Path 'artifacts/exe-workflow/portable').Path
$data = Join-Path ([IO.Path]::GetFullPath('artifacts')) ('exe-data-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($data) | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'AsukaLens.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $payload
$start.Environment['ASUKALENS_DATA_DIR'] = $data
$app = [Diagnostics.Process]::Start($start)
```

同一目录可用于验收重启恢复；结束后先正常退出并确认进程已结束，再按归属清理。默认不设置此变量时，数据位置和既有配置格式不变。测试安装身份本身不提供数据隔离；启动安装后的 EXE 也须显式传入该变量。

本机清单覆盖目录选择与下钻、三种结果视图、两种口径、硬链接/稀疏样本、搜索排序、复制定位、普通/强制刷新、实时预览与取消恢复、背景应用/取消/重启恢复、诊断预览导出和问题记录保存编辑。边界故障继续使用相关专项回归，不把一次人工流程当作所有设备、权限或异常条件的验证。

## 正式包：只读取已提交快照

干净验证机无需安装本节的开发工具。维护者先在开发机完成包校验，再按[离线验收手册](offline-acceptance.md)使用 Windows PowerShell 5.1 核对资产、准备隔离样本并记录真实操作；工具报告不直接替代正式上传模板，也不会将开发机标记为干净系统通过。

正式发布任务获得授权后，先确定新版本、更新项目唯一数字版本并提交。确认工作区干净、目标提交已在 `main`，再不带 `-LocalTest` 运行 `New-Release.ps1`。脚本要求完整 40 位提交号、当前 HEAD 与之相同且无未提交文件，从 `git archive` 生成源码，避免打包未提交的个人文件。

四个文件统一命名：

| 文件 | 内容 |
| --- | --- |
| `AsukaLens vX.X.X.zip` | 完整自包含程序目录，解压后运行 `AsukaLens.exe` |
| `AsukaLens vX.X.X.exe` | 当前用户安装器，安装载荷与便携包相同 |
| `AsukaLens vX.X.X-source.zip` | 指定提交的源码 |
| `AsukaLens vX.X.X-source.tar.gz` | 与源码 ZIP 逐文件相同的源码 |
| `SHA256SUMS.txt` | 上述四个文件的 SHA-256 |
| `release-manifest.json` | 版本、提交、工具链、四类资产、源码和载荷的逐文件清单 |

源码保留所有可分发的 Git 跟踪文件，包括文档、测试和精简迭代证据；拒绝链接/子模块、`.git`、编译目录、运行日志、个人背景配置、证书和发行包。源码 ZIP 与 tar.gz 的文件名、长度和哈希一致，不使用 GitHub 自动源码链接代替自定义资产。当前 Git 配置的 CRLF 转换不改变已提交源码快照。

`Test-Release.ps1` 检查六个输出文件、四类资产哈希、源文件一致、包内运行时/许可、版本及测试身份，**不执行安装器，也不声称通过了运行验收**。

### v1.5.0 候选包的本机复验

用户已授权准备 v1.5.0 候选资产和本机复验，本轮不创建标签、不上传资产、不公开 Release。当前候选的资产身份与本机复验见[候选记录](releases/v1.5.0-candidate.md)。固定版本的实际更新条目见 [v1.5.0.changes.json](releases/v1.5.0.changes.json)；冻结提交后将条目、运行时与确切提交代入双语模板生成候选说明，不将未知提交伪装成最终值。

```powershell
# 仅在本机没有既有正式安装时执行；脚本先检查并拒绝任何冲突。
pwsh -File tests/PackageAcceptance.Guards.Tests.ps1
pwsh -File tests/PackageAcceptance.ps1 -Directory artifacts/releases/v1.5.0 `
  -Compiler "artifacts/release-tools/inno-7.1.1/ISCC.exe" -FormalCandidate
```

正式身份模式使用本轮新建的安装目录，结束时卸载，仅保留验收结果；不会自动接管或卸载用户原有安装。合成旧版安装器仅验证相同身份的升级机制，不代表历史正式安装器迁移。还需单独启动便携与安装后真实 EXE 检查使用流程，配置指向本轮隔离目录。候选包与元数据保留一套，原始路径日志、合成夹具及重复工具产物收尾清理。验收记录绑定资产清单 SHA-256；新增文档证据提交不会改变既有候选包的冻结提交。

## 安装、升级与卸载约定

- 当前候选安装器支持 Windows 10 22H2 x64 和 Windows 11 x64，按当前用户安装到本地 Programs；不请求管理员权限，不添加服务、自启、文件关联或联网下载步骤。干净环境验收状态以当前候选记录为准。
- 提供简体中文/English 安装向导、完整许可和双语安装提示。开始菜单快捷方式默认创建，桌面快捷方式可选。
- 正式安装器固定 AppId；新版本沿用旧目录并保留用户数据，低版本拒绝覆盖。无关非空目录拒绝安装，避免覆盖用户文件。
- 应用入口代码在整个进程生命周期持有命名互斥体；安装/卸载要求先关闭窗口，不结束用户进程。初次包级检查使用同名合成互斥体，后续已用本机真正的便携及安装 EXE 验证阻止安装/卸载；干净环境仍未验证。其他文件占用仍可能导致失败；初次包级检查使用锁定 EXE 验证了失败回滚及旧载荷哈希保持一致。
- 覆盖前逐文件备份已有程序文件，普通安装失败后恢复，成功或恢复完成后清理备份。Inno 自带回滚不能恢复已覆盖的旧字节，因此这部分由安装脚本补足；本轮以先改写合成文件、再遇到锁定 EXE 的方式核验。恢复失败或进程被强制中断时，备份留在当前用户 AppData 的 `AsukaLens/InstallerRecovery-<安装身份>/`，后续安装会停止并提示检查；`destinations.txt` 按行记录目的路径，第 1 行对应 `0.bak`，第 2 行对应 `1.bak`，依次类推。这些本地文件含安装路径，不上传；不能把普通失败恢复测试当成断电恢复保证。
- 卸载只移除安装器拥有的文件、快捷方式和注册项；保留 AppData 内背景、诊断、问题记录以及安装目录中未知文件。不递归清空用户目录。
- “便携”指免安装程序目录。个人数据仍使用当前用户 AppData，不是跟随 U 盘移动的数据模式。
- 当前产物未代码签名；正式说明必须如实标明。不会使用开发者私人证书或伪造发行者签名。

## 草稿上传与验收门槛

完成正式任务要求的受控验证后，按 [验收模板](release-acceptance.example.json)填写真实结果，并填入此次 `release-manifest.json` 的 SHA-256。所有门槛必须是 `passed`；不能把 `not tested` 或本机替代测量改写为通过。发布说明另写 UTF-8 文件，含 `## 简体中文`、`## English`、精确版本、环境、运行时内含、变更、已知限制、安装/便携方法与四类下载链接。

发布说明从[双语模板](release-notes.template.md)另存，按确切清单填写 `{{VERSION}}`、`{{RUNTIME}}`、`{{COMMIT}}` 及两种语言的更新/修复内容，逐项复核固定文字是否仍适用。当前模板供首次正式发布准备，不能直接上传占位符。保留两种语言各自的七个三级标题；下载区分别提供四类资产、SHA256SUMS 和清单的准确链接。上传入口会在 GitHub 调用前校验这些内容，也会拒绝用注释、空栏目或代码示例填充必需说明。校验不替代人工核对文案、签名状态及下载实际可用性。

可先单独运行 `pwsh -File tests/ReleaseNotes.Tests.ps1` 检查模板与校验边界；完整入口控制回归仍使用 `tests/DraftUpload.Tests.ps1`。本机证据与仍需正式任务/独立环境完成的事项集中列在[发布前缺口清单](release-readiness.md)。

**本轮未创建标签。** 未来仅在明确的正式发布任务中创建并推送 `vX.X.X` 标签，必须指向上述提交；已存在标签只核查，绝不移动。上传脚本要求远程已经存在正确标签，本身不创建或重写标签。

```powershell
# 仅展示计划，不调用 GitHub；本地资产和验收门槛仍必须有效。
pwsh -File scripts/release/Upload-Draft.ps1 -Directory "artifacts/releases/vX.X.X" `
  -Notes "已审查的双语发布说明.md" -Acceptance "已完成的验收.json" -WhatIf

# 只有正式任务授权上传后执行。默认会确认目标，已获授权时可用 -Confirm:$false。
pwsh -File scripts/release/Upload-Draft.ps1 -Directory "artifacts/releases/vX.X.X" `
  -Notes "已审查的双语发布说明.md" -Acceptance "已完成的验收.json"
```

脚本限定目标仓库，核对提交和标签，拒绝测试包和已发布版本。发布说明须含独立的精确版本号，例如 `v1.5.00`、`v1.5.0-preview` 不能代替 `v1.5.0`。按 [GitHub 官方 API](https://docs.github.com/en/rest/releases/releases#list-releases)使用有推送权限的登录读取草稿列表，避免把“按标签读取已发布版本”接口用于草稿。同版本草稿需显式 `-Resume`；草稿标题必须为 `AsukaLens vX.X.X`，标签和说明必须与本次审查内容一致，说明只容许换行形式及末尾换行差异。内容不一致时停止，不能自动改写已有草稿。已有同名资产必须 digest 相同，绝不使用 `--clobber`。上传后下载六个文件重新校验，仍保持草稿，**没有自动公开发布代码**。上传中途失败保留草稿供检查，不能当成正式成功；不要在脚本运行时同时手工发布或编辑草稿。

无需发行包或 GitHub 登录，可以运行完整上传控制流程的离线回归：

```powershell
pwsh -File tests/DraftUpload.Tests.ps1
```

它复制未经修改的生产上传脚本，通过本地 `gh` 替身模拟分页、附注标签、创建草稿、上传中断/续传、下载损坏/缺失/多余文件和上传期间发布等情况；核对拒绝后没有写入、续传不重复上传、下载暂存被清理。合成资产是不可执行的短文本，静态包校验器使用测试替身，**不等于再次验证 ZIP、安装器可用性或真实 GitHub 接口**。测试不使用凭据、不联网、不创建真实标签或草稿；临时 PATH 与夹具均在结束时恢复/清理。

## 本轮证据与仍未验证的范围

本轮已进行脚本边界测试、真实四类临时资产静态核验、两类源码包独立构建、包内生产程序集与包内运行时的主要功能检查、当前用户安装/合成升级/降级拒绝/占用失败回滚/卸载、中文安装向导观察。结果与清理见本轮 `iterations/` 记录。

后续已用真正的便携与安装 EXE 补齐本机人工使用流程，详见[验收记录](../iterations/2026-11-07-16-executable-workflow/README.md)。本机原有个人数据的文件名及内容哈希在验收前后保持一致，原始库存、诊断和桌面截图不公开保存。

仍需正式发布前验证：**未预装 .NET 的干净 Windows 11 x64 环境、真实历史发行版迁移（如存在）、远程草稿上传和下载核验**。正式候选包仍须对其确切资产重新验收，不能将本轮临时包的证据绑定到另一份清单。本机已安装 SDK；包内运行时路径核验不能代替干净系统验收。没有启用 Windows 可选组件、安装虚拟机或进行真实 GitHub 上传。

后续本机检查已补充上述离线流程，修复版本子串误匹配并核验草稿说明一致性。用户当前没有独立 Windows 测试环境，因此干净系统门槛继续保留。Windows Home 不受 [官方 Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/) 支持；不通过修改宿主运行时或绕过版本限制来宣称完成验收。
