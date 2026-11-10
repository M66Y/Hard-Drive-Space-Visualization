# AsukaLens v1.5.0

## 简体中文

### 更新内容

- 保留目录选择、空间分布、文件列表、大文件、类型统计、文件定位与复制路径，以及文件大小/实际占用两种口径。
- 扫描中提供目录任务估算进度、移动头像、部分预览、阶段与并发说明、取消和旧结果恢复；增加有限图块动效及减弱选项。
- NTFS 日志连续可读时按变化刷新；失效时明确回退核验。自动、低占用和高速策略具有独立调度约束，高速完整扫描支持目录优先与空闲文件读取额度。
- 本地背景设置支持图片导入、缩放焦点、预览、自动/手动强调色、保存恢复及默认重置。
- 新增本地诊断窗口、开关、8 MiB/14 天运行日志保留、筛选清空与选中预览导出；问题记录由用户主动填写。
- 迁移至 .NET 10 LTS，统一源码启动与构建辨识，补充中英文用户说明、许可和离线验收工具；准备四类版本快照资产。

### 修复

- 修正背景持久化、损坏配置回退及异步关闭期间的资源释放；改善启动首帧和图块悬停重绘。
- 异常和诊断采用白名单字段，避免将原始异常消息、堆栈与个人路径写入分享内容；主动问题记录不混入运行诊断导出。
- 明确统计卡、搜索和筛选的范围，提示未知占用、未读取项及取消/失败时保留的旧结果。
- 为安装器增加运行中阻止安装/卸载、拒绝降级、无关目录保护和替换失败恢复；正式候选验收拒绝覆盖现有安装。

### 已知限制

- 高速模式并非在所有目录上都快于自动模式，稳定提速问题留待初版后的同级优化。
- 系统变化日志不可用时仍需核验目录和文件元信息；扫描缓存仅在本次进程内保留。
- 实际占用不包含所有文件系统开销，不等于删除文件后释放的空间；未知占用与未读取项会单独提示。
- 界面为简体中文。其他 Windows 版本与架构、跨显示器 DPI 等未覆盖情形不属于已验证范围。
- 干净 Windows 10 和 Windows 11 环境尚未验收。
- 本次确切资产尚未在 Windows 10 虚拟机中实测；前一候选已在该环境完成主要流程及合成升级/回退测试。

### 适用环境与依赖

Windows 10 22H2 x64 和 Windows 11 x64，普通用户权限。本版便携包与安装器内含 .NET 10.0.14 Windows Desktop 运行时，无需另外安装现代 .NET；从源码构建仍需要仓库 `global.json` 指定的 .NET SDK。微软的 .NET 10 官方 Windows 10 支持限于指定 LTSC / Enterprise 版本；其他版本为本项目的兼容性测试范围。

当前文件未代码签名，系统可能显示未知发行者提示。许可、运行环境和验收范围以本次确切资产为准。

### 安装与便携使用

- 便携版：完整解压 ZIP，运行 `AsukaLens.exe`，选择目录开始扫描。便携版的设置仍位于当前用户 AppData，不随程序文件夹移动。
- 安装版：运行 EXE 安装器，按当前用户安装。升级前正常退出所有 AsukaLens 窗口；卸载保留背景、诊断和问题记录。
- 这是本项目首个正式发行版本。旧本地预览的手动替换不代表已验证历史安装器迁移。

### 下载与校验

| 文件 | 用途 |
| --- | --- |
| [AsukaLens v1.5.0.zip](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0.zip) | 自包含便携版 |
| [AsukaLens v1.5.0.exe](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0.exe) | 当前用户安装器 |
| [AsukaLens v1.5.0-source.zip](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0-source.zip) | 源码 ZIP |
| [AsukaLens v1.5.0-source.tar.gz](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0-source.tar.gz) | 同一源码的 tar.gz |
| [SHA256SUMS.txt](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/SHA256SUMS.txt) | 四类资产的 SHA-256 |
| [release-manifest.json](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/release-manifest.json) | 版本、提交、工具链及逐文件清单 |

下载后用 PowerShell 的 `Get-FileHash -Algorithm SHA256 -LiteralPath '下载文件'` 对照 `SHA256SUMS.txt`。草稿阶段链接不代表公众可下载；正式公开后才可作为下载入口。

### 源码与许可

