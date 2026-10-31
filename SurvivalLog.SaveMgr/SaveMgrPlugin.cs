using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace SurvivalLog.SaveMgr;

/// <summary>存档管理：结算自动备份（间隔可设）+ 手动备份（F9）+ 备份管理 + 注入式一键回档（F10，Task 8 接入）。
/// 机制与实证见 DESIGN.md（gamekb 2026-10-23_26157732；锚点实证见 docs/2026-10-31-锚点实证.md）。</summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class SaveMgrPlugin : BasePlugin
{
    public const string Guid = "com.local.survivallog.savemgr";
    public const string Name = "SaveMgr";
    public const string Version = "0.0.0"; // 占位：版本号由作者定（硬规则1），release.ps1 校验与本串一致

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> AutoBackup;
    internal static ConfigEntry<int> IntervalDays;

    internal static SavepointService Service;
    private static int _battleSinceTick = System.Environment.TickCount;
    private static bool _lastInBattle;

    /// <summary>SaveGame 唯一入口（手动立即存档）：**带战龄保护**——进档 <30s 抛拒
    /// （00:35 实锤：进档初期灌 SaveGame 会 native 崩；进档半分钟后多次实机安全）。</summary>
    internal static void PersistNow()
    {
        if (System.Environment.TickCount - _battleSinceTick < 30_000)
            throw new System.InvalidOperationException("刚进入游戏，请稍候半分钟再存档");
        // 复刻结算的持久化包裹（2026-11-01 01:05/01:09 实测：裸 SaveGame 只"武装延迟写"，
        // 世界不安全时永远不冲刷；Begin→SaveGame→End(false) 与游戏结算同构 = 立即冲刷）
        var gsm = SaveEnv.Gsm;
        if (gsm == null) throw new System.InvalidOperationException("gsm-null");
        gsm.BeginBatchSave();
        try { gsm.SaveGame(); }
        finally { gsm.EndBatchSave(false); }
    }

    /// <summary>立即存档就绪态：非战斗=可（菜单无风险）；战斗中=进档 ≥30s（防进档流程竞态崩溃）。</summary>
    internal static bool BattleReady()
    {
        try
        {
            var inb = SaveEnv.Gsm != null && SaveEnv.Gsm.IsInBattle;
            return !inb || System.Environment.TickCount - _battleSinceTick >= 30_000;
        }
        catch { return false; }
    }

    /// <summary>战龄刷新（Ticker 每拍调）：进战斗边沿重置计时。</summary>
    internal static void TrackBattleAge()
    {
        try
        {
            var inb = SaveEnv.Gsm != null && SaveEnv.Gsm.IsInBattle;
            if (inb && !_lastInBattle) _battleSinceTick = System.Environment.TickCount;
            _lastInBattle = inb;
        }
        catch { }
    }

    public override void Load()
    {
        Log = base.Log;
        // 2026-10-31 作者拍板极简面：全部配置只 3 项功能键（+ModCenter 侧栏启停位的 General.Enabled）。
        // 热键 F9/F10、兜底日翻、回档开关、cmd 通道一律硬编码不再暴露（README 记行为即可）。
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=停全部功能（已落备份保留）");
        AutoBackup = Config.Bind("AutoBackup", "AutoBackup", true, "每日结算自动备份");
        IntervalDays = Config.Bind("AutoBackup", "IntervalDays", 5, "间隔天数：0=关闭，1=每天，N=每 N 天");

        Service = BuildService();
        MenuButtons.BootScanPending(Path.Combine(dataRoot2(), "pending.json"));

        // 游戏内实体按钮层（主菜单「一键回档」/局内 ESC「立即存档」）由 Ticker 驱动，见 MenuButtons
        InstallPatches();                     // 逐类隔离挂载（EndlessGuard 实证模式：坏一个不连坐）
        TickerHost.Install();

        Trace("loaded：服务+结算钩子+Ticker 接线（回档 Task 8 接入）");
        Log.LogInfo("[SaveMgr] 存档管理 loaded（interval=" + IntervalDays.Value + "）");
    }

    private static string dataRoot2() => Path.Combine(Paths.BepInExRootPath, "plugins-data", "SaveMgr");

    private static SavepointService BuildService()
    {
        var dataRoot = Path.Combine(Paths.BepInExRootPath, "plugins-data", "SaveMgr");
        return new SavepointService(
            store: new StateStore(dataRoot),
            saveName: () => SaveEnv.CurrentSaveBase(),
            mainPath: () =>
            {
                var dir = SaveEnv.SavesDir();
                var name = SaveEnv.CurrentSaveBase();
                return dir == null || name == null ? null : Path.Combine(dir, name + ".bytes");
            },
            intervalDays: () => IntervalDays.Value,
            currentDay: () =>
            {
                var name = SaveEnv.CurrentSaveName();
                return name == null ? 0 : SaveEnv.CurrentDay(name);
            },
            savesDir: SaveEnv.SavesDir(),
            onNewSavepoint: MenuButtons.OnNewSavepoint,
            trace: Trace,
            persist: PersistNow);
    }

    /// <summary>逐类隔离挂载 Harmony 补丁（2026-10-25 教训：PatchAll 整包挂载时单个 IL 错炸整个 Load）。</summary>
    private static void InstallPatches()
    {
        var harmony = new HarmonyLib.Harmony(Guid);
        foreach (var t in typeof(SaveMgrPlugin).Assembly.GetTypes())
        {
            if (!t.IsClass || !t.IsDefined(typeof(HarmonyLib.HarmonyPatch), false)) continue;
            try { harmony.CreateClassProcessor(t).Patch(); Trace("patch 挂载成功：" + t.Name); }
            catch (System.Exception e) { Trace("patch 挂载失败 " + t.Name + ": " + e.Message); }
        }
    }

    /// <summary>自证文件（硬规则9：不依赖 LogOutput.log）。每行带时间戳。</summary>
    internal static void Trace(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "SaveMgr_trace.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { }
    }
}
