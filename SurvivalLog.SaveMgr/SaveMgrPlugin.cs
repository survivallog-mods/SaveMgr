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

    internal static BackupService Service;

    public override void Load()
    {
        Log = base.Log;
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=停全部功能（已落备份保留）");
        AutoBackup = Config.Bind("AutoBackup", "AutoBackup", true, "每日结算自动备份");
        IntervalDays = Config.Bind("AutoBackup", "IntervalDays", 3, "间隔天数：0=关闭，1=每天，N=每 N 天");
        ManualHotkey = Config.Bind("Manual", "ManualHotkey", "F9", "手动备份热键（UnityEngine.KeyCode 名）");
        KeepPerSave = Config.Bind("Manual", "KeepPerSave", 30, "每档备份保留上限（超限删最老，含手动）");
        RestoreEnabled = Config.Bind("Restore", "RestoreEnabled", true, "一键回档（F10 主菜单注入最新备份）");

        Service = BuildService();
        TickerHost.Install();

        Trace("loaded：脚手架+服务接线（结算钩子 Task 7 接入；回档 Task 8 接入）");
        Log.LogInfo("[SaveMgr] 存档管理 loaded（cfg: interval=" + IntervalDays.Value + ", keep=" + KeepPerSave.Value + "）");
    }

    private static BackupService BuildService()
    {
        var dataRoot = Path.Combine(Paths.BepInExRootPath, "plugins-data", "SaveMgr");
        return new BackupService(
            store: new BackupStore(null, dataRoot),          // savesDir 由 getter 每次解析（游戏就绪后）
            saveName: () => SaveEnv.CurrentSaveName(),
            autoSource: () =>                                // 结算时刻新鲜 snap（≤120s），否则 null=跳过
            {
                var dir = SaveEnv.SavesDir();
                var name = SaveEnv.CurrentSaveName();
                if (dir == null || name == null) return null;
                var snap = SaveFiles.NewestSnapPath(dir, name);
                if (snap == null) return null;
                return (DateTime.Now - File.GetLastWriteTime(snap)).TotalSeconds <= 120 ? snap : null;
            },
            manualSource: () =>                              // 手动：新鲜 snap 优先，回退主档
            {
                var dir = SaveEnv.SavesDir();
                var name = SaveEnv.CurrentSaveName();
                if (dir == null || name == null) return null;
                var snap = SaveFiles.NewestSnapPath(dir, name);
                if (snap != null && File.Exists(snap)) return snap;
                var main = SaveFiles.MainSavePath(dir, name);
                return File.Exists(main) ? main : null;
            },
            intervalDays: () => IntervalDays.Value,
            keepPerSave: () => KeepPerSave.Value,
            currentDay: () =>
            {
                var name = SaveEnv.CurrentSaveName();
                return name == null ? 0 : SaveEnv.CurrentDay(name);
            },
            gameBuild: "26157732",
            trace: Trace);
    }

    /// <summary>自证文件（硬规则9：不依赖 LogOutput.log）。每行带时间戳。</summary>
    internal static void Trace(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "SaveMgr_trace.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { }
    }
}
