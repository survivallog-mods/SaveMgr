using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入式一键回档（interop 部分；调用须在主线程——Ticker Update 内直调或经 SaveMgrRuntime）。
/// 时机守卫：仅主菜单（AtMainMenu）。
/// 流程（2026-10-31 实机两轮迭代定案）：
///   ① 备份字节复制成新档名（游戏生成器，带 .bytes）
///   ② **身份重写**：注入档内部 FileName 指向原档（实锤：玩注入档会把写盘路由到原档文件！）——
///      用游戏自己的 PeekGameSave 反序列化 → 改 GameSaveData.FileName/Name → 游戏序列器
///      SerializeObject + TemporaryFile.WritFile 写回。零私有格式。
///   ③ AddHistoryEntry(新名, 时间戳串)——Name 必须可解析时间戳（非时间串→列表行被丢弃，实锤）。
/// 安全：②若 Peek 失败（身份无法重写）→ 删除复制的文件并拒绝交付（未重写的注入档是原档地雷）。</summary>
public static partial class RestoreInjector
{
    /// <summary>注入备份目录为游戏原生读档列表的新档。成功 (true, 新档名)；失败 (false, 原因)。</summary>
    public static (bool Ok, string Reason) Inject(string backupDir)
    {
        string newFile = null;
        try
        {
            if (!SaveEnv.AtMainMenu) return (false, "not-at-main-menu");

            var gsm = SaveEnv.Gsm;
            var savesDir = SaveEnv.SavesDir();
            var bytesFile = Path.Combine(backupDir ?? "", "save.bytes");
            if (gsm == null) return (false, "gsm-null");
            if (savesDir == null) return (false, "saves-dir-null");
            if (!File.Exists(bytesFile)) return (false, "backup-missing");

            newFile = ResolveFreeName(savesDir, () => gsm.GenerateNewSaveFileName());
            if (newFile == null) return (false, "name-collision");
            if (!IsTimestampName(newFile)) return (false, "name-not-timestamp");
            var entryName = TimestampOf(newFile);

            var dest = Path.Combine(savesDir, newFile);
            File.Copy(bytesFile, dest);

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

    /// <summary>F10 入口：当前档最新备份 → 注入。</summary>
    public static (bool Ok, string Reason) InjectLatest()
    {
        try
        {
            var name = SaveEnv.CurrentSaveBase();            // 备份目录按基名存（BackupService.saveName 口径）
            if (name == null) return (false, "no-current-save");
            var dataRoot = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins-data", "SaveMgr");
            var store = new BackupStore(dataRoot);
            var dir = store.LatestBackupDir(name);
            if (dir == null) return (false, "no-backup");
            var meta = store.ReadMeta(dir);
            if (meta == null) return (false, "bad-meta");
            var r = Inject(dir);
            if (r.Ok) SaveMgrPlugin.Trace($"F10: {name} 第{meta.Day}天备份 → 新档 {r.Reason}（读档列表顶部）");
            return r;
        }
        catch (System.Exception e) { return (false, e.Message); }
    }

    /// <summary>Save_yyyy_MM_dd_HH_mm_ss[.bytes] → yyyy_MM_dd_HH_mm_ss（AddHistoryEntry 的 Name 口径）。</summary>
    private static string TimestampOf(string fileName)
    {
        var n = fileName ?? "";
        if (n.StartsWith("Save_")) n = n["Save_".Length..];
        if (n.EndsWith(".bytes")) n = n[..^".bytes".Length];
        return n;
    }
}
