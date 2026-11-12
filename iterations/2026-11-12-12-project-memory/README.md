# 项目知识 Skill 与上下文检查点

2026-11-12，基线 `d39ad9ec039598f1b7ce8ad6f1ddc8469b07969e`。用户在当前开发中新增要求：优化 AGENTS.md，创建记录历史、现状、重要内容及决策理由的 Skill，并由智能体确定上下文更新阈值。本轮只落实该项目工作流，不改变软件运行行为。

项目级 `.agents/skills/asukalens-project-memory/` 包含精简 SKILL.md、checkpoint.md 和 decisions.md；长期规则仍以根 AGENTS.md 为准，现状/计划/Git/迭代为事实来源，Skill只是可更新交接索引。历史回链而非复制全部文档；原型、已提交、发行版与未验收项分开。用户新增的工作流与先前基础模块独立提交。

选择可靠可观测上下文使用量的70%作为检查点，留30%处理当前操作、核对和交接；85%及即将压缩时有新事实再检查。平台未提供可靠百分比时，回退每20次工具调用或30分钟先到者，关键决定/阶段/暂停/恢复另触发。该阈值是项目执行约定，不是OpenAI默认阈值；不声称可后台监听token或修改平台自动压缩。不为了触发更新而制造时间戳提交。

依据官方[本地Skill目录与按需加载](https://learn.chatgpt.com/docs/build-skills)使用项目级目录；[API压缩文档](https://developers.openai.com/api/docs/guides/compaction)说明的是API调用者的context_management设置，不被误写成AGENTS能控制桌面平台。正文只记录必要操作规则，日常事实更新references；超过约120行的checkpoint压缩重复内容并回链，保留唯一理由和未解决事项。

验证：使用skill-creator的quick_validate，UTF-8宿主Python检查通过；独立只读审查确认链接层级、事实、阈值回退和授权表述无阻塞。受影响本地链接与隐私核对通过。无新脚本或产品代码，不重复构建/功能回归。没有个人路径、邮箱、凭据、原始NTFS记录或日志；未建立后台服务或额外采集。

本轮不新增临时数据或二进制。关键文件及SHA-256见files.json，可恢复补丁changes.patch不含自身或历史归档；从实际基线恢复后核对暂存内容。Skill格式有效不等于平台已在当前会话中动态重新注册；根AGENTS提供明确入口供接续使用。
