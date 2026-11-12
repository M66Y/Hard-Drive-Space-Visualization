# AsukaLens 开发检查点

2026-11-12。C23已提交推送d8dbf2fcd44fd47fcc273e906e8f2a3eebbacab1，39文件/manifest38项已从committed blobs验证；C24基线此sha，归档iterations/2026-11-12-24-snapshot-scan。正式版仍v1.5.0，无打包/Release。

## 最新用户指令与暂停

用户要求十五分钟内推进，14:43:37–14:58:37 UTC，到点未完成部分暂停。当前只收尾，不再开新Native/构建/UI功能。C24测试后端已完成验证，个人卷/主窗/IPC仍未完成，不标整个用户目标完成。截止后等待用户新消息再执行。

授权本轮新1GiB自有VHD，结合10k/100k VSS同源核验与性能对照；单独读取会话≤262144槽/raw256MiB+64KiB/8192calls，每owner一次reader预算不重置。六独立snapshot同静态数据，3×MFT/3×Directory，无warmup；总raw另列。个人卷仅明确指定的metadata预检(128B FSCTL)，不records/names/content或personal snapshot；具体值不入repo，私有结果已删除。后续不能自动沿用为个人原始读取许可。

## C24实际结果

Namespace batch19/20：完整IDs/游标/父链/预算，不信cached size。SizeIndex只解selected slots，不保全卷names，8worker/queue128/32768 Native unknown对象一次补，矛盾wholefail，所有worker退出后才cleanup。Owner/Reader真实source受新fixture-vss-scale Guard绑定，COM same-thread，失败cleanupflag保留。

最终build0warn0error。pure：preflight64、namespace84、size46、NativeVssScale28；VSS79probe+20legacyguard+73reader+20scaleguard；PS newVssScale1027/driver266/ScaleGuards36通过，独审无阻塞。

初始两Native在SnapshotCreate失败，均cleaned；主因同进程多session重复CoInitializeSecurity。已改本组件成功后进程复用，外部RPC_E_TOO_LATE仍拒，补4反例。Open不丢fixed typed.Code；wrapper保存安全stage/native_error。修后真实10k/100k各6sessions全部fingerprint一致，legacy小Probe重验过；所有ownedVHD/snapshots清理，active Native/UAC/exec为0。

10k fullwall median MFT562.5395ms / Directory1376.3029ms；100k3022.9848 /11072.2267ms（此受控数据约3.663x）。含Create/backend/cleanup；指纹在时钟外，WSS20ms采样非绝对peak，不等个人盘/Win10/WizTree。counts10000/100000objects，10010/100010entries，各100dirs；logical8234240/82250240，Nativeallocated32800000/328000000，unknown0。100k每raw102577664bytes/10000 NativeSupplementCalls，3raw累计另列。

C23持锁对照未提速(1.079/10.309s vs0.913/9.149s)，锁卷缓存混杂保留全样本，不能拿它当新VSS路线速度。当前新结果仅自有固定普通混合夹具。

## 当前文件/收尾

C24所有dirty均为本任务，Root负责Program/wrapper/driver/docs/archive/memory；attribute Owner/Reader/Guard/preflight，Source SizeIndex/NativeRunner，PSagent Namespace/PS门/driver tests。各作者已封板不再编辑/构建。旧kernel六样本8files9683B ignored保留不动。C24 7份结果已hash核对归档后删本机副本，private metadata不归档已删，owned目录0，无重复Preview。

Root正在作C24统一补丁/SHA/隐私检查与commit/push main；提交结果以Git为准。恢复补丁必须使用versioned baseline bytes及命令级core.autocrlf=false（global true/local false），避免换行转换；C23已用binary-safe patch逐字节恢复37payload。不要为小helper拆轮、不要force、不要新Release。

## 接续优先项

个人实际卷规模超过当前全MFT范围假设，具体值不存；需设计选中范围的高slot/位图寻址与预算，再评估个人来源。生产权限助手/受限IPC/普通权限MainForm可选入口尚未实现。当前代码仍tests/Prototypes/Ntfs，不宣称已上线快扫；先把共享代码/通用来源整理并以受控卷验收，实际个人snapshot/records另行明确范围。

MainForm正常返回ScanResult会替换oldcomplete、Live.Attach只一次；快路partial/unknown不能正常return，cleanup确认后独立session常规fallback，用户cancel不重扫。snapshot结果不复用live journal checkpoint；当前个人元信息预检不意味着完整扫描支持。暂停时不继续路线图。
