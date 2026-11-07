# .NET 10 LTS 迁移方案

状态：2026-11-07 已完成静态评估，环境方案待用户选择；尚未安装 SDK、修改目标框架或验证 .NET 10 运行结果。本方案只处理运行环境迁移。

## 判断与现状

建议迁移到 .NET 10 LTS。微软当前支持策略列出 .NET 9 于 2026-11-10 结束支持，.NET 10 支持至 2028-11-14。升级目的为保持运行环境受支持，不承诺扫描自动提速。[支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)

迁移前代码基线为 `4e6c07f349fd858a756e9674ef5f81dd28a8f73c`。应用和测试项目均为 `net9.0-windows`，无第三方 NuGet 包，没有 `global.json`，包源已清空。本机是 Windows 11 x64（系统构建 26100），已装 SDK 9.0.203 / 9.0.301，最新已装 Windows Desktop Runtime 为 9.0.6；未检出 .NET 10。

微软当前列出仍在支持期内的 Windows 11，以及指定 Windows 10 LTSC / Enterprise 版本支持 .NET 10。本项目先以本机 Windows 11 x64 为实测环境；其他 Windows 版本和架构不能仅凭框架支持表标为软件已验收。公共说明中现有“Windows 10/11”和“本机已具备，无需额外安装”需区分操作系统兼容范围、运行依赖与实际验证。[Windows 安装与支持范围](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

## 环境方案

| 方向 | 用户影响 | 建议 |
| --- | --- | --- |
| 项目内隔离 SDK | 官方 x64 SDK 解压到已忽略的 `artifacts/dotnet10/`，仅构建/启动进程使用；用项目提供的预览入口启动，未装系统运行时时不能直接双击框架依赖 EXE | 推荐先采用，符合本轮不顺带安装系统依赖的约定 |
| 系统安装 SDK | 安装官方 .NET 10 x64 SDK，含 Desktop Runtime；保留现有 .NET 9，普通 `dotnet` 命令与新 EXE 可直接使用 | 适合准备长期在本机开发，需用户选择系统安装方向 |

两种方向均从微软官方发行元数据选择当前稳定的 .NET 10 SDK，下载前记录版本、来源及官方 SHA-512；校验后使用，不用预览 SDK。实施时记录实际版本及大小，不预先猜测下载地址。隔离 SDK 属于必要开发依赖，不进入 Git / 迭代补丁 / Release；校验及解压后删除本轮下载压缩包。

SDK 包含 .NET 和 Windows Desktop Runtime；单独的普通 .NET Runtime 不足以运行 WinForms。隔离方向的启动脚本明确使用本地 `dotnet.exe` 启动应用 DLL，并向测试子进程传递相同运行环境，不依赖机器碰巧装有 .NET 10。不持久改写系统 PATH。[SDK 与 Runtime 的区别](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

## 代码与入口改动

1. 应用与测试项目一起改为 `net10.0-windows`。新增 `global.json`，记录验收 SDK 版本，仅允许同一 .NET 10 feature band 的补丁升级，禁止预览版；后续更新 feature band 单独核验。不同时改扫描算法、背景配置格式或产品版本号。
2. 同步 `build.ps1` 的测试 DLL / 正在运行检查路径；本轮只检查脚本语法和路径，不执行发布打包。普通构建仍使用 `dotnet build`。
3. 隔离方向补充可直接使用的构建与预览入口，缺 SDK 时给明确提示，不在软件启动时自动下载。使用已安装 SDK 的方向保留普通命令。说明测试子进程的 `dotnet` 解析方式。
4. 同步 AGENTS、README、使用说明及现状中的当前命令与入口；历史迭代保持原样。完成后仅一个入口标为“当前预览”。

## 兼容性核对

| 风险 | 已核查源码 | 实施时验证 |
| --- | --- | --- |
| System.Drawing 部分 GDI+ 错误由 OutOfMemoryException 改为 ExternalException | `BackgroundSettings.IsFileError`、`WallpaperRaster`、`TreemapControl` 已处理两者 | 损坏图片、绘制降级、资源释放；异常报告仍只输出白名单字段 |
| WinForms 剪贴板与拖放实现变化 | 复制使用 `Clipboard.SetText`；拖目录使用内置 `FileDrop` 字符串数组；未使用自定义对象序列化 | 真实目录拖放和复制路径，使用合成目录，不读取/归档原剪贴板内容 |
| 废弃 API 与编译器变化 | 未发现旧 Form.Closing/Closed、Clipboard.GetData、旧菜单/网格 API；无 WPF 引用 | 两项目重新编译，逐项处理新增警告，不全局屏蔽 |
| DPI、首帧和浅色自绘 | MainForm 手动缩放与首帧准备；背景窗使用 DPI 缩放 | 实际窗口启动、144/96 DPI、窄窗、焦点、主题切换、关闭重开 |
| 原生 NTFS 元信息与增量读取 | 继续使用现有 Windows API 和取消隔离 | 核心、高级、日志协议与真实 NTFS 样本，跳过不计为通过 |
| 背景持久化与错误报告 | 背景 v1/v2 格式不变，报告保持白名单 | 隔离配置的子进程重启、旧配置、损坏配置、失败保留及隐私专项 |

以上是静态检查结果，不替代 .NET 10 构建与运行验证。[兼容性索引](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10)、[GDI+ 异常变化](https://learn.microsoft.com/en-us/dotnet/core/compatibility/windows-forms/10.0/system-drawing-outofmemory-externalexception)、[WinForms 废弃 API](https://learn.microsoft.com/en-us/dotnet/core/compatibility/windows-forms/10.0/obsolete-apis)、[剪贴板与拖放](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/migration/clipboard-dataobject-net10)。

## 验收与回退

- 保留迁移前应用构建和上述 Git 基线，现有 .NET 9 系统依赖不卸载。以既有迭代记录中的通过结果作为对照，本次评估没有重复跑 .NET 9 测试。
- .NET 10 应用与测试构建均要求无未解决警告/错误。运行核心 38 项、高级 25 项、日志 16 项；UI 10 组和背景 7 组分别运行实际 144 DPI 与独立 96 DPI；异常报告 3 组。实际数量若因必要新增检查变化，应记录新增场景。
- 检查七项保留功能，特别是完整扫描、增量/回退、取消、两种口径与硬链接；实际观察默认/自定义背景的启动、设置与重开、目录拖放和复制路径。配置只指向本轮合成目录。
- 明确记录真实运行时版本和测试子进程版本，防止构建用了新 SDK、运行却落在旧宿主。没有跨显示器、其他操作系统或整盘性能数据时如实说明。
- 任一核心检查失败则修复后重测相关范围，不能把新入口标为可用。必要回退通过新提交恢复迁移前文件，不强推或重写历史；保留的旧构建仍可用。背景数据格式不变，不需要搬迁个人设置。
- 验收通过后归档补丁/清单、清理本轮中间物和重复下载，保留当前可用预览与所需 SDK；提交并推送 main。旧预览在新版本验证前保留，之后仅清理确认可再生的重复产物。
- 本轮不打包，不创建标签/Release，也不实施完整日志系统或性能重构。

## 待选择

建议采用“项目内隔离 SDK”，完成上述迁移与验收。若更希望长期直接双击 EXE 和使用系统 `dotnet`，可选择“系统安装 SDK”。项目规则将依赖大版本列为需先确认方向的改动，因此本次评估不替用户执行这项环境选择。
