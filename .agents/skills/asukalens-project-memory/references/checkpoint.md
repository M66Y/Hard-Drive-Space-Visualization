# 当前交接点

2026-11-12，本轮起点fa21d9e57d225f263f59d4a7f4230e520381097b，起始工作区干净。用户要求连续1.5小时，08:14:03–09:44:03 UTC（本地16:14–17:44），当前已完成原始读取验收，准备独立提交第14轮，随后实施用户已确认的纯合成会话契约。

- 第14轮[原始读取](../../../../iterations/2026-11-12-14-raw-records/README.md)：首次锁前Native不稳定、第二次fresh元信息查询失败均停止并清理；随后修复解锁后重新挂载/元信息句柄生命周期，保持严格校验。3次新VHD（含最终代码）各860项：raw22全decoded/21对象匹配/14特殊分配未知，kernel仍36decoded/27拒绝；解锁、卸载清理均true。单run、22次ReadFile/22528请求字节不等于物理I/O或性能。
- map174、注入lease84、raw metadata/type/cancel130、PS raw295/code187/guards36/矩阵摘要150、旧留样115/矩阵114/原合成147通过；旧comparison另新VHD1583项通过，构建0警告0错误。严格Decoder/生产源未改，不接产品、不打包。旧6条kernel样本共9683字节仍仅本机，0遗留VHD工作目录；本轮原始字节只内存。
- 第14轮需完成补丁基线恢复、必要文件哈希/隐私检查、删除本轮安全结果副本、提交推送main。已fetch且远程匹配fa21d9e，公开noreply身份合规。全部当前raw/文档改动归本轮；新增会话实现必须独立第15轮，不能误暂存。
- 用户确认[纯合成读取会话计划](../../../../docs/ntfs-read-session-plan.md)：只做测试专用状态机，无真实快照/个人卷/产品入口；上限4096模拟记录、2次重启，unknown保留、代际隔离/取消终态、LiveUnproven无论日志安静与否都不得Complete。attribute_probe负责新NtfsReadSession.cs/Tests；special_contract负责独立反例文件NtfsReadSessionAdversarialTests.cs；Root稍后接Program/文档/审查与归档。完成事实需核对agent交付，不从方案推断。
- 2026-11-12本轮VHD明确许可仅为新建256MiB自有卷、同句柄锁后有限读取、结束解锁卸载清理；不延续以后。第15轮没有真实I/O授权需求，也不复用本轮许可开展快照/整卷扫描。

边界：本机实际只覆盖NTFS3.1/512-4096-1024单run静态夹具；真实碎片、Win10/其他几何、真实驱动阻塞/解锁失败、活跃卷、整卷速度及生产入口未验收。正式入口：[现状](../../../../docs/project-status.md)、[计划](../../../../docs/development-plan.md)。