# 开发验证与可选基准

从项目根目录运行。普通迭代按 [AGENTS.md](../AGENTS.md) 选择相关检查；不要把全部性能基准作为每次提交的前置条件。PERF-001 已按用户要求延期到初版后，本页保留方法，不代表应立即追测。

```powershell
dotnet build tests/DiskAtlas.Tests.csproj --configuration Release -p:TargetPlatformDisplayName=Windows -p:NuGetAudit=false
dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll
```

主窗检查用 `--ui-regression`，另在独立进程追加 `--dpi-96`；结果范围、三类不完整快照与基本键盘操作见[界面验收](../iterations/2026-11-07-12-result-information/README.md)。诊断专项使用 `--diagnostics-tests`、`--diagnostics-ui` 和后者的 `--dpi-96`；背景专项使用 `--background-tests` 及 `--dpi-96`，异常白名单使用 `--error-report-tests`。需要正常的可交互 Windows 桌面。

## 文档预览

`--readme-preview docs/images/overview.png` 在 96 DPI 虚拟化进程中用生产 MainForm 和内存合成数据绘制界面，仅写入指定文档图片，不扫描真实目录或读取个人设置，已有图片拒绝覆盖。`--result-info-preview` 为手动状态观察入口。主程序源码未变时，不为修改文档重新运行整套功能回归。

## 既有检查与边界


测试入口会输出实际 .NET、WinForms 和进程架构；背景重启检查同时核对子进程版本。框架迁移另运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --desktop-compatibility`，使用合成目录检查生产拖放处理和复制路径；它会把测试路径写入系统剪贴板并在检查后清除，不读取原剪贴板内容。

`tests` 为无需测试框架依赖的断言程序，38 项检查覆盖扫描、隐藏文件、根目录消失、取消、140 层目录、联接循环及扫描期间链接替换、查询与排序、精确类型筛选、分页和极端图块边界；检查并行 / 串行结果逐项一致、修改时间、并发取消、错误传播及跳过统计；还检查文件存储、无分配路径比较、完成前预览、快照隔离、并发预览查询和目录浏览。80 组随机矩形图检查面积比例、完整覆盖、边界和不重叠。10 万文件固定种子基准同时与参考算法核对结果，并输出内存分配和耗时。

另外 25 项功能检查覆盖硬链接去重、压缩 / 稀疏占用、未知占用、三种策略、日志解析与断档回退、保留时间戳的深层修改、目录移动 / 新增 / 替换、根目录外硬链接写入、持续打开的写句柄、刷新期间变更重放及取消隔离，以及跨目录让步计数、取消边界、无节拍模式、任务峰值、阶段估算和真实目录任务计数。构建测试项目后运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --advanced-tests`；两组共 63 项检查。`--ui-regression` 当前包含 14 组界面集成检查，其中一组直接使用正式扫描协调器，另覆盖进度终态与迟到回调、阶段切换、动效关闭、图块面积和命中稳定性以及尺寸变化。另检查头像透明度与缩放复用、局部重绘、静态缓存复用/失效/容量上限/释放及降级绘制一致性。

可选的合成图表绘制基准：`dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --treemap-benchmark after`。使用正常桌面环境，测量 12 个带嵌套内容的文件夹和 241 个平坦文件的悬停处理、离屏绘制耗时、托管分配和刷新范围。结果保存在 `artifacts/treemap-after.json`，已有报告需先归档再运行。它关闭动效，只测已预热的绘制成本，不等于实际鼠标到屏幕延迟或完整扫描性能。历史基线与方法见本次修正记录。

