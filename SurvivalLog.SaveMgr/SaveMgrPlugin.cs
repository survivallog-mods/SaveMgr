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
    internal static ConfigEntry<string> ManualHotkey;
    internal static ConfigEntry<int> KeepPerSave;
    internal static ConfigEntry<bool> RestoreEnabled;
    internal static ConfigEntry<bool> FallbackWatcher;
    internal static ConfigEntry<bool> CmdChannel;
    internal static ConfigEntry<bool> BackupNow;   // 面板动作位（cfg 即 RPC）：true→执行一次手动备份→回 false
    internal static ConfigEntry<bool> RestoreNow;  // 面板动作位：true→主菜单一键回档→回 false

    internal static BackupService Service;

    public override void Load()
    {
        Log = base.Log;
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=停全部功能（已落备份保留）");
        AutoBackup = Config.Bind("AutoBackup", "AutoBackup", true, "每日结算自动备份");
        IntervalDays = Config.Bind("AutoBackup", "IntervalDays", 5, "间隔天数：0=关闭，1=每天，N=每 N 天");
        ManualHotkey = Config.Bind("Manual", "ManualHotkey", "F9", "手动备份热键（UnityEngine.KeyCode 名）");
        KeepPerSave = Config.Bind("Manual", "KeepPerSave", 30,
            new ConfigDescription("每档备份保留上限（按天数升序删最老，含手动）", new AcceptableValueRange<int>(1, 999)));
        RestoreEnabled = Config.Bind("Restore", "RestoreEnabled", true, "一键回档（F10 主菜单注入最新备份）");
        FallbackWatcher = Config.Bind("AutoBackup", "FallbackWatcher", true, "兜底日翻检测（结算补丁静默失败时的保险）");
        CmdChannel = Config.Bind("Debug", "CmdChannel", true, "文件触发通道 plugins-data/SaveMgr/cmd.txt（manual/restore，本机自动化；关闭即禁）");
        BackupNow = Config.Bind("Action", "BackupNow", false, "【动作】立即备份当前档（面板按钮；执行后自动复位）");
        RestoreNow = Config.Bind("Action", "RestoreNow", false, "【动作】一键回档·注入最新备份为新档（需在主菜单；执行后自动复位）");

        Service = BuildService();
        InstallPatches();                     // 逐类隔离挂载（EndlessGuard 实证模式：坏一个不连坐）
        TickerHost.Install();

        Trace("loaded：服务+结算钩子+Ticker 接线（回档 Task 8 接入）");
        Log.LogInfo("[SaveMgr] 存档管理 loaded（cfg: interval=" + IntervalDays.Value + ", keep=" + KeepPerSave.Value + "）");
    }

    private static BackupService BuildService()
    {
        var dataRoot = Path.Combine(Paths.BepInExRootPath, "plugins-data", "SaveMgr");
        return new BackupService(
            store: new BackupStore(dataRoot),
            saveName: () => SaveEnv.CurrentSaveBase(),
            mainPath: () =>
            {
                var dir = SaveEnv.SavesDir();
                var name = SaveEnv.CurrentSaveBase();
                return dir == null || name == null ? null : SaveFiles.MainSavePath(dir, name);
            },
            forcePersist: () =>                                 // 游戏自己的持久化 API（退出存档同款）
            {
                try { SaveEnv.Gsm?.SaveGame(); }
                catch (System.Exception e) { Trace("forcePersist: " + e.Message); }
            },
            intervalDays: () => IntervalDays.Value,
            keepPerSave: () => KeepPerSave.Value,
            currentDay: () =>
            {
                var name = SaveEnv.CurrentSaveName();
                return name == null ? 0 : SaveEnv.CurrentDay(name);
            },
            gameBuild: "26157732",
            trace: Trace,
            autoEnabled: () => AutoBackup.Value);
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
