# SurvivalLog.SaveMgr — 存档管理（设计文档）

> 状态：**设计定稿待作者批**（2026-10-31）。工程脚手架与发版流水线已建，**功能代码未实现**。
> 版本号未定（硬规则 1：作者定号，csproj/Plugin 暂为 0.0.0 占位）。
> 机制实证基于 gamekb `2026-10-23_26157732`（dump + E8 调用方扫描 + Iced 反汇编），文中 RVA 均属该版。

## 0. 需求来源与定位

v1.4.0 候选 02 投票物料（已迁至 `assets/banners/`）作者对玩家的承诺：

| 痛点（VANILLA） | 承诺（MOD ON） |
|---|---|
| 手滑存错一档，几百小时囤货差点清零 | 多存位管理，重要节点随手备份 |
| 想回档反悔？游戏里没有后悔药 | 装修翻车一键回档随时反悔 |
| 大件装修不敢试 | 大胆试大胆囤，档在人在 |

本次设计新增需求：**每日结算时自动存档，间隔天数可设置；支持手动存档**。

**"回档"是头牌承诺 → 恢复能力进首期**（B 方案已静态验证通过，见 §3）。

## 1. 功能（首期范围，待作者确认）

1. **自动存档**：每日结算时机触发，按配置间隔 N 天（默认 3，1=每天，0=关闭）落一份持久备份。
2. **手动存档**：ModCenter 面板按钮/热键，随时对当前档落备份（新鲜度=最近一次游戏写盘，见 §2.1）。
3. **备份管理**：列表（天数/角色/时间/来源）、删除、导出 zip（进存档文件夹外长期保存）。
4. **一键回档（注入式恢复）**：把备份以**新存档名**注入游戏读档列表——原档不动，随时可再回（B 方案）。
5. 总开关 + 各功能独立开关；删除 dll 后备份文件仍在 `plugins-data`，游戏本体零残留。

非目标：不改游戏原生存档内容/结构；不做云端同步；不做跨大版本存档迁移。
反目标：任何绕过游戏 API 手写 HistorySave/存档序列化的路径（StackLimit999 事故红线，见 §7）。

## 2. 机制实证

### 2.1 存档布局与写盘时机（E8 扫描 + 实盘文件时间戳双证）

存档目录 `%USERPROFILE%\AppData\LocalLow\LLS\SLGame\Saves\`（Steam Cloud 开启）：

| 文件 | 写入时机 | 写入者 |
|---|---|---|
| `Save_<时间戳>.bytes` + `.bak` | 退主菜单（`BattleToMain`）、章节倒计时结束（`SaveCountDownEnd`）、结局、重生等大节点；**每日结算不写主档** | `SaveGame()`（0x28E3430）调用方 17 处 |
| `Save_<名>.snap0~N.bytes` | **无尽/轮回档的每日结算**滚动写（普通档不写！），窗口 4~6 格 | `CaptureDaySnapshotCore`（0x28D1E30） |
| `HistorySave.bytes` | 列表/全局进度变更时 | `AddHistoryEntry`/`PersistHistoryData` |

**~~snap 与主档同构~~（静态结论，已被实机推翻）**：`.snapN` 是 `RebirthDaySnapshot` 元数据包装（`{Version,Day,SaveFileName,Child:SaveChildData}`，dump.cs:87451），**非完整档**；且"每结算写盘"本身是无尽系统行为——**普通档结算与退出均不落盘**（2026-10-31 三轮实机，见 docs/2026-10-31-注入实机实录.md）。
→ **备份字节源=主档**；自动备份=hook 后主动 `SaveGame()` 强制持久化（游戏退出存档同款公开 API），等主档 mtime 变化后抄走（≤4s 实测）。

实盘佐证（2026-10-31）：作者无尽档当日 snap0@11:02…snap3@18:32（结算时刻）+主档 19:53（退出）；普通注入档结算/退出 mtime 均纹丝不动。

### 2.2 结算链路（自动存档锚点）

```
CountUpTimer.Update(0x26CC750) / CostTime(0x26CB810)   ← 日翻转两条路
  ├─ CaptureRebirthDaySnapshot()  (0x26CB730, 私有常规方法)
  │    └─ GameSaveManager.CaptureDaySnapshot(day)   (0x28D2640, 公开常规方法)
  │         └─ CaptureDaySnapshotCore → SaveGameFileCore   ← 写 .snapN
  └─ TriggerDaySettlement()        (0x26CBEE0, 私有常规方法)
