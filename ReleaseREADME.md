# SurvivalLog.SaveMgr — 存档管理

## 功能
- 每日结算自动存档，间隔天数可设（默认 3，1=每天，0=关闭）
- 手动存档：热键随手备份重要节点
- 备份管理：列表 / 删除 / 导出 zip
- 一键回档：备份注入游戏原生读档列表成新档入口，原档不动随时可再回

## 安全
- 备份=字节级复制到 `BepInEx/plugins-data/SaveMgr/`，不解析存档格式
- 恢复=复制成新档名 + 游戏自己的注册 API，从不改写现有存档
- 删 dll 即还原原版

## 安装
需先装好基座（见 SurvivalLog.Base 包）。把 `BepInEx\plugins\SurvivalLog.SaveMgr.dll` 放进游戏 `BepInEx\plugins\`，重启游戏即生效。
