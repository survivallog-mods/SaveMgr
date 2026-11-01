using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入 interop 部分（主线程调用）。零隐藏副本定案后仅存 InjectFile：
/// 任意完整档字节（结算暂存/主档）→ 新档名 → 身份重写 → AddHistoryEntry（Name=时间戳律）。
/// 恢复界面=官方读档列表本身。Register（存档点补注册）含无尽类型回填，见 CopyEndlessMeta。</summary>
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

    /// <summary>把已落盘的新档补注册进官方读档列表（主菜单期；pending 泵调用）。
    /// sourceSaveName=原档名（无尽类型回填源；null 回落当前档名）。已在列表/文件缺失 → false 跳过；
    /// 注册成功 → true（类型回填结果另记 trace，不影响注册成败）。</summary>
    public static bool Register(string fileName, string sourceSaveName = null)
    {
        try
        {
            var gsm = SaveEnv.Gsm;
            var savesDir = SaveEnv.SavesDir();
            if (gsm == null || savesDir == null) return false;
            var f = fileName ?? "";
            if (!f.EndsWith(".bytes")) f += ".bytes";
            if (!File.Exists(Path.Combine(savesDir, f))) return false;
            var list = gsm.GetHistoryList();
            if (list != null)
                foreach (var c in list)
                    if (c != null && c.FileName == f) return false;      // 已注册
            if (!IsTimestampName(f)) return false;
            gsm.AddHistoryEntry(f, TimestampOf(f));
            CopyEndlessMeta(gsm, f, sourceSaveName);
            return true;
        }
        catch (System.Exception e) { SaveMgrPlugin.Trace("register: " + e.Message); return false; }
    }

    /// <summary>新档条目的无尽类型回填（纯净无尽/剧情无尽标签）：从原档 HistoryChild 复制无尽块
    /// IsPureEndless/EndlessStartDay/HasEndlessMeta/IsStoryEndless/StoryEndlessOriginEnding。
    /// 依据（GameAssembly 反汇编 2026-11-01）：AddHistoryEntry 内的 AssignChildMetaFromGameSave
    /// 只补进度 meta，无尽块游戏原生仅在 ForkCore 建条目时直写、常规存盘 SyncHistoryChildMeta 也不回填
    /// ——不补则列表永不显类型标签。写法=游戏活对象公共属性（ForkCore 同款），随后
    /// SetLastPlayFileName(原值) 幂等触发 HistorySave.bytes 重写落盘（该 API 无同名早退，反汇编实证）。
    /// 普通档（原档条目无无尽块）零动作；原档条目缺失 → 跳过（退化为旧表现，无害）。</summary>
    private static void CopyEndlessMeta(GameCore.HotUpdate.GameSaveManager gsm,
        string newFileName, string sourceSaveName)
    {
        try
        {
            var source = string.IsNullOrEmpty(sourceSaveName) ? SaveEnv.CurrentSaveName() : sourceSaveName;
            if (string.IsNullOrEmpty(source)) return;
            GameCore.HotUpdate.HistoryChild src = null, dst = null;
            var list = gsm.GetHistoryList();
            if (list == null) return;
            foreach (var c in list)
            {
                if (c == null || string.IsNullOrEmpty(c.FileName)) continue;
                if (SameBase(c.FileName, source)) src = c;
                else if (SameBase(c.FileName, newFileName)) dst = c;
            }
            if (dst == null || src == null || ReferenceEquals(src, dst) || !src.HasEndlessMeta) return;
            dst.IsPureEndless = src.IsPureEndless;
            dst.EndlessStartDay = src.EndlessStartDay;
            dst.HasEndlessMeta = true;
            dst.IsStoryEndless = src.IsStoryEndless;
            dst.StoryEndlessOriginEnding = src.StoryEndlessOriginEnding;
            var last = gsm.GetLastPlayFileName();                       // 同值重写=纯冲刷，不动"继续游戏"指向
            if (!string.IsNullOrEmpty(last)) gsm.SetLastPlayFileName(last);
            SaveMgrPlugin.Trace("meta: 无尽类型回填 " + newFileName + "（pure=" + src.IsPureEndless +
                " story=" + src.IsStoryEndless + " start=" + src.EndlessStartDay + "）← " + src.FileName);
        }
        catch (System.Exception e) { SaveMgrPlugin.Trace("meta: " + e.Message); }
    }

    /// <summary>档名基名比对（去 .bytes、忽略大小写）——全链本就带后缀，双保险。</summary>
    private static bool SameBase(string a, string b)
    {
        if (a.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase)) a = a[..^".bytes".Length];
        if (!string.IsNullOrEmpty(b) && b.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase)) b = b[..^".bytes".Length];
        return string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>身份重写（战时安全：纯文件+游戏序列器，无历史依赖）。fileName=Saves/ 内已有文件。
    /// 不重写的档=身份指向原档的地雷（2026-11-01 01:22/01:26 实锤）。</summary>
    public static bool RewriteIdentity(string fileName)
    {
        try
        {
            var gsm = SaveEnv.Gsm;
            var savesDir = SaveEnv.SavesDir();
            if (gsm == null || savesDir == null || string.IsNullOrEmpty(fileName)) return false;
            var f = fileName.EndsWith(".bytes") ? fileName : fileName + ".bytes";
            var path = Path.Combine(savesDir, f);
            if (!File.Exists(path) || !IsTimestampName(f)) return false;
            var gs = gsm.PeekGameSave(f);
            if (gs == null) return false;
            gs.FileName = f;
            gs.Name = TimestampOf(f);
            var serialized = GameCore.HotUpdate.MemoryPackToolsExpand.SerializeObject(gs);
            GameCore.HotUpdate.TemporaryFile.WritFile(path, serialized);
            return true;
        }
        catch (System.Exception e) { SaveMgrPlugin.Trace("rewrite: " + e.Message); return false; }
    }
}
