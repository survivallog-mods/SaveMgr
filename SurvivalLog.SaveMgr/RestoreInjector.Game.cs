using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入 interop 部分（主线程调用）。零隐藏副本定案后仅存 InjectFile：
/// 任意完整档字节（结算暂存/主档）→ 新档名 → 身份重写 → AddHistoryEntry（Name=时间戳律）。
/// 恢复界面=官方读档列表本身。</summary>
public static partial class RestoreInjector
{
    /// <summary>注入任意完整档字节文件为游戏原生读档列表的新档（备份目录/主档皆可）。
    /// 仅主菜单（战时动历史数据会把卸载态 HistorySave 写空）。成功 (true, 新档名)。</summary>
    public static (bool Ok, string Reason) InjectFile(string sourceFile)
    {
        string newFile = null;
        try
        {
            if (!SaveEnv.AtMainMenu) return (false, "not-at-main-menu");

            var gsm = SaveEnv.Gsm;
            var savesDir = SaveEnv.SavesDir();
            if (gsm == null) return (false, "gsm-null");
            if (savesDir == null) return (false, "saves-dir-null");
            if (!File.Exists(sourceFile)) return (false, "backup-missing");

            newFile = ResolveFreeName(savesDir, () => gsm.GenerateNewSaveFileName());
            if (newFile == null) return (false, "name-collision");
            if (!IsTimestampName(newFile)) return (false, "name-not-timestamp");
            var entryName = TimestampOf(newFile);

            var dest = Path.Combine(savesDir, newFile);
            File.Copy(sourceFile, dest);

            // 身份重写：不做则注入档与原档同身份（玩它会写坏原档）
            var gs = gsm.PeekGameSave(newFile);
            if (gs == null)
            {
                try { File.Delete(dest); } catch { }
                SaveMgrPlugin.Trace("inject: PeekGameSave 失败，已删除复制的文件（拒绝未重写身份的注入）");
                return (false, "identity-rewrite-failed");
            }
            gs.FileName = newFile;
            gs.Name = entryName;
            var serialized = GameCore.HotUpdate.MemoryPackToolsExpand.SerializeObject(gs);
            GameCore.HotUpdate.TemporaryFile.WritFile(dest, serialized);
            SaveMgrPlugin.Trace("inject: 身份重写 → " + newFile + "（Name=" + entryName + "）");

            gsm.AddHistoryEntry(newFile, entryName);
            SaveMgrPlugin.Trace("inject: AddHistoryEntry done");
            return (true, newFile);
        }
        catch (System.Exception e)
        {
            var dir = SaveEnv.SavesDir();                          // null 时跳过删除（避免 CWD 相对路径误删）
            if (newFile != null && dir != null) { try { File.Delete(Path.Combine(dir, newFile)); } catch { } }
            SaveMgrPlugin.Trace("inject: " + e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>Save_yyyy_MM_dd_HH_mm_ss[.bytes] → yyyy_MM_dd_HH_mm_ss（AddHistoryEntry 的 Name 口径）。</summary>
    private static string TimestampOf(string fileName)
    {
        var n = fileName ?? "";
        if (n.StartsWith("Save_")) n = n["Save_".Length..];
        if (n.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase)) n = n[..^".bytes".Length];
        return n;
    }
}
