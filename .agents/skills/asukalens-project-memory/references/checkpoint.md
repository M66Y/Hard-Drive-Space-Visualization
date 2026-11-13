# AsukaLens 开发检查点

2026-11-13。C25基线fd0639e8d3c4b217a376b50460ea261752c20179（C24已完成提交推送，manifest33项）；本轮提交/上传结果以Git实查为准。正式版仍v1.5.0，本轮无打包/Release。

## 当前任务与授权

用户恢复开发：先完成真实磁盘支持，再接主窗；没有新的时间预算。先前15分钟暂停、VHD和metadata-only许可不自动延续。C25已完成代码与合成/本机IPC/控件验证，仍在执行必要收尾；个人卷raw/VSS实测和实际画面尚待确认，不能宣称整个用户目标完成。

已提出修正版实测题，尚未收到答复：个人目录短时VSS、raw257MiB/8192次、262144入口；MFT页可能包含邻近对象及驻留小文件内容片段，仅解析选中元信息，不开数据流、不保存/上传raw与清单。旧“不读取正文”题被修正版取代。没有新VHD许可。实际画面需要可用桌面，未确认则保留待验收。

## 已实现的C25工作区

基线fd0639e，所有dirty均本任务：Core/MainForm/Program/双语README/AGENTS/docs、src/Scanning/Ntfs共享生产核、旧tests源搬移及新回归、iterations/2026-11-13-25-personal-ntfs。三位子Agent已封板，无并发写入。root统一构建/归档/提交。

大MFT按选中slot读取bitmap最多512扇区，MFT最多4096页，源总257MiB/8192calls；scope262144/32MiUTF16，Native32768对象/8workers/queue128。只支持本地fixed NTFS3.1、512/4096/1024；ACL/reparse/属性扩展等整次回退，不能等同所有整盘支持。

PersonalSource先固定live逐级身份及卷GUID，再同snapshot逐级复核；source.Create异常始终保留cleanup事实。共享VSS owner固定MTA线程，实际snapshot GUID/provider/清理保留，旧fixture守卫/预算不变。生产代码不依赖测试factory。

主窗默认关NTFS快扫：普通进程+同exe短时提权worker；父启动时间/path和双方OS PID、user SID ACL、首实例+remote-reject管道，路径/树仅内存。worker分支早于WinForms/日志初始化。完整树+cleanup确认才发布；用户cancel恢复旧树不fallback；支持失败仅cleaned=true后普通fallback；清理未知本进程latch禁再快扫。snapshot结果Source.CapturedAt且Checkpoint=null，不直接复用live刷新基线；定位前后台核当前完整ID。

Modified已补，同snapshot SI对象时间优先；硬链接用统一对象时间，不为目录陈旧缓存误报。快路不挂LiveSession partial，普通fallback session仍独立。

## 已运行验证

应用/测试--no-restore Release build零warning/error（最终微调后已统一复建）。core38/advanced25/journal16；新Wire117/Coordinator45/pipe44/PersonalSource45/PersonalIndex27；namespace84/size46；VSS79probe+20fixture+78reader+20scaleguard；Map199/bitmapPrefix94/vssScale28/prototype147/raw108/preflight64。

主窗default与96DPI最终各60项通过，可选屏幕捕获不算visual；general UI default/96通过；DiskActivityMainForm58通过。截图内容并非应用实际画面，不能当visual证据、不能归档，root需删除本轮ignored截图。正常控件回归修了嵌套列自动宽度越界。受限桌面GDI失败已改交互桌面跑控件，不算实际观感通过。

PS ScaleGuards36/VssScale1027通过，ValidationDriver266通过，exec会话已收回。根构建restore因环境NuGet配置权限失败，已使用已有assets/no-restore通过；未修改用户NuGet配置。

## 既有C24证据

C24同snapshot namespace+紧凑索引/Native后端，10k MFT562.5395ms/目录1376.3029；100k3022.9848/11072.2267（受控普通混合数据约3.66x），6独立session全部指纹/两大小一致并清理。含Create/backend/cleanup，非个人卷/Win10/WizTree保证。C23持锁路径无稳定收益，不能择样。详见iterations/2026-11-12-24-snapshot-scan。

## 后续与收尾

最终build/本机pipe和受影响UI回归已通过；等待修正版个人读取授权与实际画面条件，不擅自Native。若批准，真实source尝试必须全程严格预算并确认只清理本次snapshot，私有结果不落公共archive；格式失败如实回退再修范围内兼容。无许可则保留真实功能待验收。

同步docs/证据、隐私核查、恢复patch和SHA、清理、自动提交推送main。本轮实现不等全部验收，提交也不等新Release。Git .git及memory references需平台允许写入；不force。补丁用versioned blob bytes、命令级core.autocrlf=false、binary-safe stdout，旧global autocrlf=true不能影响恢复验证。

旧kernel六样本8files9683B ignored保留，不清理或上传；无本轮VHD/个人snapshot，个人预检旧私有文件已删。