三种真实策略的可选诊断基准：构建测试项目后运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --policy-benchmark`。它创建并自动删除 15,200 个合成小文件，分别测量完整扫描和无日志的无变化刷新，预热后按平衡顺序每模式运行 6 次并逐项比对结果。汇总墙钟、进程 CPU、总托管分配与任务峰值；不测量实际前台应用响应或物理磁盘 I/O。结果保存在 `artifacts/policy-benchmark-results.json`；再次运行前应先归档并移除上一份同名结果，避免覆盖证据。

可选的真实主窗响应基准：在正常 Windows 桌面运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --response-benchmark`。使用 64,000 个合成小文件和隔离诊断配置，按平衡顺序测量扫描、首个预览观察、列表可用、搜索、取消、CPU、分配及采样工作集，并逐项比对完整结果。报告 `artifacts/scan-response-results.json` 已存在时拒绝覆盖；应先归档旧报告。定时器调度间隔不是帧率，采样工作集不是精确峰值，预热样本不代表整盘冷扫描。方法、基线和已确认的优化范围见[扫描性能方案](scan-performance-plan.md)。

高速文件并行边界检查：`dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --parallel-file-tests`。检查共享读取额度、取消回收、缓冲上限、原策略隔离、硬链接、读取失败和枚举后的文件替换，使用合成文件与受控故障。

大型混合目录基准：先运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --mixed-benchmark before`，修改后运行相同命令并把末尾改为 `after`。它创建并保留约 29.5 万个合成文件，在同一夹具上对自动/高速交替各测 6 次；两份报告位于 `artifacts/mixed-scan-*.json`，已有报告拒绝覆盖。完成对比并归档后，核对 `artifacts/mixed-scan-owned.txt` 所标识的本轮夹具再清理。计时不含 UI，结果一致性逐项核对。

用户明确指定目录时可运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --folder-benchmark "D:\ScanDemo"`。真实主窗按自动/高速/高速/自动做四次只读完整扫描，报告留在系统临时目录的独立 `AsukaLens-scan-comparison-*` 文件夹中，不写入项目。没有额外预热或清空缓存；目录内容变化会标记为不同快照，不宣称等工作量。报告和运行诊断包含实际统计，仅供本机核查，不提交或发布。 若逐项指纹不一致，可在路径前加 `--pair` 做一次自动/高速的针对性诊断，仅输出差异计数；它额外保留逐项快照，计时不作性能结论。比较工具自检使用 `--folder-comparison-tests`。

另有 16 项日志边界检查：构建后运行 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --journal-tests`，覆盖只读请求 V1/V0 协商、V2/V3 记录、128 位身份、截断/重建/回退游标、卷不一致、未知格式、读取上限、取消及保守回退；三组共 79 项检查。界面集成另验证最近完成扫描的详情窗口与实际刷新方式。真实卷日志不可读时，模拟协议检查不能证明真实卷日志读取成功。

增量刷新只在本次运行保留的完整快照上进行，关闭后不保存缓存，重启后的第一次扫描为完整扫描。界面区分“日志增量”“元信息核验”“完整扫描”。元信息核验模式仍走访所有目录和文件元信息，主要复用树节点内容及已核实的存储信息，不能保证像日志模式一样显著减少磁盘访问。日志丢失连续性、格式无效、记录超限、旧快照不完整或身份异常时采用保守回退。详情提供核验目录/文件、重扫目录、复用文件、处理日志记录、原因和系统错误码，以及准备/读取整理耗时。Ctrl+F5 或重新扫描按钮的右键菜单可强制完整扫描。

可选刷新对比：`dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --refresh-benchmark`。同一组 2,400 文件、61 目录，预热后按轮换和反向顺序各运行 8 次，逐项比对完整扫描、无日志核验、模拟连续日志与本机实际路径。结果保存在 `artifacts/refresh-benchmark.json`，再次运行前先归档原报告。它测量墙钟、进程 CPU 和总托管分配，不测物理 I/O 或整盘冷启动；模拟日志仅验证复用工作量，不能当作本机实测加速。没有更改系统权限、日志设置或创建驻留服务。

可选的真实文件系统扫描基准（会创建并自动删除 72,000 个小文件，耗时较长）：

```powershell
dotnet run --project tests/DiskAtlas.Tests.csproj --configuration Release -p:TargetPlatformDisplayName=Windows -- --scan-benchmark
```

分别测量多目录和单层目录，预热后交替运行 9 次并报告中位数，包含扫描、汇总及排序；核对每次的文件数、目录数、字节总数与跳过数。如果项目根目录保留 `空间透镜-1.2.0-Windows.zip`，会直接从中加载旧版扫描器作为参考；否则比较新版串行和 8 路并行。注意 1.4 版额外逐文件读取实际分配大小、身份和硬链接数，旧版没有这部分工作，不能据此作同功能的速度比较。1.3 版历史基准见审查记录。

另外提供两个可选检查：

```powershell
# 20 万文件的历史模型内存对比；未提供 1.2 基准包时明确跳过
dotnet run --project tests/DiskAtlas.Tests.csproj --configuration Release -p:TargetPlatformDisplayName=Windows -- --memory-benchmark

