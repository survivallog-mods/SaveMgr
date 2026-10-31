# 存档管理 SaveMgr（Survival Log MOD）

> 作者 Night · 抖音 166660371 · 免费分享 · 仅供个人使用，禁止二次分发
> **状态：设计定稿待作者批，功能开发中，未发版。**

## 一、这是干什么的

游戏只有 4~6 天的滚动快照，超窗即覆盖；想回档没有后悔药。本 mod 提供：

- **每日结算自动存档**：间隔天数可设（默认 3 天，1=每天，0=关闭）
- **手动存档**：重要节点随手备份（热键/面板按钮）
- **备份管理**：列表 / 删除 / 导出 zip 长期保存
- **一键回档**：把备份注入游戏原生读档列表成新档入口——**原档不动，随时可再回**

## 二、安全与还原

- 备份全部落在 `BepInEx/plugins-data/SaveMgr/`，字节级复制，不解析存档格式
- 对游戏存档目录的写入只有一种：恢复时复制备份成**新档名**（走游戏自己的 `AddHistoryEntry` API 注册），从不改写现有存档
- 删除 dll 即还原原版；备份文件保留在 plugins-data 由玩家自行处置

## 三、安装与卸载

需先装好基座（见 SurvivalLog.Base 包）。把 `BepInEx\plugins\SurvivalLog.SaveMgr.dll` 放进游戏 `BepInEx\plugins\`，重启游戏即生效。

## 四、反馈

建议/bug：仓库 Issues，或抖音联系作者（Night，166660371）。

机制细节与实证见 `SurvivalLog.SaveMgr/DESIGN.md` 与 `docs/`。
