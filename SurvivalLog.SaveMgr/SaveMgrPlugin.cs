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
    internal static ConfigEntry<int> KeepPerSave;

    internal static BackupService Service;

    public override void Load()
    {
        Log = base.Log;
        // 2026-10-31 作者拍板极简面：全部配置只 3 项功能键（+ModCenter 侧栏启停位的 General.Enabled）。
        // 热键 F9/F10、兜底日翻、回档开关、cmd 通道一律硬编码不再暴露（README 记行为即可）。
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=停全部功能（已落备份保留）");
        AutoBackup = Config.Bind("AutoBackup", "AutoBackup", true, "每日结算自动备份");
        IntervalDays = Config.Bind("AutoBackup", "IntervalDays", 5, "间隔天数：0=关闭，1=每天，N=每 N 天");
        KeepPerSave = Config.Bind("Manual", "KeepPerSave", 30,
            new ConfigDescription("每档备份保留上限（按天数升序删最老，含手动）", new AcceptableValueRange<int>(1, 999)));

        Service = BuildService();
        // 游戏内实体按钮层（主菜单「一键回档」/局内 ESC「立即存档」）由 Ticker 驱动，见 MenuButtons
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
