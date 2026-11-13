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

- 2026-11-12：C23合并扩容、端到端速度与自有VSS验证，旧profile不扩；10k/100k统计一致但MFT中位1.079/10.309s、目录0.913/9.149s，保留缓存波动，不为名义快扫直接产品化。驻留分配按Native实测，修正transport错误零假设；锁卷缓存混杂未独立量化，后续改同snapshot对照。依据archive23及docs/ntfs-scale-and-product-plan.md。
- 2026-11-12：真实VSS小探针已证同source Native/内容/raw可读，选择不带writer、自动释放单snapshot；仅按成功返回并绑定的owned GUID清理，未知async不确认，失败保留私有恢复标识/镜像，不枚举删除他人。此证明不覆盖个人namespace完整性。依据archive23/vss-native.json、NtfsVssFixtureProbe与PS完整envelope门。
- 2026-11-12：用户确定后续合并snapshot namespace/compact sizes/parallel Native及条件式主窗；完整目录入口作为scope authority，以避免strictDecoded缺失导致漏算，不按系统slot编号跳过。用户另允许指定个人卷metadata-only预检，具体值仅本机；没有个人snapshot/原始记录许可。理由是先保证完整范围和真实收益，再开放可选产品入口，不将测试完整性标签直接移给用户目录。

- 2026-11-13：用户恢复C25，先真实卷后主窗，不沿用旧15分钟/VHD/个人metadata-only授权。选择按selected bitmap页/高slot支持大MFT，以明确预算保护资源；通用VSS源和普通权限主窗通过同exe短时助手连接。清理事实先于结果发布，取消不fallback，未知清理本进程锁止；理由是避免部分快结果或残余source污染旧完整结果。快照结果不带live USN checkpoint，修改时间以对象SI为准以避硬链接目录缓存陈旧。MFT物理页可能含邻近记录/驻留内容片段，个人实测必须据准确边界确认，不再用“不读取正文”概括raw。实现及合成/IPC/控件通过不等真实卷/视觉验收；合同docs/ntfs-personal-scan.md，归档iterations/2026-11-13-25-personal-ntfs。

- 2026-11-13：C26个人真实范围依次暴露旧规模、MFT属性列表及reparse边界；用户批准固定Large与显式partial策略。保留严格解析，支持受限扩展而不猜地址；只允许声明的重解析跳过形成部分结果，独立同snapshot核保留对象，Unknown/矛盾仍失败。理由是普通扫描同样不跟随链接，回退不能解决范围本来包含链接这一事实；用明确范围与可恢复旧完整树保证可用性。Wire v3有界描述和固定原因，详情截断保守标全部目录未读全。真实passed-partial/Namespace取消cleanup通过不等整个范围完整/性能/视觉/Win10通过。证据iterations/2026-11-13-26-personal-ntfs-compatibility，合同docs/ntfs-personal-scan.md。

- 2026-11-13：C27 保留严格 COM 安全检查，进程入口 MTA 分派、桌面显式 STA，避免等待 STA 的 COM 初始化副作用。COM-only 6/6 机制对照与修后真实主窗快扫通过；旧生产 HRESULT 未取得，不宣称所有失败同因。计时测试独立无额外 Native 审计，仍含生产 Coordinator 发布校验且禁止自动回退混入样本；紧凑私有差异仅输出固定布尔类别。本次完整 FO 存在文件逻辑/占用差异和晚于快照的修改时间，不因耗时较短宣称提速；用户确定本轮收尾、下轮同快照逐项对照。理由是先排除活跃来源时点变化，再判断算法正确性，避免重复非同源扫描。证据 iterations/2026-11-13-27-personal-ntfs-ui-timing/。

- 2026-11-13：C28 用同一自持VSS给原NTFS后端和原DiskScanner供源，保持原元信息分支独立，避免跨时点变化误判统计；测试专用有界遍历和固定字段私有摘要不改产品默认扫描。真实Matched且来源／预算／清理确认，两种大小与根汇总、排除集合一致，无需差异补查，因此关闭该核查项，不再重复扫描。C27旧差异不全部回溯归因，正确性不当作产品提速；之后按用户选择先改善主界面可读性。证据 iterations/2026-11-13-28-same-snapshot-comparison/，范围 docs/ntfs-same-snapshot-comparison-plan.md。首次仅UAC等待超时、未建来源，重启完成本轮唯一实际会话；许可不沿用未来。

- 2026-11-13：界面路线综合用户提供的优化提示词与三阶段观点，先视觉精修、再交互、后高级分析／个性化；按视觉／行为／可选能力归类，不机械映射P0/P1/P2。用户选择舒适大字留白、真实背景A/B、第二阶段图表定位优先。已有交互以改善为主，背景强度／密度／工作区后置，理由是先稳定默认体验和数据语义再增加选项。用户明确要求每阶段先审方案／预览后实施、完成后审效果再继续；路线可调整且不自动授权开发。规则AGENTS.md，具体判断docs/ui-design-roadmap.md。当前纯文档，未改界面、未制作效果或发布。

- 2026-11-13：C29用户明确启动第一阶段并限4小时，看真实A/B后选择B轻透氛围。保持布局，稳定数据／文字底板，舒适字号和控件状态；实际150%最小窗口暴露DPI行高／表头／列宽、单列AutoSize溢出与单位省略，选择修尺寸和数值适配以保证关键数据完整，不缩普通正文。图块数值单位不完整时整条隐藏而非省略误导，原Tooltip保留。理由是先统一默认设计规则并保护阅读和数据含义；高级配置后置。检查234各两档及UI／背景／监测通过，真实桌面另观察；阶段效果仍等用户审阅，不自动第二阶段。证据iterations/2026-11-13-29-visual-polish/，路线docs/ui-design-roadmap.md。
