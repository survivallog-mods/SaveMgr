# SaveMgr MVP 实现计划（自动备份 + 手动备份 + 管理 + 注入式回档）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现存档管理 mod 首期（A+B）：每日结算按间隔自动备份、热键手动备份、备份管理（列表/清理/导出）、主菜单热键注入最新备份为新档。

**Architecture:** 纯逻辑层（IntervalPolicy/BackupMeta/BackupStore/SaveFiles）零游戏依赖、可离线单测；interop 薄层（SaveEnv）+ Harmony postfix（CaptureDaySnapshot）+ Ticker 兜底与热键；恢复=字节复制成新档名 + 游戏自己的 AddHistoryEntry。对游戏存档目录的唯一写入=注入新档名。

**Tech Stack:** BepInEx 6 IL2CPP + HarmonyX + Il2CppInterop（net6.0，游戏 2026-10-23_26157732）；测试=sdk 便携 dotnet 控制台自测工程（零外部包）；实证=套件 disasm_tool + 一次性探针。

**Spec:** `SurvivalLog.SaveMgr/DESIGN.md`（证据链：`docs/2026-10-31-可行性分析.md`、`docs/2026-10-31-B方案注入验证.md`）

**Scope note:** ModCenter 面板页签与套件接入（DESIGN §8）不在本计划，发版前另行走。备份的选择式恢复 UI（选某一天注入）延到 ModCenter 阶段；本计划回档=注入该档最新备份（F10）。

## Global Constraints（每个任务默认继承）

