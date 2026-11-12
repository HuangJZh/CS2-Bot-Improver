# Medium 敌方反应时间约 170 毫秒

Custom 8.53：在原有整体强度缩放完成后，将普通 Medium 敌人的基础 Reaction 设为 0.170 秒。精英继续使用原来的 1.20–1.30 倍相对强度，其基础反应为约 131–142 毫秒。这里的数值不是发现敌人到首次开火的总耗时，情境修正与视觉/运动过程继续生效。

只调整敌方 Medium 的 Reaction；队友、Low/High、定位速度、精度、视觉延迟、采样间隔、武器伤害等保持原值。EnemyStrength=0.80 保留。264 项模型检查与 Release 构建通过；已在用户退出 CS2 后安装。

安装前备份：`D:/codespace/CS2-Bot-Improver-backups/medium-reaction-170-20261113/before`。无需覆盖 human-aim.json 或难度 VPK。
