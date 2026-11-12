# 影响后续开发的决定

- 2026-11-07：高速模式无法稳定提速记为 PERF-001，用户要求初版后处理。不能用普通“继续”自动重启同级非阻塞优化；正确性/隐私/阻塞缺陷仍优先。依据 docs/development-plan.md 历史优先级。
- 2026-11-07：采用 .NET 10 LTS，以便长期开发；框架升级已独立验收，普通功能迭代不顺带更换大版本。技术与验证依据 docs/project-status.md。
- 2026-11-07：公开非商业分发，署名 AsukaLens — M66Y。代码PolyForm Noncommercial，文档/图片CC BY-NC；以 LICENSE / NOTICE.md 为准。默认图片由维护者使用AI工具生成，不能扩写为第三方角色授权。
- 2026-11-10：v1.5.0 以明确披露的干净环境例外发布；该例外不自动适用以后版本。Windows10兼容实测与微软框架官方支持范围分开。依据 iterations/2026-11-10-07-publication/README.md。
- 2026-11-12：为接近WizTree整盘速度，先研究可选NTFS路径，不把增加遍历并发当整盘提速保证。当前仅有限记录解码和逐对象补查，无整卷MFT读取或生产入口。依据 docs/development-plan.md。
- 2026-11-12：压缩/稀疏默认流只接受单一理由，命名流/属性扩展等组合保留未知；同一只读句柄两组观察也不等于活跃卷快照。依据 iterations/2026-11-12-09-special-lookup/README.md。
- 2026-11-12：复杂硬链接/较长命名流记录触发USA校验失败后，用户明确选择保持严格校验，先完成限定范围。不能忽略尾字换取通过；短名通过不证明复杂形态已修复。下一步先固定生成条件、限量本机留样和独立重放，原因尚未定位。依据 docs/ntfs-kernel-record-plan.md。
- 2026-11-12：磁盘活动面板布局经用户确认，首版每秒采样、60秒仅内存、整块物理盘口径；不代表本软件专属I/O。其他系统/设备边界见 docs/disk-activity.md。
- 2026-11-12：用户要求维护项目记忆Skill。选择70%/85%检查点，为压缩与交接留余量；百分比不可观测时按20次工具调用或30分钟回退，并在阶段/关键决定落盘。不能承诺仅靠Skill精确监听平台token阈值。

本页项目路径均相对仓库根目录；按需打开对应事实文件，不默认加载全部历史。新增决定只记影响取舍的原因与证据，已有长期规则回链AGENTS.md，不复制正文。

- 2026-11-12：固定21对象/63请求已稳定重现27次严格USA失败；保留最终6条原始失败正文仅本机供后续研究。以collected与all_decoded区分收集和解码，重放匹配不等于修复。理由是此前缺少原始字节无法可靠研究；继续严格拒绝，下一步依据可重放证据另提读取策略。依据 iterations/2026-11-12-13-kernel-matrix/README.md。

- 2026-11-12：不放宽复杂USA失败，改为在自有静态VHD内成功锁卷后经同一只读句柄读取严格原始记录。三次22条/21对象对照通过，证明受限替代来源可用，不能解释内核缓存根因或推广到个人活跃卷；旧6条kernel样本不扩容。随后用户确认先做无I/O的读取会话契约，避免在来源一致性未明确时直接扩展整卷吞吐。依据 iterations/2026-11-12-14-raw-records/README.md 与 docs/ntfs-read-session-plan.md。

- 2026-11-12：受控raw之后先落地无I/O读取会话模型，避免在真实来源一致性未知时直接扩大批量扫描。SourceStamp只作模拟验证epoch；错绑定批次直接拒绝、失效高水位不可回退、重启不返还累计预算、unknown总值保持null。有限线程反例验证模型终态，不背书真实VSS/活跃卷或全MFT覆盖；下一步实际来源另行设计。依据 iterations/2026-11-12-15-read-session/README.md 与 docs/ntfs-read-session-plan.md。

