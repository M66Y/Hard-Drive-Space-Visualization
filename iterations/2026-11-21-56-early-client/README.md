# C56：完全不透明整窗离屏重定向候选

2026-11-21，用户批准按[方案](../../docs/ui-opaque-redirect-plan.md)制作显式C56，保持布局、背景、字体和动效，不录屏，保留C55、不设默认。**实现、必要兼容/资源检查及真实打开已完成；连续缩放是否仍闪黑等待用户反馈，未宣称根治。**

## 实现与兼容原因

新增`OpaqueWindowRedirect`，利用系统整窗离屏重定向，LWA_ALPHA始终255、无透明色键、无WS_EX_TRANSPARENT。`Opacity`仍1，不用99%透明度、截图覆盖或冻结尺寸。仅`--opaque-redirect-candidate`或独立`AsukaLens.OpaqueDibCandidate.exe`启用，普通和旧DIB入口不启用。

WinForms在CreateHandle末尾UpdateStyles会撤销仅Native或OnHandleCreated阶段添加的位。候选用公开AllowTransparency意图协调框架，随后设置并读取真实Native属性；Active不只看内存状态。按句柄代际初始化/退役，旧初始化不能回滚新句柄；拒绝接管外部已有layered/透明策略，失败分类并恢复自有样式、保留其他位。没有新增应用像素缓存，原128MiB预算保留；系统维护的额外窗口图像未测量，不能冒称也在该预算内。

## 检查结果和保留失败

- 应用与测试最终构建0警告0错误。两档原生专项各81项通过，涵盖alpha255/no-key/no-click-through、失败回退与回滚失败分类、代际隔离、真实句柄重建、原生下拉/输入、动效和释放。
- 六个关闭周期GDI计数144档均135、96档均129；USER计数分别58、59且不增长。应用像素会话关闭归零。该检查不代表测量了系统/GPU图像内存。
- 26个唯一兼容组合最终完成，包含C56/普通/关闭新模式DIB两档六组完整UI、精确根合成/DIB图表与列表、布局/自然尺寸呈现、四组还原背景、滚动和柔光。DIB核心608项、6513984像素完成。
- 两次柔光测试在独立普通Form还原阶段30秒超时，尚未进入C56主窗段；失败原样保留。仅加入setter/消息泵阶段标记后，全99项在原时限内完成，96档亦通过；原因仍未确认，没有延长时限、删断言或声称排除超时。
- 初始原生夹具的创建期重入时机、未启用色键时的无效颜色字段比较、显示前逻辑尺寸与显示后DPI尺寸比较均已纠正；另有真实框架样式撤销问题已针对修复，未放宽像素阈值。

详见[验证摘要](verification-summary.json)。同构建off/on/on/off同步尺寸p50约250/228/227/247ms，双方保留C55缓存；应用像素峰值均27873396字节。暖UI、外部负载未控，不是屏幕帧率或普遍提速保证，见[性能摘要](performance-summary.json)。

## 真实程序与验收边界

本机入口`artifacts/dib-mainform-C56/app/AsukaLens.OpaqueDibCandidate.exe`已用独立配置打开，复用前轮自建52文件约8.97MiB。实际常用尺寸→最大化→还原后布局、背景和标题完整。未保存桌面截图，未录屏、改系统/OBS或其他前台覆盖物。窗口留给用户测试，连续缩放反馈待收；当前GDI存储表面早期仍有黑样本，不能据此宣称最终呈现通过或失败。

C55及普通二进制保持，未默认启用或发布。Win10的新窗口模式、真实跨屏DPI和连续无闪黑未验，不套用旧发行包的验收。初始baseline/WVR_REDRAW/layered/子面板诊断和超限限制见[诊断摘要](diagnostic-summary.json)及本轮方案；WVR_REDRAW扩大黑样本，未接入。子表面4096条之外2576条丢弃，不作全程正常结论。

## 归档与提交边界

`before/`为C55已审工作区前像，`key-files/`为关键源码，`changes.patch`只记录C56本轮差异（含前一诊断阶段），逐字节重放见[补丁校验](patch-validation.json)。哈希见[构建清单](build-manifest.json)及[归档清单](archive-manifest.json)。本机原始诊断ZIP保持；本轮测试/性能日志压缩并校验到候选目录`owned-validation.zip`后清理散文件、重复apphost及临时脚本；保留候选、独立配置与旧demo，见[清理摘要](cleanup-summary.json)。公开归档没有原始日志、图片或私人路径。

Git仍main `9ac1f0b`，C30暂存和C31–C55依赖未整合。为避免覆盖或混提，未提交/推送、未创建Release；本补丁不能直接当作针对干净HEAD的可构建提交。新后端/默认启用仍需用户反馈后另行确定。
