# GitHub 发布工具与只读检查

2026-11-07 已准备官方 GitHub CLI **2.103.3 Windows x64 便携版**，位于本机忽略目录 `artifacts/release-tools/gh-2.103.3/`。版本来源、下载 URL、ZIP/官方校验文件/EXE 的 SHA-256 保存在 [github-cli.json](../scripts/release/github-cli.json)。工具目录约 39.85 MiB，包含 EXE 与许可；它是后续发布依赖，未加入 Git，也没有改动系统 PATH。

本机浏览器登录已由用户本人完成，并确认使用系统凭据库。真实只读查询能够读取指定仓库，返回推送权限，Release 数量为 0；这不证明真正资产上传已经验收。用户退出登录、凭据过期、权限或网络变化后，应重新检查，不能将本记录永久当作可用证明。

## 日常检查

```powershell
pwsh -NoProfile -File scripts/release/Test-GitHubAccess.ps1
```

探针优先使用 PATH 中的 `gh`，没有时使用上述便携工具并核对 EXE 哈希。只检查版本、所需命令参数、当前 github.com 登录、仓库读取、接口返回的推送权限和分页 Release 列表，不创建标签、草稿或资产。每个子进程限时 30 秒；失败时返回固定阶段状态，不打印原始错误或认证输出。

结果中的 `status=read-checks-passed` 仅表示只读前置检查成功。`authenticated`、`repository_readable`、`reported_push_permission` 分别描述不同条件，`upload_tested` 始终为 false。未登录为 `authentication-required`，权限不足为 `push-permission-missing`，网络/API 异常为对应检查失败；此脚本输出诊断对象，不用进程退出码代替这些字段，也不生成正式发布验收凭据。

真实上传脚本同样支持上述工具选择顺序。`-WhatIf` 保持原约定，在本地验收门槛通过后仅展示计划，不调用 GitHub；它不代替本探针或真实远程验收。正式上传仍须先完成[发布前缺口](release-readiness.md)中的候选资产、干净系统和授权条件。

## 认证与隐私

GitHub CLI 这一版本支持使用遥测；项目的探针和上传调用均显式传入 `GH_TELEMETRY=false`，探针另关闭更新通知和交互提示。上传调用结束或失败后恢复调用者原有环境变量，不修改用户级/系统级设置。此约束针对通过项目脚本执行的调用；直接在终端运行原生 `gh` 时需自行设置该进程环境。见[官方环境变量说明](https://cli.github.com/manual/gh_help_environment)。

需要重新登录时，在项目根目录运行以下代码，并由账户本人完成浏览器授权。不要使用 `--insecure-storage`，不要把令牌、设备码或原始认证输出放进项目文件。Git 凭据已经可用时，登录流程询问是否用 CLI 认证 Git，可以选择 No，保持现有 Git 凭据方式。

```powershell
. ./scripts/release/Release.Common.ps1
$gh = Resolve-ReleaseGitHubCli
$previousTelemetry = [Environment]::GetEnvironmentVariable('GH_TELEMETRY', 'Process')
try {
    $env:GH_TELEMETRY = 'false'
    & $gh auth login --hostname github.com --git-protocol https --web --skip-ssh-key
} finally {
    [Environment]::SetEnvironmentVariable('GH_TELEMETRY', $previousTelemetry, 'Process')
}
```

官方登录可能在凭据库不可用时回退到明文存储，因此本轮另外只在内存中核对认证状态的存储来源，确认是 keyring；没有导出令牌。未使用 `gh auth token` 或 `--show-token`。未来若出现存储失败，先修复安全存储再继续，不将明文配置复制到仓库。参见[官方登录说明](https://cli.github.com/manual/gh_auth_login)与[认证状态说明](https://cli.github.com/manual/gh_auth_status)。

## 工具恢复与升级

便携目录被清理后，可根据 `github-cli.json` 的固定官方 URL 重新下载 ZIP 与校验文件。先核对两份下载的 SHA-256 和 ZIP 中对应条目，再检查归档路径、解压到新目录并核对 `bin/gh.exe` 哈希。已有目录不覆盖，不跟随链接解压；下载临时文件在完成后清理。不要从搜索结果中的第三方下载站获取文件。

工具更新时重新验证来源、校验值、命令参数和只读访问，再更新 pin 与记录；不因为上游出现新版本自动覆盖当前可用工具。本机当前依赖只保留解压后的两份必要文件。凭据仍由系统管理，不在工具目录中。

维护检查：`pwsh -File tests/GitHubAccess.Tests.ps1` 用本地合成 CLI 验证成功、未登录、权限不足、API 失败、隐私输出和工具缺失/损坏；`tests/DraftUpload.Tests.ps1` 核对真实上传脚本的所有 CLI 调用关闭遥测，并在成功/失败时恢复调用者环境。合成检查不联网，不代表真实上传成功。
