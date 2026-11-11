# 本地网页装备同步

基于 Ian Lucas 的 MIT 项目 Inventory Simulator 3.8.1，保留本机专用修改。
上游：https://github.com/ianlucas/cs2-css-inventory-simulator/tree/3.8.1

只在 Windows cs2 -insecure 的本地监听服务器中，给 local-only.json 指定的回环房主应用网页外观；不改变 Steam 的真实库存。机器人外观由 BotRandomizer 管理。

首次成功下载后会刷新房主已有的自有武器；!ws / css_ws 成功后立即应用。保留弹匣与备弹，捡到的其他玩家武器不改写。刀、手套和模型变化使用上游重建流程，切换中的视角可能有一次短暂切枪。

HTTP 直连并绕开系统中残留的代理，8 秒连接超时、30 秒请求超时、最多 3 次尝试；空闲连接 30 秒后回收、2 分钟强制换新。失败保留已下载的内存库存，并显示失败提示，不伪报成功。

部署需复制编译产物及 lang 到 plugins/InventorySimulator，将 gamedata/inventory-simulator.json 放到 CounterStrikeSharp 的 gamedata 目录。保留本机 local-only.json；该文件包含用户账号，不提交到仓库。

示例配置：
```json
{"Enabled": true, "PlayerSteamId": "填写本地房主的 SteamID64"}
```

编译：dotnet build -c Release -p:CounterStrikeSharpApiPath=<1.0.379 的 api 目录>。
权限条件检查：dotnet run --project tests/PolicyTests.csproj。
