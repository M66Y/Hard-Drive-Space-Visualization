# AsukaLens 开发检查点

2026-11-13。当前 C27 接续，基线 e7b4324（仅 AGENTS 主动询问偏好已独立提交）；继承 C26 be21b34 的代码。先核 Git status，不把未提交修改当正式版本。v1.5.0 已发布但不含实验快扫，本轮不打包或发布。

## 当前事实

C26 的 Large 个人范围、受限 MFT 扩展和显式重解析点排除已验收。固定预算、失败恢复及同源正确性见 docs/ntfs-personal-scan.md 与 iterations/2026-11-13-26-personal-ntfs-compatibility/。只跳过链接和目标、声明 partial，不能称完整整盘；未知占用仍失败。快照清理确认先于发布，取消不回退，清理未知锁止本进程快扫。

C27 实际生产开发 EXE 已在用户指定目录完成部分快扫，无 fallback，cleanup 确认；已观察空间图/列表、大文件、类型、实际占用、排除明细及快照范围提示。独立 COM-only 12 个进程：6 STA 返回 RPC_E_TOO_LATE、6 MTA 成功；旧生产失败只取得 SnapshotFailed，没取到实际 HRESULT。Program 改 MTA 分派，GUI 独立 STA 后实际入口通过，不据此承诺所有快照失败已解决。

第一后台 FO 因 included/root metrics 不一致停止；排除范围一致、无其他读取错误/未知占用。旧 F 少计 Coordinator 发布核验，不能给稳定加速结论，具体个人计数/时间/指纹不入仓库。v2 已计入 Coordinator 发布核验并完成一对完整 FO：新增/删除/type/identity/links 类别均无差异，文件逻辑大小/分配量有差异，存在晚于快照的修改时间；排除范围一致、无未知占用/其他错误、cleanup 确认、无 fallback。该组仍未通过一致性门槛，exit3为对照拒绝而非扫描崩溃。不单独证明全部差异由外部写入导致。

## 工作区及检查

本轮文件归属：Program.cs、FastScanBridge/Coordinator、对应 tests 与 NtfsPersonalTimingDriver、tests/Program.cs、四份状态/合同/归档文档和本记忆。测试 timing worker 独立于旧全对象 Native 审计入口，保留真实生产校验且禁止计时中自动 fallback；目录仅 stdin/私有 pipe，比较信息只留内存。最终 Git status 与归档 files.json 为清单依据。

已完成 app/tests 构建零警告；Coordinator 68、Bridge 45、最终 timing 58 通过。desktop compatibility、mainform 默认/96 DPI 各86、general UI 两档通过。v2 已通过独立审查和本次真实对照执行；性能一致性拒绝如实保留，不能把执行完成记为性能验收通过。当前正式普通 app 构建已更新；验收预览正常退出。

## 继续顺序与授权

本轮不再追加扫描，统一文档/归档/清理并提交推送 main；提交结果以 Git 为准。用户已选择下一轮同一只读快照逐项比较，排除时点变化再判断统计问题；本轮不执行新方案，未来实际来源生命周期、预算和授权需据届时范围核对。真实测量期间已暂停本仓库源码/构建/归档写入。

本轮用户已明确允许同一个人范围短时 VSS 和 Large raw/bitmap 边界：1,048,576 入口、1 GiB+2 MiB、65,536 请求；接受邻近和驻留片段，只解析元信息、不开文件数据流、不保存或上传原始页/清单、不跟随链接、不改差异区，只清理本次快照。本次继续任务未扩大边界；不延续未来轮次/其他目录/VHD。UAC 由用户本人确认。

旧原始 kernel 留样仅 ignored 本机保留，不清除或上传。C27 COM-only 源以 .cs.txt 和固定结果归档，避免被主工程编译；生成 DLL 已删。私有真实 GUI 截图不归档。新 backend Windows 10、更广格式/设备、稳定性能和其他阶段真实取消仍待验证。

项目记忆是索引，长期规则见 AGENTS.md，当前状态见 docs/project-status.md，下一步见 docs/development-plan.md，本轮证据见 iterations/2026-11-13-27-personal-ntfs-ui-timing/。
