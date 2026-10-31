using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入式一键回档（interop 部分；调用须在主线程——Ticker Update 内直调或经 SaveMgrRuntime）。
/// 时机守卫：仅主菜单（AtMainMenu）。行为验证对齐 Task 2 探针实录 + Task 9 实机。</summary>
public static partial class RestoreInjector
{
    /// <summary>注入备份目录为游戏原生读档列表的新档。成功 (true, 新档名)；失败 (false, 原因)。</summary>
    public static (bool Ok, string Reason) Inject(string backupDir, string label)
    {
        try
        {
            if (SaveMgrPlugin.RestoreEnabled != null && !SaveMgrPlugin.RestoreEnabled.Value)
                return (false, "restore-disabled");
            if (!SaveEnv.AtMainMenu) return (false, "not-at-main-menu");

            var gsm = SaveEnv.Gsm;
            var savesDir = SaveEnv.SavesDir();
            var bytesFile = Path.Combine(backupDir ?? "", "save.bytes");
            if (gsm == null) return (false, "gsm-null");
            if (savesDir == null) return (false, "saves-dir-null");
            if (!File.Exists(bytesFile)) return (false, "backup-missing");

            var newName = ResolveFreeName(savesDir, () => gsm.GenerateNewSaveFileName());
            if (newName == null) return (false, "name-collision");

            File.Copy(bytesFile, Path.Combine(savesDir, newName + ".bytes"));
            SaveMgrPlugin.Trace("inject: bytes → " + newName);

            gsm.AddHistoryEntry(newName, label);
            SaveMgrPlugin.Trace("inject: AddHistoryEntry(" + newName + ", " + label + ") done");
            return (true, newName);
        }
        catch (System.Exception e)
        {
            SaveMgrPlugin.Trace("inject: " + e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>F10 入口：当前档最新备份 → 注入（label=回档·第N天）。</summary>
    public static (bool Ok, string Reason) InjectLatest()
    {
        try
        {
            var name = SaveEnv.CurrentSaveName();
            if (name == null) return (false, "no-current-save");
            var dataRoot = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins-data", "SaveMgr");
            var store = new BackupStore(SaveEnv.SavesDir(), dataRoot);
            var dir = store.LatestBackupDir(name);
            if (dir == null) return (false, "no-backup");
            var meta = store.ReadMeta(dir);
            if (meta == null) return (false, "bad-meta");
            var r = Inject(dir, "回档·第" + meta.Day + "天");
            if (r.Ok) SaveMgrPlugin.Trace($"F10: {name} 第{meta.Day}天备份 → 新档 {r.Reason}（读档列表顶部）");
            return r;
        }
        catch (System.Exception e) { return (false, e.Message); }
    }
}
