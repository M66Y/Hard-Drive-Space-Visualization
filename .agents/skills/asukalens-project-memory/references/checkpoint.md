# AsukaLens 开发检查点

## 当前：C65整块背景修正版待用户复审（2026-11-22）

本轮基线main/origin 3282f8b（C64分析），工作区归本轮：Wallpaper.cs、src/UI/ScrollbarPointerTracking.cs、tests/ScrollFrameConsistencyTests.cs及Program入口、受影响说明/规则/记忆和C65归档。用户已批准先做整体背景反例、统一滚动提交/发布、快拖和缓滚都验，保持布局/背景/动效；用户正常关闭旧程序后已替换打开。最终提交/推送以Git为准。

旧代码整屏跳转没抓到撕裂，补入小幅连续拖动后，两档原反例观察11/14次背景搬移；延迟模式另有3次松手只改条值、不移动实际列表。先保留失败，后修指针Emit包入既有PerformBufferedScroll、父列表Update后再PresentationResumed；延迟松手补一次实际ThumbTrack，随后ThumbPosition/捕获释放/EndScroll。不改原条身份、键盘/滚轮、范围/事件NewValue与系统偏好。

最终两档各92新检查、42指针/34表面及列表缓存/滚动/完整UI/联动/布局，共16组全通过。原反例的未受保护阶段会读可视区跨列背景；修正后所有事件都有真实native redraw hold，返回时背景与轨道正确、发布顺序正确、实际行移动。受保护阶段不采中间DC，不将0次采样冒充DWM零闪动。没有以延后Update或DrawToBitmap掩盖反例。

正常入口bin/Release/net10.0-windows/AsukaLens.exe已重新打开，仍扫描自建140文件demo。已实际顶部→底部→顶部拖动和列表内小幅滚轮，看到位置及背景完整；真实连续观感**等待用户复审**，异步问题已发出，不自动判整个缺陷解决。不要再次操作正在供用户体验的窗口或要求录视频。

保留C63用户最初接受、C64新视频残留和本轮原失败；证据见iterations/2026-11-22-65-scroll-frame-consistency。应用/测试零警告，C55 DLL SHA256仍D34D6350A54CE9F990AAF209C140D66EF922AEB538C881ECB964A7D6395F3498。当前程序和活跃C63 demo保留，原视频未动、无新录屏/系统设置/个人卷/VSS/打包/标签/Release。

## 下一步及长期边界

- 等待本轮实际反馈决定缺陷是否收尾；改善不等于全部通过，有新残留先定位，不重复让用户证明故障存在。
- 第一、二阶段已接受，第三阶段只有docs/ui-stage-three-plan.md和设计预览，产品实施尚未授权。不因滚动修复自动开工。
- C57普通默认/C55失败回退、背景图片/聚焦/Zoom、字号和1.15倍沿边柔光保持，无悬停文字/自动说明框。C61内嵌文件先保留总览，主动进入父目录后选中。
- Windows10新绘制、真实跨屏未验；用户已跳过Win10，不重复追问。C28同快照已Matched；快扫默认关闭，个人raw/VSS须当轮授权；PERF-001仍P2延期。
- 项目现状、计划、决策和Git优先于记忆；只存精简交接事实，不存个人路径/视频帧/文件清单或NTFS页。
