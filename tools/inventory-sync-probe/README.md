# 网页库存同步回归探针

仅用于测试者自行启动的本机监听实例：必须同时带 `-ip 127.0.0.1 -port 27025`，否则不执行。不要安装到正常游玩的实例。

使用对应的 CounterStrikeSharp 1.0.379 API 和已安装的本地限制版 InventorySimulator 编译：

```text
dotnet build -p:CounterStrikeSharpApiPath=<api 目录> -p:InventoryPluginPath=<InventorySimulator 插件目录>
```

在游戏完全退出后，临时将输出安装到插件目录的 InventoryProbe 子目录，并以 `-insecure -ip 127.0.0.1 -port 27025 +map de_mirage` 启动已挂载插件的 CS2。探针会清空测试者本次进程的缓存，创建有 7 发弹匣 / 19 发备弹的无外观标记 M4，等待 HTTP 完成后检查自动应用；再执行 css_ws、切换 Dust2 并自动退出。它不改网页数据、Steam 库存或持久游戏配置。

`evidence.jsonl` 为本地诊断记录，包含装备标识，请保存在本地。验证 M4 在延迟下载前 ItemID=1、属性数=0，下载后 ItemID 属于虚拟编号段、属性数大于零，弹药仍为 7/19。换图后的房主必须继续满足 CanApply 且具备网页装备。`errors.txt` 不应存在；超时阶段记录不能当作通过。

测试结束后移走临时 InventoryProbe 插件并恢复测试挂载，避免下一次正常游玩自动触发测试。

框架当前不支持直接枚举 CEconItemAttribute 的 NetworkedVector；本探针只读取其 Count，不猜测其内存布局。这里只验证外观同步与弹药保存，不验证网络长期可用性或瞄准质量。