- 版本号 `0.0.0` 占位，**不得自定/自增**（硬规则 1）；csproj 与 Plugin.cs Version 必须一致。
- Harmony：只挂常规方法，**禁 async 状态机 MoveNext**；逐类隔离挂载（EndlessGuard 模式），单补丁失败不连坐。
- 自证用 trace 文件 `BepInEx/SaveMgr_trace.txt`，不依赖 LogOutput.log（硬规则 9）；UI/行为结论必须实机验证后才算数（硬规则 10）。
- 构建：`"E:/Game/Mod/survivallog-mods/sdk/dotnet/dotnet.exe" build <proj> -c Release > log 2>&1; echo $?` 判退出码，禁 `| tail`（硬规则 8）。
- 游戏在跑（SurvivalLog.exe 活跃）绝不杀进程，实机步骤择时（硬规则 3）。
- 备份只落 `BepInEx/plugins-data/SaveMgr/`；对 `Saves/` 只读 + 注入新档名这一种写。
- 所有游戏侧调用包 try/catch + trace，任何异常不得穿透到游戏主循环。
- ModShared/ 是套件镜像勿手改；.ps1 一律 ASCII；提交外科手术式 `git add <明确清单>`（硬规则 2）。
- 存档目录：`%USERPROFILE%\AppData\LocalLow\LLS\SLGame\Saves\`；锚点 RVA 属 gamekb `2026-10-23_26157732`。

## Review Focus（最可能咬人的五类输入，测试钉在所属任务）

1. **结算写盘未完成/文件占用 → 复制半截档**：CreateBackup 必须双读 md5 稳定 + IOException 重试（≤3 次、间隔 500ms）后放弃并 trace → Task 4 Step 2（锁定文件用例）。
2. **同一结算日重复信号（CostTime/Update 双路 + 兜底 watcher 撞车）**：同日幂等，`currentDay == lastAutoDay` 直接跳过 → Task 3 Step 2（`SameDayDuplicate` 用例）。
3. **备份目录残缺（崩溃孤儿：无 meta / 坏 json）**：ListBackups 跳过不抛、Prune 只按完整备份排序删除 → Task 4 Step 2（孤儿目录用例）。
4. **注入档名同秒碰撞/已存在**：名字占用时重试生成 ≤3 次，仍撞则失败报因不覆盖 → Task 8 Step 2（PathIsFree + 重试用例）。
5. **磁盘满/目录被删/只读**：BackupStore 全部文件操作失败返回 `null`/`-1`/原因串，不抛穿 BackupService 边界、游戏不崩 → Task 4 Step 2（dest 根目录被占位为文件时 CreateBackup 返回 null）。

---

### Task 1: 静态实证——锚点写盘同步性 + 模式 gating（DESIGN §6-①②）

**Files:**
- Create: `docs/2026-10-31-锚点实证.md`
- Modify: `SurvivalLog.SaveMgr/DESIGN.md`（§6 勾选两项 + §2.2 锚点定案改写）

**Interfaces:**
- Produces: 锚点定案结论（postfix `CaptureDaySnapshot` 是否成立；兜底 DayWatcher 是否转正），Task 7 依赖此结论。

- [ ] **Step 1: 反汇编 `CaptureDaySnapshotCore`（RVA 0x28D1E30，十进制 42845232）**

Run: `cd suite/tools/disasm_tool/bin/Release/net6.0 && "E:/Game/Mod/survivallog-mods/sdk/dotnet/dotnet.exe" disasm.dll <gamekb>/binaries/GameAssembly.dll <gamekb>/il2cpp/script.json 42845232 400`
判读：结尾是否直接 `call SaveGameFileCore`(0x28E2410) 同步写；还是写 `batchSavePending/deferredSaveArmed` 走延迟帧。**若延迟 → Task 7 的字节源改为主档 mtime 稳定后复制（兜底策略），postfix 仍作时机信号。**

- [ ] **Step 2: 反汇编 `CaptureRebirthDaySnapshot`（0x26CB730）与其 CostTime(0x26CB810)/Update(0x26CC750) 内调用点前后的分支**

判读：调用是否被 `GetSnapshotSlotCount()`(0x28DC460) 或模式开关 gating（slots=0/无尽/休闲差异）。同时 grep dump.cs 确认无尽模式日翻是否同走 CountUpTimer。
**若 gated → 锚点改 `TriggerDaySettlement`(0x26CBEE0) postfix 或 DayWatcher 转正，写进定论。**

- [ ] **Step 3: 结论落 `docs/2026-10-31-锚点实证.md`，DESIGN §6 勾选①②、§2.2 按结论改写**

- [ ] **Step 4: Commit**

```bash
git add docs/2026-10-31-锚点实证.md SurvivalLog.SaveMgr/DESIGN.md
git commit -m "docs(evidence): 锚点写盘同步性与模式gating实证——CaptureDaySnapshot 锚点定案"
```

### Task 2: 实机探针——snap 独立可读 + AddHistoryEntry 注入链路（DESIGN §6-③，作者在场）

**Files:**
- Create: `suite/SurvivalLog.SaveInjectProbe/`（探针工程，BepInEx 插件，**永不发版/不进任何发布名单**，硬规则 5）
- Create: `docs/2026-10-31-注入实机实录.md`

**Interfaces:**
- Produces: 实录结论「snap 复制→新档名→AddHistoryEntry→原生列表可见→可进游戏」。Task 8 的 RestoreInjector 按实录校准；失败则 B 方案降级回 A（DESIGN §3 重议）。

- [ ] **Step 1: 写探针插件**（主菜单按 F11：取 `GetLastPlayFileName` → 找该档最新 `.snap*.bytes` → `GenerateNewSaveFileName()` 复制成新档 → `BaseSingleton<GameSaveManager>.Instance.AddHistoryEntry(newName, "探针注入")` → 每步写 `SaveInjectProbe_trace.txt`）。csproj 参照 SaveMgr（引用集：BepInEx×2/Il2CppInterop/Il2Cppmscorlib/HotUpdate/UnityEngine.CoreModule/InputLegacyModule）。

- [ ] **Step 2: 构建 + 部署**（exit code 判定；dll 拷 `<游戏>\BepInEx\plugins\`；游戏未运行时才操作文件）

- [ ] **Step 3: 作者实机**：启动游戏到主菜单 → F11 → 打开读档列表看顶部新条目（天数/角色正确）→ 点进游戏跑过一次结算 → 退出。全程 trace 与截图/记录落 `docs/2026-10-31-注入实机实录.md`。

- [ ] **Step 4: 清理探针 dll + Commit**（SaveMgr 仓提交实录文档；探针工程提交进 suite 仓，提交信息注明"探针，永不发版"）

### Task 3: 测试基建 + IntervalPolicy / BackupMeta（纯逻辑）

**Files:**
- Create: `SurvivalLog.SaveMgr/IntervalPolicy.cs`、`SurvivalLog.SaveMgr/BackupMeta.cs`
- Create: `SurvivalLog.SaveMgr.Tests/`（console 工程 + Program.cs 断言器）

**Interfaces:**
- Produces:
  - `static class IntervalPolicy { static bool ShouldBackup(int currentDay, int? lastAutoDay, int intervalDays); static bool IsSameDayDuplicate(int currentDay, int? lastAutoDay); }` 语义：intervalDays<=0 恒 false；lastAutoDay==null 且 interval>0 → true；否则 `currentDay-lastAutoDay>=intervalDays`；IsSameDayDuplicate=`currentDay==lastAutoDay`。
  - `sealed class BackupMeta { int Day; string SaveName,Source,Label,GameBuild,CreatedAt,Md5; string[] Files; }`（System.Text.Json 序列化）。
  - 测试工程：`dotnet run --project SurvivalLog.SaveMgr.Tests -c Release`，exit code=失败数（0=全绿）。csproj 以 `<Compile Include="../SurvivalLog.SaveMgr/IntervalPolicy.cs" />` 等链接纯文件，零游戏引用。

- [ ] **Step 1: 建测试工程 + 写失败用例**：`ShouldBackup_Off_WhenIntervalZero`、`ShouldBackup_First_WhenNoLastDay`、`ShouldBackup_True_AtOrBeyondInterval`（day 10 last 7 interval 3 → true；day 9 → false）、`SameDayDuplicate_True`（10,10）/`False_Null`（10,null）。

- [ ] **Step 2: 跑测红**：`sdk dotnet run --project SurvivalLog.SaveMgr.Tests -c Release`，预期编译错（类不存在）。

- [ ] **Step 3: 实现两类**（签名见上，System.Text.Json 属 net6 基础库）。

- [ ] **Step 4: 跑测绿**（exit=0）。

- [ ] **Step 5: Commit**：`git add SurvivalLog.SaveMgr/IntervalPolicy.cs SurvivalLog.SaveMgr/BackupMeta.cs SurvivalLog.SaveMgr.Tests/`，`feat(core): 间隔策略与备份元数据（含自测工程）`。

### Task 4: BackupStore——复制/md5/meta/滚动清理/导出/容错（纯文件系统）

**Files:**
- Create: `SurvivalLog.SaveMgr/BackupStore.cs`、`SurvivalLog.SaveMgr/SaveFiles.cs`
- Test: `SurvivalLog.SaveMgr.Tests/Program.cs` 追加用例

**Interfaces:**
- Consumes: Task 3 的 BackupMeta。
- Produces:
  - `sealed class BackupStore { ctor(string savesDir, string dataRoot); string CreateBackup(string sourceFile, string saveName, int day, string source, string label, string gameBuild); List<BackupMeta> ListBackups(string saveName); BackupMeta ReadMeta(string backupDir); int Prune(string saveName, int keep); string ExportZip(string backupDir, string destDir); string LatestBackupDir(string saveName); int? ReadLastAutoDay(string saveName); void WriteLastAutoDay(string saveName, int day); }`
    语义：备份目录 `<dataRoot>/backups/<saveName>/D<day>_<yyyyMMdd_HHmmss>/`（save.bytes + meta.json，先写临时名再改名）；CreateBackup 双读 md5 稳定 + IO 重试 ≤3×500ms，任何失败返回 null 不抛；state 存 `<dataRoot>/state.json`；ListBackups 跳过 ReadMeta==null 的目录；Prune 只对完整备份按 CreatedAt 升序删超限，返回删除数；ExportZip 产 `D<day>_<ts>.zip` 返回路径。
  - `static class SaveFiles { static string NewestSnapPath(string savesDir, string saveName); static string MainSavePath(string savesDir, string saveName); }`（按 mtime 最新 `.snap*.bytes`；无 snap → null）。

- [ ] **Step 1: 写失败用例**（临时目录夹具）：`CreateBackup_Writes_Bytes_And_Meta`（md5=源文件）、`CreateBackup_Null_WhenSourceLocked`（源文件用 FileStream 独占开 → 期望 null 且无半截目录残留）、`CreateBackup_Null_WhenDestBlocked`（backups/<name> 占位为普通文件 → null）、`ListBackups_Skips_Orphan`（手工建无 meta 目录 → 不在列表、不抛）、`Prune_Keeps_Newest_N`（5 份 keep 3 → 删 2 份最老）、`SameSecond_DirNames_Not_Clobbered`（同秒两次 → 两个目录名不互踩，允许 `_2` 后缀）、`State_RoundTrip`、`NewestSnap_By_Mtime`。
- [ ] **Step 2: 跑测红**
- [ ] **Step 3: 实现 BackupStore/SaveFiles**（临时名+rename 保证不落半截；目录名同秒加序号后缀）
- [ ] **Step 4: 跑测绿**
- [ ] **Step 5: Commit**：`feat(core): BackupStore 字节级备份/滚动清理/导出/容错 + SaveFiles 选取`

### Task 5: SaveEnv——interop 薄层（编译 + trace 自证；实机归 Task 9）

**Files:**
- Create: `SurvivalLog.SaveMgr/SaveEnv.cs`

**Interfaces:**
- Produces: `static class SaveEnv { static GameSaveManager Gsm { get; } static string SavesDir(); static string CurrentSaveName(); static int CurrentDay(string saveName); static bool AtMainMenu { get; } }`
  语义：Gsm=`BaseSingleton<GameSaveManager>.Instance`（null 安全）；SavesDir 优先 `Path.GetDirectoryName(TemporaryFile.GetFilePath(...))`——**先在 dump.cs grep `TemporaryFile` 核对 GetFilePath 签名**，不通则 `Application.persistentDataPath + "/Saves"`；CurrentSaveName=`GetLastPlayFileName()`（空→null）；CurrentDay=`GetHistoryList()` 匹配 FileName 的 `Day`，无匹配 → -1；AtMainMenu=`Gsm!=null && !Gsm.IsInBattle`。每个成员 try/catch + Trace 失败原因。

- [ ] **Step 1: 核对 TemporaryFile.GetFilePath 签名**（grep dump.cs），把选定路径来源写成 SaveEnv 头注释
- [ ] **Step 2: 实现**（签名见上；不写单测，Task 9 实机自证）
- [ ] **Step 3: 构建 exit=0**
- [ ] **Step 4: Commit**：`feat(env): SaveEnv interop 薄层——存档路径/当前档名/天数/主菜单判定`

### Task 6: BackupService + Plugin 接线（cfg/热键/手动备份）

**Files:**
- Create: `SurvivalLog.SaveMgr/BackupService.cs`、`SurvivalLog.SaveMgr/SaveMgrTicker.cs`
- Modify: `SurvivalLog.SaveMgr/SaveMgrPlugin.cs`、`SurvivalLog.SaveMgr/SurvivalLog.SaveMgr.csproj`（补 UnityEngine.InputLegacyModule 引用，HintPath 同 PlanPick）

**Interfaces:**
- Consumes: Task 3/4/5 全部接口。
- Produces:
  - `sealed class BackupService { ctor(BackupStore store); void OnSettlement(int day, string snapPath); string ManualBackup(); }` 语义：OnSettlement=同日幂等(IntervalPolicy)→`CreateBackup(snapPath, …, "auto", $"第{day}天")`→成功才 `WriteLastAutoDay`；ManualBackup=字节源 `NewestSnapPath ?? MainSavePath`，`"manual"`，返回备份目录或 null。
  - cfg 键（DESIGN §5 原值）：`General.Enabled=true`、`AutoBackup.AutoBackup=true`、`AutoBackup.IntervalDays=3`、`Manual.ManualHotkey=F9`、`Manual.KeepPerSave=30`、`Restore.RestoreEnabled=true`。
  - `SaveMgrTicker`：2s tick（unscaledDeltaTime 累计，public Update + 双构造器，RoleUnlock 实证模式）；轮询 `Input.GetKeyDown(F9)` → 手动备份（异常隔离 trace）。
  - `static class SaveMgrRuntime { static void Enqueue(Action a); }`（主线程动作队列，落 `SaveMgrTicker.cs`；Ticker 每 tick 排空执行，单条异常隔离）——Task 7/8 消费。

- [ ] **Step 1: 写失败用例（纯部分）**：`OnSettlement_Idempotent_SameDay`（day 10 两次 → 第二次不再生成目录）、`OnSettlement_Respects_Interval`（interval 3：day10 备、day12 不备、day13 备）、`ManualBackup_NoSnap_FallsBackToMain`（只放主档 → 以主档成备份）。BackupService 经 ctor 注入 `Func<string>` 型取路径委托以便纯测。
- [ ] **Step 2: 跑测红**
- [ ] **Step 3: 实现 service + plugin 接线 + ticker**（Harmony 尚未挂，OnSettlement 无人调用属预期）
- [ ] **Step 4: 跑测绿 + 构建 exit=0**
- [ ] **Step 5: Commit**：`feat(svc): 结算/手动备份服务 + cfg 接线 + Ticker 热键（F9）`

### Task 7: SettlementHook——Harmony postfix + DayWatcher 兜底 + moddeps 同步

**Files:**
- Create: `SurvivalLog.SaveMgr/Patch_CaptureDaySnapshot.cs`
- Modify: `SurvivalLog.SaveMgr/SaveMgrTicker.cs`（DayWatcher）、`SaveMgrPlugin.cs`（逐类隔离挂载，EndlessGuard 模式）
- Modify: `suite/tools/gamekb/moddeps.json`（规则 11，SaveMgr 挂钩点条目——suite 仓外科手术式提交）

**Interfaces:**
- Consumes: Task 1 定案锚点；Task 6 的 `SaveMgrRuntime.Enqueue` / `BackupService.OnSettlement`。
- Produces: `[HarmonyPatch(typeof(GameSaveManager), nameof(GameSaveManager.CaptureDaySnapshot))]` postfix `static void Postfix(int day)`（try/catch 全包 + Trace；直接 Enqueue 主线程处理：postfix 本就在主线程，Enqueue 仅为统一异常边界）。DayWatcher：Ticker 内每 tick 比对 `SaveEnv.CurrentDay(saveName)`，day 增量且 60s 内无 postfix 信号 → 兜底触发一次 OnSettlement（字节源=NewestSnapPath，可能为刚写的 snap）。cfg `AutoBackup.FallbackWatcher=true`。

- [ ] **Step 1: 实现 patch + watcher**（按 Task 1 定论：若写盘延迟 → Enqueue 的处理动作先等 `NewestSnapPath` mtime 稳定 1s 再复制——由 BackupStore 双读 md5 兜底）
- [ ] **Step 2: 构建 exit=0**（无单测；行为验证归 Task 9）
- [ ] **Step 3: moddeps.json 加 SaveMgr 条目 + suite 仓外科手术式提交**
- [ ] **Step 4: Commit（SaveMgr 仓）**：`feat(hook): CaptureDaySnapshot postfix + DayWatcher 兜底（逐类隔离挂载）`

### Task 8: RestoreInjector——注入回档（F10，主菜单）

**Files:**
- Create: `SurvivalLog.SaveMgr/RestoreInjector.cs`
- Modify: `SurvivalLog.SaveMgr/SaveMgrTicker.cs`（F10 → Enqueue 注入）

**Interfaces:**
- Consumes: Task 4 `LatestBackupDir/ReadMeta`、Task 5 SaveEnv、Task 2 实录校准的行为。
- Produces: `static class RestoreInjector { static (bool Ok, string Reason) Inject(string backupDir, string label); static bool PathIsFree(string savesDir, string fileName); }`
  语义：守卫 `RestoreEnabled && AtMainMenu`；`GenerateNewSaveFileName()` 取名 → `PathIsFree` 不通过则重取 ≤3 次，仍撞 → `("false","name-collision")`；复制 `save.bytes` → `Saves/Save_<新名>.bytes`；`Gsm.AddHistoryEntry(新名, label)`；每步 trace；任何异常 → `(false, 原因)` 不抛。F10 逻辑：`LatestBackupDir(SaveEnv.CurrentSaveName())` 注入，label=`"回档·第{meta.Day}天"`。

- [ ] **Step 1: 写失败用例（纯部分）**：`PathIsFree_True_New` / `PathIsFree_False_Existing`；名字生成经委托注入的 `RetryUntilFree_Fails_After3`（恒返回同名 → 失败原因 name-collision，且 Saves 目录无写入）。
- [ ] **Step 2: 跑测红**
- [ ] **Step 3: 实现**（interop 调用对齐 Task 2 探针实录）
- [ ] **Step 4: 跑测绿 + 构建 exit=0**
- [ ] **Step 5: Commit**：`feat(restore): 注入式回档——新档名复制 + AddHistoryEntry 注册（F10 主菜单）`

### Task 9: 实机验收（作者在场，硬规则 3/9/10）

**Files:**
- Create: `SurvivalLog.SaveMgr/VERIFY.md`（验收记录：步骤/结果/trace 摘录）
- Modify: `SurvivalLog.SaveMgr/DESIGN.md`（§6 勾选、§10 迭代清单补行）

- [ ] **Step 1: 部署**（构建 exit=0 → dll 拷 `<游戏>\BepInEx\plugins\`；游戏未运行时操作）
- [ ] **Step 2: 常规模式实机**：进档玩过一次结算 → `plugins-data/SaveMgr/backups/<档名>/` 出现 `D<n>_…/`（meta.day 正确）；改 IntervalDays=1 再过一天 → 备份；间隔 3 不补备份；F9 手动 → manual 备份；`SaveMgr_trace.txt` 有各步自证行。
- [ ] **Step 3: 回档实机**：退到主菜单按 F10 → 读档列表顶部出现"回档·第N天" → 进游戏核对天数/物品 → 原档条目仍在。
- [ ] **Step 4: 模式覆盖**：休闲+无尽各过一次结算，确认锚点/兜底至少一条路触发备份（对照 Task 1 定论）。
- [ ] **Step 5: 卸载还原**：删 dll 重启 → 游戏行为原版；备份目录留存（预期行为，README 已写）。
- [ ] **Step 6: 结果落 VERIFY.md + DESIGN 维护清单 + Commit**：`docs(verify): MVP 实机验收记录`

### Task 10: 收尾——README/CHANGELOG 草案与遗留清单

**Files:**
- Modify: `README.md`（热键/目录说明校准实机行为）、`SurvivalLog.SaveMgr/CHANGELOG.md`（发版节草稿，**版本号留作者**）

- [ ] **Step 1: 校准文档**（F9/F10 语义、备份目录位置、注入档无 snap 附件的已知行为；明示 MVP 阶段"备份管理"=资源管理器直管普通文件夹——删除/导出即文件夹操作，面板化延至 ModCenter 阶段）
- [ ] **Step 2: Commit**：`docs: MVP 文档校准（发版节草稿，版本号待作者定）`

---

## 遗留（本计划外，另立计划/另走流程）

- ModCenter 面板页签 + 选择式恢复 UI + 备份管理面板（DESIGN §8 套件接入清单）
- 发版（作者定号 → 仓内 release.ps1；N 网确认门）
- push_modshared 仓列表/安装器名单等 suite 侧接入