```

**首选锚点：postfix `GameSaveManager.CaptureDaySnapshot(int day)`**——公开、常规方法（不触 async 状态机铁律）、自带 day 参数（间隔计数直接用）。
**兜底：Ticker 轮询 `CountUpTimer.GetDay()`**（0x64D7C0，公开）检测日翻——零补丁，若锚点被模式 gating 淘汰时启用（见 §6-②）。

### 2.3 注入原语 `AddHistoryEntry`（B 方案核心，反汇编 0x28CFB30 实锤）

```
AddHistoryEntry(fileName, name):
  new HistoryChild → FileName/Name → SaveOrigin=CurrentSaveOrigin
  → PeekGameSave(fileName)              ← 读 mod 刚复制的文件
  → AssignChildMetaFromGameSave         ← 补 Day/MaxDay/角色/章节元数据；写 [child+0x48]=1 (HasProgressMetaV2)
  → 追加 HistoryList 尾部
  → SerializeObject → TemporaryFile.WritFile   ← 立即同步写 HistorySave.bytes
```

**自足原语**：元数据、序列化、写盘、`.bak` 全走游戏自己的手。mod 前置动作只有一个——**先把备份字节复制成 `Saves/Save_<新名>.bytes`**（文件不就位则 meta 不补、列表不显示）。顺序不能反。

**实机追加两律（2026-31-31 实录）**：
1. **Name 必须是时间戳串**（`yyyy_MM_dd_HH_mm_ss`）——列表行时间列=`ParseSaveTimeShort(child.Name)`，非时间串→行被丢弃；
2. **身份必须重写**：字节内 `GameSaveData.FileName`(0x18) 仍指原档，不重写则玩注入档会把一切写盘路由到**原档文件**（实锤事故，已恢复）。重写走 `PeekGameSave` 反序列化→改 `FileName/Name`→`SerializeObject`+`TemporaryFile.WritFile` 写回（游戏自己的 API）；Peek 失败即删文件拒交付。

### 2.4 列表显示与加载校验（反汇编 0x2BB9980 / 0x28DABA0 实锤）

显示：主菜单读档 → `Reducer_Web_MainUI.RebuildSaveList` 倒序遍历 `HistoryList`，三过滤：非 legacy + `HasProgressMetaV2` + `IsSaveFilePresent`。注入条目三者全过，且**追加在尾部=列表顶部**。`LastPlayFileName` 不被改（反汇编确认）→ 不劫持"继续游戏"。

加载：`Ac_MainUI_LoadSave` → `EnterBattle(fileName, isLoading:true)` → `GetGameSave` → `LoadWithBackupFallback`。两疑点已排除：
- `RebuildIntegrityBaseline` 在读档**之后**从数据重建，是顾问指标**不是拦截门**；
- `EnterNewerDataFileBlock` 只拦"更新版本游戏写的档"，同版本字节副本放行。

### 2.5 mod 侧调用先例

`BaseSingleton<GameSaveManager>.Instance` 直调与 PlanPick/PreDisasterSpeed/WelcomeTip 同款（5 处先例）。注入环节**零 Harmony**；须主线程（Ticker Update 模式现成）。

## 3. 实现选型（方案对比与定案）

| 方案 | 恢复形态 | 侵入度 | 裁决 |
|---|---|---|---|
| A 纯外部备份器 | 关游戏后文件还原 | 最低 | **首期底盘**（自动+手动+管理） |
| B = A + 游戏内注入新档 | 原生读档列表出现备份入口 | 中（全程走游戏 API） | **进首期**（banner 承诺"一键回档"；§2.3/2.4 已验证） |
| C 扩容原生快照槽 | patch `GetSnapshotSlotCount` | 最高 | **否决**（不提供永久备份，改行为风险大） |

组件划分（均待实现）：

| 组件 | 职责 | 挂接方式 |
|---|---|---|
| SettlementHook | 结算时机 + day 计数 + 间隔判断 | Harmony postfix `CaptureDaySnapshot`（逐类隔离挂载，EndlessGuard 模式） |
| BackupStore | 字节复制/md5双读/滚动清理/导出 | 纯文件操作，`BepInEx/plugins-data/SaveMgr/backups/` |
| BackupService | 结算→SaveGame 强制持久化→主档 mtime 等待状态机→备份 | 非阻塞 Tick 驱动，15s 超时放弃 |
| RestoreInjector | 复制成新档名 + **身份重写** + `AddHistoryEntry`（Name=时间戳） | 主菜单时机，主线程直调；Peek 失败删文件拒交付 |
| Ticker | 兜底日翻检测 + UI 刷新 | MonoBehaviour Update（public + 双构造器，RoleUnlock 实证模式） |
| MenuButtons | 局内 HUD「立即存档/读取存档」实体按钮（读取存档=Entrance.BattleToMain+自动点官方菜单项弹官方列表） | Root MutationObserver 注入（ModCenter MenuEntryInjector 同款）；主菜单不加按钮（作者拍板） |

## 4. 备份文件布局

```
BepInEx/plugins-data/SaveMgr/
  backups/<原档名>/
    D<day>_<yyyyMMdd_HHmmss>/
      save.bytes          # 主档字节（来源=结算时刻的 snap / 手动时的主档+最新snap）
      meta.json           # {day, player, chapter, source(自动/手动), gameBuild, createdAt, md5}
  exports/                # 用户导出 zip 落点
