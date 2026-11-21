# C50：DIB显式平移与列表暴露区域修复

日期：2026-11-19。用户明确批准修复C49已定位的两项问题，保留布局、背景和动效；完成后先看DIB候选，不设默认。基线main/origin/main为`9ac1f0b`，继承C30暂存与C31–C49工作区。只有测试原型及对应验证改变，没有修改生产绘制代码或扫描器。

## 实现与理由

1. `NativeResizeFrame`新增显式源矩形、目标逻辑位置、预期视口参数；旧API保持零原点合同。只有MM_TEXT、零window origin、匹配的整数viewport、无镜像／缩放／旋转及有界坐标被接受。原生裁切保留，托管HRGN与原生裁切取交；GetHrgn(target)已有视口转换，不能再偏移一次。
2. 仅为本窗口拥有的两个直接下拉框识别WM_PRINTCLIENT目标；`OnPrint`作用域不会更新根PresentedLayout／FrameCount或消耗prepared帧。未知目标仍走受保护复制或原GDI+回退。
3. 子窗口移动前记录旧Region覆盖，布局／新Region完成后减去当前覆盖，与客户区求交，使用尺寸、DPI、状态一致的已绘DIB立即补画。新子窗口的可见裁切继续有效。原来的早期根发布仍保留，不强制Refresh或靠下次自然Paint掩盖残留；复制失败按原绘制器回退，失效帧只请求正常重绘。

复制裁切依据：[Microsoft ExtSelectClipRgn](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-extselectcliprgn)规定HRGN使用设备坐标；[Region.GetHrgn](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.region.gethrgn?view=windowsdesktop-10.0)按给定Graphics返回区域。本机零容差测试具体核对正／负视口、不同源／目标位置、复杂原生孔洞与有限／无限托管裁切，而非只凭文档推断。

## 验证

| 检查 | 结果 |
| --- | --- |
| 测试构建 | 0警告、0错误 |
| DIB内存／映射／裁切／资源 | 608项；6,513,984像素；两个完整场景零容差 |
| 句柄重建、同键新缓冲、打印隔离、同步暴露区 | 144／96 DPI各62项；新缓冲非不透明像素0 |
| DIB早期覆盖 | 两档各91项、1,075,200 RGB像素 |
| 原型交互／布局 | 两档各199项 |
| 四组固定12步序列 | All144、Combo144、Grid144、All96，78次映射成功，0回退；18次旧列表残留在布局结束前恢复 |
| 旧采样覆盖 | 每组44个几何后根点均匹配，共176个；无观察失败／截断 |
| 修复证据反例 | 错偏移、错误像素、缺失阶段、打印冒充根呈现4种均拒绝 |

最终四组绑定同一构建，见`evidence-summary.json`。旧C49失败校验脚本保持原样；新增`tests/ResizeMappingRepairEvidence.Tests.ps1`要求同一旧Grid像素先暴露、再于GeometryAfterChildren前恢复，期间没有自然根Paint，不能删除原失败点。96 DPI为虚拟化进程，不是跨屏验收。

开发中一次编译失败来自Region SetClip缺CombineMode参数，已修正。初版对GetHrgn结果重复应用viewport，新增有限裁切测试检出4,802个不符像素；撤掉重复转换后所有像素通过，保留严格反例，未放宽容差。

## 实际展示与边界

候选：`artifacts/resize-candidate-C50/app/AsukaLens.NativeFramePrototype.exe`。本次以All原生组合模式打开；双击无参数仍从A纯画布开始，F2切换，F6尺寸演示，F7合成进度。测试数据明确为合成，不能扫描真实目录，不是完整正常功能DIB接入。

已观察下拉展开／收起、F6连续尺寸中的窗口及结束画面，背景和模块仍保持原样。工具只能提供离散窗口观察，不能据此宣称屏幕每一帧无闪黑。一次鼠标放大请求被工具拒绝为超出当前窗口坐标，未执行；后续拖动未观察到尺寸变化，不记为鼠标实时缩放通过。未录屏、不操作OBS、不改系统设置；当前只读查询DragFullWindows=true。

**剩余问题：**四组早期NcPaintEntering仍各采到20个黑色点，NcPaintReturned均0黑。新尺寸准备成本仍存在；本轮没有做完整同条件速度基准。连续屏幕闪黑、半截标题、最小化恢复及跨屏DPI未整体关闭。用户实际反馈“改善比较明显”，据此保留候选；普通版未替换、未设默认，整体缺陷未关闭。

## 归档与提交

`before/`、`key-files/`及`changes.patch`保留本轮源码基线和改动，`build-manifest.json`绑定二进制及关键源码，详细自有窗口trace仅在ignored artifacts压缩保存。正常二进制和C48候选哈希不变。未进行个人扫描／raw／VSS，未发布或修改Release。

因测试原型依赖继承的未收尾UI代码，当前index也含C30文件，本轮不混提整个工作区；未提交／推送，按源文件前像与补丁保存可恢复记录。第一阶段和默认切换仍需用户复审。