对应提交：[4d473747f88370a1b411129743e2169229c78dcc](https://github.com/M66Y/Hard-Drive-Space-Visualization/tree/4d473747f88370a1b411129743e2169229c78dcc)。两种源码包来自同一提交；从源码构建步骤见包内中英文 README。

AsukaLens — M66Y。项目代码使用 PolyForm Noncommercial 1.0.0；文档和 AI 生成的主题图片使用 CC BY-NC 4.0。第三方运行时与安装器组件保留原许可，完整内容随包提供。项目是源码可用的非商业软件，不宣称 OSI 开源。

## English

### Changes

- Retains folder selection, treemap, file lists, largest files, type statistics, file location and path copying, and both logical and allocated size metrics.
- Adds estimated directory-task progress, a moving avatar, partial previews, stage and concurrency details, cancellation and previous-result recovery, plus bounded tile animation and a reduced-motion option.
- Uses continuous readable NTFS journal evidence for change-based refresh, with explicit verification fallback. Automatic, Low-impact and High-speed have separate scheduling constraints; full High-speed scans prioritize directories and use spare capacity for file reads.
- Local backgrounds support image import, zoom and focus, preview, automatic/manual accent colors, persistence and reset.
- Adds a local diagnostics window, logging toggle, 8 MiB/14-day runtime retention, filtering, clearing and selected preview export; problem notes are entered voluntarily.
- Moves to .NET 10 LTS, adds a consistent source launcher and build identity, bilingual user documentation, licensing and offline acceptance tools, and prepares four snapshot asset formats.

### Fixes

- Fixes background persistence, corrupt-configuration fallback and resource disposal during asynchronous shutdown; improves the initial paint and tile hover redraw.
- Uses allowlisted fields for errors and diagnostics to exclude raw exception messages, stack traces and personal paths from shareable records; voluntary notes are excluded from runtime exports.
- Clarifies card, search and filter scope, unknown allocation, unread entries and the previous result retained after cancellation or failure.
- Adds installer protections for running applications, downgrades and unrelated directories, with recovery after a failed replacement; formal candidate acceptance refuses to overwrite existing installations.

### Known limitations

- High-speed mode is not consistently faster than Automatic for every folder. Further tuning is deferred until after the initial delivery.
- When the system change journal is unavailable, refresh still verifies folder and file metadata. Scan caches last only for the current process.
- Allocated size excludes some filesystem overhead and is not the space freed by deletion. Unknown allocation and unread entries are reported separately.
- The application UI is Simplified Chinese. Other Windows versions and architectures, and untested cross-monitor DPI scenarios, are outside the validated scope.
- Clean Windows 10 and Windows 11 environments have not been validated.
- These exact assets have not been tested in the Windows 10 VM; a previous candidate completed core workflows and synthetic upgrade/recovery checks there.

### Requirements

Windows 10 22H2 x64 and Windows 11 x64, using a standard user account. Both the portable package and installer include the .NET 10.0.14 Windows Desktop runtime; no separate modern .NET installation is required. Source builds require the SDK specified by the repository's `global.json`. Microsoft's official .NET 10 Windows 10 support is limited to specified LTSC / Enterprise editions; other editions are covered by project compatibility testing.

These files are currently unsigned; Windows may display an unknown-publisher prompt. License, platform and acceptance statements apply to these exact assets.

### Installation and portable use

- Portable: extract the entire ZIP, run `AsukaLens.exe`, and choose a folder. Settings remain in the current user's AppData; they do not travel with the portable program folder.
- Installer: run the EXE to install for the current user. Close all AsukaLens windows before upgrading. Uninstall preserves backgrounds, diagnostics and problem notes.
- This is the first formal release. Replacing historical local previews manually is not evidence of migration from a historical installer.

### Downloads and checksums

| File | Purpose |
| --- | --- |
| [AsukaLens v1.5.0.zip](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0.zip) | Self-contained portable package |
| [AsukaLens v1.5.0.exe](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0.exe) | Per-user installer |
| [AsukaLens v1.5.0-source.zip](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0-source.zip) | Source ZIP |
| [AsukaLens v1.5.0-source.tar.gz](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/AsukaLens%20v1.5.0-source.tar.gz) | Identical source in tar.gz format |
| [SHA256SUMS.txt](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/SHA256SUMS.txt) | SHA-256 for all four assets |
| [release-manifest.json](https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.5.0/release-manifest.json) | Version, commit, toolchain and file inventory |

Use PowerShell `Get-FileHash -Algorithm SHA256 -LiteralPath 'downloaded-file'` and compare with `SHA256SUMS.txt`. Draft links do not imply public availability; downloads become public only after publication.

### Source and licensing

Source commit: [4d473747f88370a1b411129743e2169229c78dcc](https://github.com/M66Y/Hard-Drive-Space-Visualization/tree/4d473747f88370a1b411129743e2169229c78dcc). Both source archives represent this commit. See the included bilingual READMEs for build instructions.

AsukaLens — M66Y. Project code uses PolyForm Noncommercial 1.0.0; documentation and AI-generated theme artwork use CC BY-NC 4.0. Third-party runtime and installer components retain their own licenses, included in the packages. This is source-available, noncommercial software, not OSI-approved open source.