```

清理策略：每档保留上限可配（默认 30 份），超限删最老；导出不受限。

## 5. 配置（cfg `com.local.survivallog.savemgr`）

| 键 | 默认 | 说明 |
|---|---|---|
| Enabled | true | 总开关 |
| AutoBackup | true | 结算自动存档 |
| IntervalDays | 5 | 间隔天数（0=关，1=每天；2026-10-31 作者定默认 5） |
| ManualHotkey | F9 | 手动存档热键（主菜单/局内均可） |
| KeepPerSave | 30 | 每档备份保留上限 |
| RestoreEnabled | true | 一键回档（注入）开关 |
| Action.BackupNow | false | 【面板动作位】true→执行一次手动备份→自动复位（ModCenter 按钮） |
| Action.RestoreNow | false | 【面板动作位】true→主菜单一键回档→自动复位（ModCenter 按钮） |

## 6. 开工前实证清单（规则 12：不猜，实锤前不写实现）

1. ~~写盘同步性~~ **✅ 已实证（2026-10-31，docs/2026-10-31-锚点实证.md）**：`SaveGameFileCore` 内部 `TemporaryFile.WritFileAsync` **未等待** + 外层 BeginBatchSave/EndBatchSave 再延迟一层 → postfix 仅作时机信号，字节复制必须等 mtime 稳定 + md5 双读重试（转正为必要路径）。
2. ~~模式 gating~~ **✅ 已实证（同上）**：自然日翻转调用点（CostTime 内 day++ → TriggerDaySettlement → CaptureRebirthDaySnapshot）**无任何模式门控**；无尽共用 CountUpTimer（紧邻驱动 EndlessManager.OnHourChanged）。唯一门控 = 手机权限"跳过每日结算"（C2S_Permissions.IsSkipDailySettlement，仅挡调用点 A，不挡自然跨日）。**边界**：快照槽位设 0 时 Core 早退、postfix 空触发 → 字节源只在 NewestSnapPath mtime ≤120s 时采用，否则跳过+trace。
3. ~~snap 独立可读性实机~~ **✅ 已实证并推翻前提（2026-10-31，docs/2026-10-31-注入实机实录.md）**：snap 不可独立读（元数据包装）；主档字节注入可读可玩；身份重写后注入档完全独立（原档 mtime 全程不动）。VERIFY.md 全项通过。

## 7. 风险与红线

- **StackLimit999 前科**：该存档 mod 因加载时改写存档物品数据实锤误伤合法档，已禁发。本 mod 红线：**对游戏存档目录的写入只有一种**——RestoreInjector 复制备份字节成新档名（游戏文件系统的"新增"，非改写），其余全部只读。
- **Harmony 铁律**：禁挂 async 状态机 MoveNext（2026-10-26 铆律）；逐类隔离挂载，单个补丁失败不连坐；锚点仅常规方法。
- **注入时机**：限主菜单（historyData 已 Initialize、避开战斗中 HistorySave 并发写）。
- **Steam Cloud**：注入新档会触发云同步上传，单机单机无冲突；备份目录在 `plugins-data` 不入云。
- **规则 11**：实现定稿挂钩点后同步 `suite/tools/gamekb/moddeps.json`。
- **发版**：走仓内 `release.ps1`；N 网另走确认门（规则 14）。

## 8. 套件接入清单（发版前，均在 suite 侧）

- [ ] `tools/push_modshared.ps1` 仓列表加 SaveMgr（第 13 仓）
- [ ] suite `README.md` MOD 一览表加行
- [x] ModCenter 注册（2026-10-31：Meta.cs 中文分组页含两个动作按钮「cfg 即 RPC」模式 + RepoDecision.RepoKeys 第 13 仓；实机验证面板点击→备份落地）
- [ ] 安装器 [Files]/InstallDelete 名单
- [ ] AGENTS.md 硬规则 15 的"12 个 mod 仓"口径更新（发版时）

## 9. 文件

```
SaveMgr/
├── SurvivalLog.SaveMgr/
│   ├── SaveMgrPlugin.cs        # 插件入口（当前为脚手架 stub，仅自证 trace）
│   ├── DESIGN.md               # 本文
│   ├── CHANGELOG.md
│   ├── docs/                   # 可行性分析 + B 方案验证证据链（2026-10-31）
│   └── SurvivalLog.SaveMgr.csproj
├── ModShared/                  # 套件镜像（push_modshared 生成物，勿手改）
├── assets/banners/             # v1.4.0 候选02 投票物料（自 suite 迁入）
├── release.ps1                 # 单发流水线（自包含，PlanPick 同款）
├── README.md / ReleaseREADME.md / LICENSE / .gitignore
```

## 10. 迭代维护清单

- 2026-10-31 初版：可行性定案（A+B 首期，C 否决）+ B 方案静态验证全链路通过 + 仓库初始化。
- 2026-10-31 三版（作者 UX 拍板）：主菜单不加按钮；局内 HUD「立即存档/读取存档」实体按钮（读取存档=BattleToMain 接力自动弹官方存档列表，Gsm.BattleToMain 只存档不切场景→改 MainAction.Entrance.BattleToMain，实机实证）；面板/cfg 砍到 3 项；Steam Cloud 会复活本地删除的档——测试档清理必须走官方列表 ×（历史+文件+云一体）。
- 2026-10-31 二版（MVP 实现完成）：单测 35/35；实机验收全项通过（VERIFY.md）；静态结论四处被实机修正（snap 真相/普通档不落盘/Name 时间戳律/身份重写律，§2 已更新）；注入独立性实证。**版本号 0.0.0 待作者定**；三模式各有档实测、ModCenter 面板为后续。
