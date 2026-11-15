# CS2 Bot Improver — HuangJZh 独立版

基于 [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 修改的人机系统，用于 CS2 本地人机与私人对局。独立维护瞄准射击、队友协作、经济、道具和路线分配逻辑。

**面板 1.0.0 · 战斗模块 Custom 8.58 · 基础组件来源 v1.4.8**

[仓库首页](../README.md) · [English](README.en.md)

[当前功能](../README.md#当前功能) · [开始使用](../README.md#开始使用) · [常用命令](../README.md#常用命令) · [更新与维护](../README.md#更新与维护) · [致谢](../README.md#致谢)

## 当前版本

维护分支为个人仓库的 [`custom/independent`](https://github.com/HuangJZh/CS2-Bot-Improver/tree/custom/independent)。面板已移除上游版本检查、更新按钮及发布页跳转，项目入口指向本仓库。面板与战斗模块分别编号。

当前验证环境为 **Windows 本地游戏主机**。部分基础依赖支持 Linux，但定制回防 AI 接入及当前原生兼容补丁只在 Windows 验证，不将本分支标为已完成跨平台验证的发行版。

## 当前功能

| 模块 | 当前行为 |
| --- | --- |
| 瞄准与射击 | Low / Medium / High 三档；感知延迟、一次定位、跟枪、首发朝向检查与连发节奏；支持 MIXED、HEAD、BODY。 |
| 声音与视角 | 协调目视交战与声音调查，减少不同模块争抢视角。 |
| 敌我强度 | 真人同队人机相对强度系数为 0.75；敌方可有 1–2 名参数增强约 20–30% 的精英。系数不代表对应比例的胜率变化。 |
| 购买与经济 | 保留已有武器，优先主武器，其次发枪，最后道具；常规购买排除 AUG、SG553，有钱时优先 AK、M4、AWP。 |
| 起狙人数 | 购买名额为 T 一把、CT 最多两把，计入已有及真人持有的狙击枪；不强制丢弃存活带回的枪。 |
| 轮盘发枪 | 一名附近合适队友响应，优先资金最多者；在买得起的 AWP、AK、M4 中随机选，钱少时依次考虑步枪、冲锋枪；无人能买时尝试转交附近队友最贵的现有主武器。 |
| 道具库存 | 购买后才能投掷，投出即消耗，不无限补充；T 火与 CT 火统一占用燃烧道具类别，避免跨阵营拾取后重复购买。 |
| 寻路与战术 | 使用游戏导航区域进行分路分配，保留原生移动与局部行为；包含基于回合历史的轻量路线反馈。 |
| 下包后行为 | 在支持的经典模式中，下包后可接入 Valve 回防模式行为树模块，同时保留定制战斗控制。 |
| 饰品与资料 | 保留机器人饰品和资料；单独的本地虚拟库存功能限定于已支持的本地主机环境。 |

### 三档难度

当前普通敌人的基础反应字段约为 **Low 150 毫秒 / Medium 100 毫秒 / High 75 毫秒**。这是模型配置，**不等于敌人出现到实际开枪的总耗时**；感知、拉枪角度、定位过程、武器就绪、可见性和个体强度都会影响出弹时间。

这组参数以模拟 Aim Rush 的估算为起点，不代表已测得与其完全一致，也不对应经过验证的真人段位或职业水平。详见[当前瞄准校准记录](AIMRUSH-ESTIMATE-PRESETS-20261114.md)。

## 开始使用

### 已有稳定安装

1. 使用桌面原快捷方式打开 `Panel.exe`，标题应显示 **独立版 1.0.0**。
2. 确认目录和文件状态为绿色。
3. 选择**机器人模式**，设置难度、瞄准和道具预设，然后启动 CS2。
4. 出现黄色提示时，重启 CS2 使对应修改生效。恢复正常游戏配置时，切换为**联机模式**。

当前独立版面板包仅用于替换**已有定制安装**的面板，并非完整插件安装包。替换前备份并关闭旧面板，再覆盖 `Panel.exe`；原快捷方式与设置继续有效。

### 全新安装与兼容性

仅克隆源码无法直接运行：还需要原生依赖、子模块、游戏资源与验证过的二进制组合。本仓库当前尚未提供面向全新安装的完整独立发行包，不应直接用上游整包覆盖现有定制安装。

当前锁定清单记录 CS2 build **26598758** 及对应的 CounterStrikeSharp 原生兼容补丁。游戏更新可能改变签名或使回防接入失效，更新时应验证整套依赖组合。

安装位置、面板构建和回退方法见[独立版本说明](INDEPENDENT-VERSION-20261115.md)，组件来源与文件哈希见[依赖锁定清单](../dependencies/base-v1.4.8.lock.json)。

## 常用命令

在游戏控制台输入；本地主机诊断指令不属于所有玩家都可执行的普通指令。

| 命令 | 用途 |
| --- | --- |
| `bot_aim mixed` / `head` / `body` | 切换混合、头部或躯干瞄准；默认 mixed。 |
| `bot_aim` | 查看当前瞄准模式。 |
| `bot_nades off` / `less` / `normal` / `more` / `max` | 调整道具策略；所有启用档位仍受真实库存约束。 |
| `bot_nades` | 查看当前道具模式。 |
| `css_bot_request auto` | 请求队友发枪；轮盘“需要一把武器”也接入同一逻辑。 |
| `bot_human_aim status` | 查看定制瞄准模型状态。 |
| `bot_team_nav status` | 查看队伍导航状态。 |
| `bot_route_learning status` | 查看轻量路线学习状态。 |
| `bot_retake_ai status` | 查看回防 AI 接入状态。 |
| `bot_aim_capture start 60` | 开始约 60 秒的本地瞄准诊断记录。 |
| `bot_aim_capture stop` | 停止诊断记录。 |
| `br_reroll` | 下次复活时重新随机机器人饰品。 |

职业队命令保留在 [Commands.txt](../Commands.txt)，也可通过面板的**指令**页面搜索复制。面板还可设置难度、瞄准、道具、机器人物品和游戏规则；部分修改需要重启游戏。

## 更新与维护

本分支**不自动跟随上游更新**。CS2 或基础组件更新后，由维护者审查差异、保留定制功能、验证兼容性并准备恢复点后再安装。修改保存在个人仓库。

| 文档 | 内容 |
| --- | --- |
| [独立版本说明](INDEPENDENT-VERSION-20261115.md) | 面板修改、可复现构建、安装与回退。 |
| [基础组件更新流程](BASE-SOFTWARE-UPDATES.md) | 依赖审查与升级步骤。 |
| [当前瞄准校准](AIMRUSH-ESTIMATE-PRESETS-20261114.md) | 三档参数与估算边界。 |
| [队伍导航](TEAM-NAVIGATION-8.38.md) | 路线分配设计和限制。 |
| [地图扩展记录](NAVIGATION-MAPS-EXPANSION-20261102.md) | 十张爆破图导航数据及验证范围。 |
| [依赖锁定清单](../dependencies/base-v1.4.8.lock.json) | 固定组件、来源和安装文件哈希。 |

### 源码与构建范围

战斗模块包含定制 C# 源码和对应检查。部分运行组件仍以固定二进制或子模块形式保留，仅检出仓库不等于完整复现已安装环境。

上游没有公开已发布面板的 Rust 后台源码，因此独立版 1.0.0 使用固定 1.4.8 二进制的可复现界面覆盖，原生机器码及既有配置兼容性保持不变。界面不再调用后台残留的版本查询处理函数；这不是完整的面板源码重编译。

`Panel/` 保存公开 React 界面和打包标题栏适配代码；`tools/build_independent_panel.py` 在校验原始程序与界面哈希后生成独立面板。构建步骤及固定依赖见独立版本说明。

## 致谢

- [Metamod:Source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib) by [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) by [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) by [XBribo](https://github.com/XBribo)
- [CS2-Bot-Vision](https://github.com/XBribo/CS2-Bot-Vision) by [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json) by [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI), forked from [Austin](https://github.com/Austinbots)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap) by [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## 许可证

[GNU Affero General Public License v3.0](../LICENSE), except `Panel/`, which is [PolyForm Strict 1.0.0](../Panel/LICENSE)
