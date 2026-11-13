# AsukaLens 开发检查点

2026-11-13。C26从基线00c9b410703237aa14f8555124d2b713668b9a32接续；证据见iterations/2026-11-13-26-personal-ntfs-compatibility/README.md与checks.json。当前提交/推送以Git核对；正式版仍v1.5.0，本轮无新Release。

## 本轮已核实状态

个人NTFS路径已在指定范围通过passed-partial：目录来源与bitmap/严格raw、included全部唯一对象的独立同snapshot Native身份/两大小/links/Modified核验、原始请求预算及cleanup确认。namespace_complete=false，重解析入口与目标未纳入统计，不能称完整原始范围/整盘支持或WizTree速度。真实取消在snapshot与bootstrap完成后的Namespace阶段通过，cleanup=true，不覆盖其他阶段取消。

当前默认入口仍普通目录扫描；勾选NTFS快扫（实验）后同exe短时管理员助手工作，普通main不常驻提权，v1.5.0不含此功能。DiskAtlas是历史内部名称，未做无关改名。新Wire v3只从受认证本机pipe传递元信息与有界排除描述。所有结果先确认来源清理；未知清理锁止本进程后续快扫、cancel不fallback；失败转普通的原因在进行中可见。

C26固定Large：1048576入口、64Mi UTF16名字/目录路径预算、16384 MFT页/4096bitmap扇区、1GiB+2MiB/65536 raw请求、每次64KiB；Native131072对象/8路/128队列。私有来源binding固定budget，legacy Standard/fixture不扩。文件相对路径按需组装，目录绝对前缀与issues保留另核预算。

受限MFT属性列表最多64KiB/64扩展/1024runs；严格USA/完整引用与base/instance/VCN/物理覆盖，不猜未知地址。续段不使用未定义大小字段。Metadata查询只申请0x80属性权限，raw页仍可能含相邻记录与驻留片段；不能说物理上绝不读取正文。旧复杂kernel返回USA表示问题不因此宣布修复。

用户明确批准遇reparse沿普通策略跳过、不跟随，允许显式部分结果并保留旧完整恢复。Filter重映射并核原库存，Wire仅传总数+<=2000 parentID/leaf详情、receiver固定Reason；<=2000标实际祖先，详情截断全部included目录保守Incomplete。未知大小/错误/矛盾不混入partial成功。主窗最多保留一个完整树+查询上下文，恢复/新完整/关闭释放；部分快扫完成和统计范围明确。

## 验证与限制

app/tests Release构建零warning。Resolver+真实Reader内存替身102、continuation61、bundle41、Large25、reparse36、Wire277、Coordinator67、pipe45、Source52、Index27、acceptance54+input18、Metadata36(0skip)、namespace84/size46/Map199、VSS79+20+81+20、prototype147、raw108、prefix94/scale28通过；core38/advanced25/journal16通过。主窗default/96各86，含七项/partial/恢复/取消隔离；generalUI两档通过。PS旧scale guards36、VSS evidence1027、driver266通过。独立审查未发现partial统计/隐私阻塞，修了游标释放与异常清理finally。

真实结果画面仍待观察：可选截图捕获无可用图片，用户Escape停止ComputerUse后未再使用该工具。不能把后台passed-partial或合成控件断言代替个人结果视觉验收。实际个人性能、Win10新后端、更广格式/设备与其他阶段取消仍待验证；全对象Native审计是额外成本，不作性能样本。

各次纳入证据的真实probe返回cleanup=true，当前无测试进程。已清理本轮隔离UI配置/诊断、合成截图及未接入UI driver；历史ignored kernel raw样本保持不动，不上传。新VHD未创建。

## 授权与后续

本轮用户单独批准指定个人范围短时VSS与raw/bitmap读取，承认邻近/驻留片段；后来扩大到上述Large总边界。只提取元信息，不开用户数据流，不保存或上传raw/清单，不调diffarea，仅清理本次snapshot。范围stdin入私有pipe、不放argv。许可不自动延续以后轮次/其他目录/VHD/发行；UAC由用户本人处理。

接续先核Git和归档，保留普通扫描可用入口。下一步先安排真实结果画面，再另定同条件性能与Win10验证；不重复做已通过合成或假称所有个人卷都快。尚未验证的项目继续待验收，不因代码提交改成完成。新轮如需个人卷snapshot/raw，按当轮边界确认。