- 2026-11-12：真实来源先选自有固定VHD的短生命周期owner，准备阶段也先守卫真实归属/无父链，读者不可凭caller bool/裸句柄/旧token复用map。固定Native后验与Dispose分开，关闭期间取消必须复查；VSS只读研究不当现成来源。理由是先建立可核查真实绑定，再扩大批量范围；第16轮仍22目标，扩大4096槽另需确认。依据 iterations/2026-11-12-16-fixture-source/README.md 与 docs/ntfs-fixture-source-plan.md。

- 2026-11-12：用户另行批准本窗口内读取新建测试VHD的MFT有效范围；独立mft-range与固定4096槽/4MiB+bootstrap/256次预算，不扩大旧22目标模式。第17轮256真实槽读完而183 invalid、6 unsupported仍如实保留，范围完成不等于格式支持或完整占用；原因是先建立可核验的批量覆盖与失败语义，避免把未知数据当零来制造速度。下一步先做受控分类原因诊断，再决定格式/建树/性能范围，不放松严格decoder。依据 iterations/2026-11-12-17-bounded-mft/README.md 与 docs/ntfs-bounded-mft-plan.md。

- 2026-11-12：第18轮只加固定聚合原因诊断，保持严格decoder/分类/预算。真实183 Invalid分解为182全零Signature与1 SparseMappingFlags，6 Unsupported为RecordFlags；不凭全零改成Free，也不凭错误码宣布损坏。理由是字节形态与记录分配是两种证据，下一步先设计纯合成位图模型，真实来源后续单独验证。依据 iterations/2026-11-12-18-mft-classification/README.md 与 docs/ntfs-classification-diagnostics-plan.md。

- 2026-11-12：第19轮先实现无I/O位图模型，位图三态声明与四类decode交叉统计，不将全零推断成Free。模拟对象绑定只比引用，所有结果Synthetic，不自动提升为真实位图验证。Count/Memory/indexer视作不可信输入边界，即便外部伪装受控异常也统一净化；理由是异常类型不能证明来源。真实BITMAP解析/同来源读取/后验仍需另行方案。依据 iterations/2026-11-12-19-bitmap-model/README.md 与 docs/ntfs-bitmap-model-plan.md。

- 2026-11-12：第20轮仅在严格Map/USA后的私有record0提取普通驻留BITMAP，非驻留只Deferred元数据、无读取地址。ResidentComplete仅指字节容量，不证明bit值/位序/来源/分配；不把Synthetic定位器改标签提升为Native。下一轮先观察同owner真实bootstrap的属性形态，再决定非驻留映射/预算；实际Deferred就停，不为通过临时扩读。理由是当前合成布局未证明实际卷形式，来源真实与解释正确需要分别验收。依据 iterations/2026-11-12-20-bitmap-attribute/README.md、docs/ntfs-bitmap-attribute-plan.md、docs/ntfs-bitmap-source-plan.md。

- 2026-11-12：用户明确要求提速、减少小轮，当前观察任务扩大并合并为自有VHD端到端NTFS流程，一个C22归档与提交。新增授权只含MFT4096槽/4195840字节/256次、位图512字节前缀及2自建释放文件，不含个人/活跃卷或发行。原位图文件超过512也只映射并读取必要前缀，不能扩大为整文件读取。理由是以可运行链路和统一验收交付，减少重复收尾，同时保留权限/数据边界。
- 2026-11-12：按微软记录头文档，sequence在segment释放时递增，witness限定旧seq1..65534并核同slot+1/bitmap0/strictFree，拒绝猜测wrap。实际21正例+2释放槽及目录两大小对照通过；14对象用Native补值，不把请求数或正确性当性能收益。依据 iterations/2026-11-12-22-ntfs-fast-scan/README.md、docs/ntfs-fixture-fast-scan-plan.md。
