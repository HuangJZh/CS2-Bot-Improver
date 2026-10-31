# BotSupport

独立附加功能；保留现有反应时间 Low 180 / Medium 160 / High 120 毫秒和混烟调整。

- `=` 轮盘的“需要武器”由符合条件且资金最多的一名 Bot 付款（同钱选最近），在买得起的 AWP、AK（T）或两种 M4（CT）中等概率随机选择，AUG 不发放；这些都买不起时先选买得起的最贵步枪，再选买得起的最贵冲锋枪；指定枪型买不起时也同样降档。
- 必须在购买时间、购买区；请求者没有主武器。队友必须活着、未交战、在购买区，距离不超过 1500 单位，按实际余额判断能否支付，不强制预留资金。Bot 自己没有主武器也可以给你发枪（1.0.6 起）。购买的枪直接进入请求者背包，失败退钱，2 秒防重复。
- 控制台 `css_bot_request awp`、`css_bot_request ak`、`css_bot_request m4a1`、`css_bot_request m4a4` 可以指定本阵营枪械；聊天 `!bot_request awp` 同样支持。
- 地面上的 Bot 每次枪械开火后独立判定一次，Low / Medium / High 成功率 75% / 85% / 95%；成功清零水平速度，保留垂直速度。不影响真人、跳跃、刀和投掷物。
- 属于开枪后停止，不修正刚打出子弹的散布，也不会强制维持停止状态。连发每一发都会再判定。
- 开局和每回合对比实际安装的难度包，跟随原面板难度。无法识别时保留已识别档位，首次默认 Medium，并在日志提示。

使用本机 API 编译：`dotnet build -c Release -p:CounterStrikeSharpApiPath=<csgo>/addons/counterstrikesharp/api`。
纯规则检查：`dotnet run --project tests/SupportTests.csproj -c Release`。
安装 `bin/Release/net10.0/` 中的 BotSupport.dll、BotSupport.deps.json 和 BotSupport.pdb 到游戏 `addons/counterstrikesharp/plugins/BotSupport/`；重启机器人模式。

## 1.0.4 诊断日志

每次 `playerchatwheel` 轮盘操作会先记录原始参数（slot 与 args）；命中“需要武器”时会继续记录被拒绝的原因（已有主武器/不在购买时间或购买区/冷却）、同队 Bot 数、符合条件的候选队友数，以及最终发枪的队友、武器和价格，或失败原因。这样若轮盘仍无反应，可直接从 `counterstrikesharp/logs` 判断是命令未到达、时机/区域不符，还是候选队友与资金问题。日志只用于诊断，不改变发枪规则。
