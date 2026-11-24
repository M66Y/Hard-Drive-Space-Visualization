# AsukaLens 开发检查点

## 当前C71：祖先验证复用候选完成合成验证（2026-11-24）

基线7e6f21d（C70规则分流），此前产品C#为2781ebb，开始main/origin一致且工作区干净。用户批准C69目录枚举候选实现和合成验证；特殊大小补查/界面不动，真实磁盘另询问，不打包发布。此次没有VSS/raw/VHD或个人目录授权，也没有执行这些操作。

新增NtfsNamespaceAncestorValidation：最多8192条不可变证明，仅当前Source/Namespace租约，绑定私有代际/完整目录身份/父级与规范相对路径。保留末级属性预查、真实目录打开/ACL、句柄身份/标签和完整分页；缺父证明/容量满回原Resolve，矛盾/错误拒绝，取消/来源关闭/租约结束清空。重复打开不替换证明，避免旧证明链留存。Source工厂显式reuseValidatedAncestors:true启用；正常管道仍默认false，只增加using收尾。未新增UI开关或个人扫描入口。

应用和测试--no-restore构建零警告；初次restore因沙箱无法读用户级NuGet配置失败，未改配置。新增纯合成94项通过，原namespace84/source52/index27/reparse36/large-profile25/size46/bridge45/coordinator68/core38/advanced25通过，共540项。计数1→1/501→201/2145→65是合成属性回调，不是实际I/O或提速。特殊LookupSize/ReadNativeSize和原Resolve、NativeCursor实际打开/核验逻辑保持基线，源指纹等价核查随归档保留。

实现及限制docs/ntfs-namespace-ancestor-candidate.md；归档iterations/2026-11-24-71-namespace-ancestor-reuse。本轮代码/测试/说明/记忆统一归档，按授权完成核验、清理和提交推送main；最终提交与上传状态以Git为准。正常在用程序、C55和活跃demo不替换/清理。真实Source兼容/权限/清理与实际收益尚未验；需要新范围确认，不沿用C68。下一步由用户决定受控实测与采纳，不自动默认启用。

C69审查和用户保留的3处暂停文档删除（30bbec9）已保留。C70规则整理见iterations/2026-11-24-70-project-rules，产品事实不受影响。C69审查证据见iterations/2026-11-24-69-namespace-audit，不改写其候选未实现的历史时点。

## 已接受产品与历史证据

- C67实现e578eb7：三阶段界面均获用户接受；默认100/原有/舒适、C57+DIB/C55失败回退、现有图片/焦点/Zoom/字号/完整圆角及1.15倍沿边柔光保留；无悬停文字/动态说明行。C61对象联动和主动进入父目录不变。
- C55 DLL SHA256 D34D6350A54CE9F990AAF209C140D66EF922AEB538C881ECB964A7D6395F3498。活跃自建demo与正常程序不清理或关闭，历史正式快照保留。
- C68实现2781ebb：新34/原计时58、bridge45/coordinator68/wire277/core38/advanced25通过，完整主窗计时含同步交付，不测DWM；客户端阶段/UAC启动/清理传输合并等口径见docs/ntfs-product-timing.md。
- C68获准F/O各一次均返回且清理确认，但跨时点大小/占用/根统计不同，exit3拒绝性能结论、未追加扫描、不重开C28。实际数据只在ignored artifacts/ntfs-product-timing-C68，公开白名单；旧个人卷权限已用完。
- C28同快照已Matched；不等于稳定整盘/WizTree级提速，快扫仍实验默认关、链接目标排除。PERF-001普通高速不稳定快于自动保持P2；日志不可用仍全量元信息核验，自动模式非实时负载自适应。
- Windows10新绘制/新后端、真实跨屏、新密度最小窗口人工专项、磁盘活动高负载/热插拔/休眠未全验。用户跳过Win10不重复催促；历史GDI/柔光偶发原因不明，不能重列已接受的闪黑/滚动为当前缺陷。
- 显示单独保存不重写图片；取消恢复/普通失败回退，恢复失败留副本提示，不保证断电/多进程双文件原子。v1.5.0旧发行包不含新开发功能，当前不打包/标签/Release。

Git/现状/当前计划优先于记忆。C69旧暂停记录的用户删除已保留，较长历史见references/decisions.md及各iterations；路线不自动授权下一阶段。
