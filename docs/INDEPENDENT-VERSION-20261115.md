# HuangJZh 独立版本 1.0.0

2026-11-15：桌面面板独立编号为 1.0.0，战斗模块继续为已安装的 Custom 8.58。两个版本号分别记录界面与玩法，避免与上游 1.4.x 混淆。

## 本次变化

- 删除面板启动时的版本查询，以及标题栏的“检查中 / 最新 / 更新”按钮及上游发布页跳转。
- 删除打包界面里的 GitHub / GitCode 上游更新地址配置，不改为查询个人仓库，也不引入新的自动更新。
- 项目入口改为 https://github.com/HuangJZh/CS2-Bot-Improver，添加维护者署名；保留全部原贡献者与 AGPL-3.0 许可证。
- 桌面快捷方式保持有效，继续使用原位置的 Panel.exe。窗口内标题和 Windows 文件版本均为独立版 1.0.0。
- 未变更 BOT 瞄准、反应、队友强度、精英、经济、发枪、道具、寻路或回防逻辑。

## 构建边界与复现

原仓库只公开了部分 React 界面，没有发布已安装 Panel 1.4.8 对应的 Rust 后台源码，公开界面也停留在 1.4.3。因此本次使用**固定版本的打包界面覆盖**，而非声称完整重编译原生面板。后台仍是验证过的 1.4.8 二进制，保留现有设置与原生操作兼容性。

`tools/build_independent_panel.py` 只接受 SHA256 为 `dd29fe27b4b662d0341b14677427dbd978195620b8f1a7cb4372162c5136a087` 的原始 Panel.exe，并校验原界面资源哈希。替换资源来自 `Panel/packaged/titlebar.js`，同步更新公开 React 源码里的标题、项目链接与版本。

覆盖范围仅限压缩的 JavaScript 资源及其长度、Windows 版本资源和 PE 校验和；原生机器码、其他界面资源和程序标识保持原样。后台编译产物仍含未被调用的 `latest_release_tag` 处理函数，但整个界面已无其调用入口。界面不会启动该查询；这不等于完整删除未公开后台中的函数。

复现环境：Python 3，依赖版本固定在 `Panel/packaged/requirements.txt`。输入使用固定 v1.4.8 发行包内的 Panel.exe，输出必须使用不同路径：

```powershell
python -m pip install -r Panel/packaged/requirements.txt
python tools/build_independent_panel.py <原始Panel.exe> <输出目录/Panel.exe>
node --check <输出目录/Panel.frontend.js>
```

生成的 `.build.json` 记录输入、输出、界面哈希与版本。未知版本会被拒绝，不尝试猜测偏移。此构建不下载或写入游戏插件，也不包含个人饰品身份和缓存。

## 安装与恢复

已安装至 `E:/steam/steam/steamapps/common/Counter-Strike Global Offensive/CS2-Bot-Improver/Panel.exe`；桌面原快捷方式直接打开此文件。

旧面板和设置备份位于 `D:/codespace/CS2-Bot-Improver-backups/independent-panel-20261115/`。独立版面板包仅包含 Panel.exe、许可证和说明，适用于当前已有的稳定安装，并非完整游戏插件安装包。恢复时关闭面板，将 `Panel-before.exe` 复制回上述 Panel.exe 即可。

原始依赖来源仍记录在 `dependencies/base-v1.4.8.lock.json`，不能为了独立编号而抹除来源记录。此次不重新安装基础包，尤其保留当前 CS2 兼容的 CounterStrikeSharp 原生修补版。

## 后续维护

开发与发布入口为个人仓库的 `custom/independent` 分支。本次及后续独立版不向原仓库提交 PR。玩法模块延续现有版本记录；面板另行递增独立版本。

上游和 CS2 更新由人工按 `BASE-SOFTWARE-UPDATES.md` 审查，再按模块引入、测试和备份；不要直接用上游整包覆盖定制安装。若将来获得后台源码，可迁移为完整源码构建；在此之前，固定后台版本的界面覆盖是明确的维护边界。

## 验证

已验证打包界面语法、所有原生代码字节保持不变、Windows 版本为 1.0.0；实际启动后显示独立标题、文件全部就绪，更新按钮消失，项目入口及原贡献者可见。面板原配置及抽查清单内 463 个已安装 DLL、cfg、难度 VPK 和瞄准配置文件哈希均保持不变。本次没有重跑完整对局。
