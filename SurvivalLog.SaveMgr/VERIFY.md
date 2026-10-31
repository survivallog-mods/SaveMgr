# SurvivalLog.SaveMgr — 实机验收记录（2026-10-31，VERIFY）

> 环境：游戏 1.1.20151（自更后）；SaveMgr 0.0.0（脚手架版本号，作者定号前实测构建）；
> 方法：全自动化（cmd.txt 触发通道 + CDP WebUI 驱动 + WM_CLOSE 优雅退程），trace 自证。
> 证据：`docs/2026-10-31-实机trace存档.txt`、`docs/2026-10-31-注入实机实录.md`。

## 验收结果

| # | 项 | 结果 | 证据 |
|---|---|---|---|
| 1 | 插件加载 + patch 挂载 | ✅ | trace `patch 挂载成功：Patch_CaptureDaySnapshot` |
| 2 | 主菜单手动备份（cmd manual ≡ F9） | ✅ | `manual: 第117天 → D117_20261031_214700`，backups/ 目录结构完整（save.bytes+meta.json） |
| 3 | 结算钩子真实触发 | ✅ | `hook: CaptureDaySnapshot(day=118)`（满速跑过日结算） |
| 4 | 自动备份全链（hook→SaveGame→mtime→抄档） | ✅ | 21:49:43 hook → 21:49:45 强制持久化 → 21:49:47 `auto: D118_20261031_214947`（≤4s） |
| 5 | 注入式回档（cmd restore ≡ F10） | ✅ | `inject: 身份重写 → …21_47_16` + `AddHistoryEntry done` + 列表顶部条目可见（CDP DOM 断言） |
| 6 | 注入档可玩 | ✅ | 进入战斗 UI，正常游玩至结算 |
| 7 | **注入档独立性（写盘不落原档）** | ✅ | 游玩+结算全程原档 mtime 不动（21:40:45 基线），注入档自己主档/snap4/备份目录各归其位 |
| 8 | 守卫负测试（局内 restore 拒绝） | ✅ | 无新档产生（`not-at-main-menu` 路径） |
| 9 | 卸载还原 | ✅ | 删探针 dll；SaveMgr 删 dll 后游戏侧零残留（备份留存 plugins-data 属预期） |
| 10 | 单测 | ✅ | 35/35 ALL GREEN（IntervalPolicy/BackupStore/BackupService 状态机/RestoreInjector 纯逻辑） |

## 已知行为/边界（README 同步）

- 自动备份新鲜度=**hook 后强制 SaveGame 的即时落盘**；手动备份新鲜度=最近一次持久化（主菜单时=上次退出时的状态）。
- 注入档回读列表时间列=注入时刻（游戏渲染约定），天数/角色等 meta 从备份字节自动恢复。
- 注入档**无原档的 snap/回溯附件**（原生时光回溯对它是空窗，重新积累）；无尽 meta（剧情无尽标签等）不由 AssignChildMetaFromGameSave 恢复，列表内不显模式标签（数据完整，玩法不受影响）。
- 玩家开手机权限"跳过每日结算"时自然跨日仍会触发（调用点 B 无门控）；DayWatcher 兜底在位。
- 备份/恢复全走文件字节 + 游戏 API，零私有格式。

## 待办（后续版本）

- 三模式（休闲/无尽/普通）各有档实测一轮（本轮覆盖：普通注入档 + 原无尽档的 snap 行为侧证）。
- 游戏自更 1.1.20151 后 gamekb 未同步——锚点实测仍命中，下次 gamekb_sync 时复核 moddeps。
- ModCenter 面板 + 选择式恢复 UI（DESIGN §8 套件接入，另立计划）。