# 在软件自身的 WinForms 窗口中测试预览、导航、取消恢复和最终切换
dotnet run --project tests/DiskAtlas.Tests.csproj --configuration Release -p:TargetPlatformDisplayName=Windows -- --ui-regression
```

旧版压缩包不随当前项目保留；历史内存对比在缺少可选的 1.2 基准包时明确跳过，扫描基准仍可比较当前版本的串行与并行。内存基准的目录前缀包含 90 个填充字符，以观察重复路径的成本；不包含 1.4 新增的存储信息记录，不等同于实际进程峰值内存。1.3 版减少约 72% 的数字是历史结果。界面集成测试使用真实小文件和可控等待点，不把人为等待计入性能结果。

三轮审查的发现、修改、实测证据与验证范围见 [优化审查记录.md](../优化审查记录.md)。

## 发布脚本与本机临时包

脚本边界：`pwsh -File tests/ReleaseScripts.Tests.ps1`。用户明确允许临时包后，按[发布说明](releasing.md)生成 `-LocalTest` 资产，再执行 `Test-Release.ps1` 和 `PackageAcceptance.ps1`。后者需要先构建测试项目，使用包内生产 DLL、包内运行时和隔离配置，不读取个人扫描数据；会验证专属测试安装身份并清理注册项与快捷方式。

`--package-smoke` 仅供上述脚本在可丢弃载荷副本中运行，不能直接在普通测试 DLL 目录下冒充包内运行验证。包级工具的本机日志含路径，不进入公开归档。干净系统验收和真实远程草稿验证单独记录，不因本机通过而勾选。

真正发行入口的本机人工流程另见[启动与数据隔离方法](releasing.md#直接发行-exe-的本机使用流程)及[验收记录](../iterations/2026-11-07-16-executable-workflow/README.md)。入口目录解析与隔离存储检查使用 `dotnet tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll --app-data-location-tests`，共 12 项；覆盖默认兼容、中文目录、非法路径拒绝以及诊断/异常写入位置。该检查不启动发行 EXE，不能替代实际窗口验收。

上传控制流程可独立执行 `pwsh -File tests/DraftUpload.Tests.ps1`：实际生产脚本搭配本地 CLI 与包校验替身，检查目标、版本、草稿内容、断点续传、资产冲突、下载复核、错误传播和清理。无须构建应用、生成发行包或登录 GitHub；不把离线测试通过当成真实远程验收。

离线验收工具检查使用 **64 位 Windows PowerShell 5.1**：`powershell.exe -NoProfile -File tests/OfflineAcceptance.Tests.ps1`，可用 `-Summary <新报告.json>` 保存白名单汇总。它在已有现代 .NET 的开发机验证真实环境探测，生成小型合成资产与 .NET Framework 子进程，检查校验、隔离、CLI、状态与清理；无需重新构建应用或生成发行包。目标机只需复制两个验收脚本，按[操作手册](offline-acceptance.md)执行。合成子进程不是 AsukaLens，不能据此填写真实发行入口或干净系统通过。
