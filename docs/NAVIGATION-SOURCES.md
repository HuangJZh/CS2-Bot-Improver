# 导航实现参考与来源

本模块分为项目自己实现的上层路线分配、当前游戏导航图导出，以及原生 MoveTo 执行适配。没有把旧版 CS:GO 的学习模型整体移植到 CS2。

1. **ed0ard / CS2-Bot-Nav**，固定提交 `0d1d04f8eb748e3434462d2a799ca89ab299e583`，AGPL-3.0。
   - 项目：https://github.com/ed0ard/CS2-Bot-Nav
   - 参考文件：[BotNav.cs](https://github.com/ed0ard/CS2-Bot-Nav/blob/0d1d04f8eb748e3434462d2a799ca89ab299e583/BotNav.cs)、[NavMemory.cs](https://github.com/ed0ard/CS2-Bot-Nav/blob/0d1d04f8eb748e3434462d2a799ca89ab299e583/NavMemory.cs)。
   - 作者 ed0ard & XBribo。采用其 CCSBot::MoveTo Windows/Linux 函数签名和调用方式；其余目标选取、路径分配、生命周期与玩家实体枚举按本项目重写。原许可证与本仓库根 LICENSE 均为 AGPL-3.0。
   - 没有导入其整套内存偏移读写或常驻原地 Hold 实现，避免与本项目的 BotState 脱困及 BotHider 实体列表冲突。

2. **David Durst / CSKnow、MLMove**，CSKnow 固定提交 `40c9691d0d72dbf757f85d731dcc52eac3948246`，MIT。
   - 项目：https://github.com/David-Durst/csknow
   - 研究：[Learning to Move Like Professional Counter-Strike Players](https://davidbdurst.com/mlmove/)，SCA 2024。
   - 设计说明：[Region-Scale Agent Navigation](https://davidbdurst.com/blog/csknow_first_two_non_hand_crafted.html)。
   - 检查文件：`assign_players_to_orders_node.cpp`、`spacing_helpers.cpp`、`push_condition_node.cpp`。借鉴按任务分配队员、避免阻挡、保持补枪距离的分层思想；本版使用自己编写的规则和 A*，没有复制论文权重、训练网络或宣称复现其评测成绩。

3. **当前安装地图的导航网格**。
   - `tools/export_team_navigation.py` 使用已有 Source2Viewer-CLI 提取 `.nav`，通过 Awpy NavMesh 读取原生区域及有向邻接。
   - 图中同时保存原始 map VPK 与 nav 文件的 SHA-256。运行时只对校验匹配的地图启用。
   - Awpy：https://github.com/pnxenopoulos/awpy 。它仅作为离线数据导出工具，不是本插件运行时依赖。

本次下载供检查的源码、原许可证及固定提交清单保存在本机 `E:/csdemo/bot-captures/navigation-20261102/reference`。
