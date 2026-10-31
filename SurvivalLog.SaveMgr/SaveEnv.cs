using System;
using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>interop 薄层：游戏状态只读访问（全部 try/catch + Trace，异常返回安全默认值）。
/// 路径来源：游戏自己的 TemporaryFile.GetRootPath()（dump.cs:109067 附近，public static，
/// 实证 2026-10-31）——零路径猜测；不可用时回退 Application.persistentDataPath + "/Saves"。
/// 行为验证归实机（计划 Task 9）。</summary>
public static class SaveEnv
{
    private static string _savesDirCache;
    private static bool _historyEnsured;

    public static GameCore.HotUpdate.GameSaveManager Gsm
        => GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.Instance;

    /// <summary>历史数据懒加载守卫：主菜单直接调 GetLastPlayFileName/GetHistoryList 会 NRE
    /// （2026-10-31 实机实证，游戏源 GameSaveManager.cs:421）；原生 RebuildSaveList 每次都先调
    /// EnsureHistoryV2Migrated（幂等）——照抄该防御。</summary>
    private static void EnsureHistoryReady()
    {
        if (_historyEnsured) return;
        try { Gsm?.EnsureHistoryV2Migrated(); _historyEnsured = true; }
        catch (Exception e) { SaveMgrPlugin.Trace("EnsureHistoryReady: " + e.Message); }
    }

    /// <summary>存档根目录（尾带分隔符的完整路径）。失败 → null。</summary>
    public static string SavesDir()
    {
        if (_savesDirCache != null) return _savesDirCache;
        try
        {
            var root = GameCore.HotUpdate.TemporaryFile.GetRootPath();
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                return _savesDirCache = root;
        }
        catch (Exception e) { SaveMgrPlugin.Trace("SavesDir(TemporaryFile): " + e.Message); }
        try
        {
            var fallback = UnityEngine.Application.persistentDataPath + Path.DirectorySeparatorChar + "Saves";
            if (Directory.Exists(fallback)) return _savesDirCache = fallback;
        }
        catch (Exception e) { SaveMgrPlugin.Trace("SavesDir(persistentDataPath): " + e.Message); }
        return null;
    }

    private static string _cachedName;

    /// <summary>最近游玩的档名，游戏原生约定**带 .bytes 后缀**（2026-10-31 实机实证：
    /// GetLastPlayFileName/HistoryChild.FileName/AddHistoryEntry 的 fileName 全链带后缀）。
    /// 局内 GetLastPlayFileName 会 NRE（战时 historyData 卸载，游戏源:421）——菜单期成功值缓存，
    /// 局内回落缓存（进局必经菜单，缓存必已填充）。</summary>
    public static string CurrentSaveName()
    {
        try
        {
            var gsm = Gsm;
            if (gsm == null) return _cachedName;
            EnsureHistoryReady();
            var name = gsm.GetLastPlayFileName();
            if (!string.IsNullOrEmpty(name)) _cachedName = name;
            return string.IsNullOrEmpty(name) ? _cachedName : name;
        }
        catch { return _cachedName; }
    }

    /// <summary>当前档基名（去掉 .bytes），供本地路径运算（snap glob / 主档路径 / 备份目录名）。</summary>
    public static string CurrentSaveBase()
    {
        var n = CurrentSaveName();
        if (n == null) return null;
        return n.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase) ? n[..^".bytes".Length] : n;
    }

    /// <summary>该档在历史列表里的 Day（按去后缀基名比对，兼容两种存储形态）；无匹配/异常 → -1。</summary>
    public static int CurrentDay(string saveName)
    {
        try
        {
            var gsm = Gsm;
            if (gsm == null || string.IsNullOrEmpty(saveName)) return -1;
            EnsureHistoryReady();
            var baseName = saveName.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase)
                ? saveName[..^".bytes".Length] : saveName;
            var list = gsm.GetHistoryList();
            if (list == null) return -1;
            foreach (var child in list)
            {
                if (child == null || string.IsNullOrEmpty(child.FileName)) continue;
                var f = child.FileName.EndsWith(".bytes", System.StringComparison.OrdinalIgnoreCase)
                    ? child.FileName[..^".bytes".Length] : child.FileName;
                if (f == baseName) return child.Day;
            }
            return -1;
        }
        catch (Exception e) { SaveMgrPlugin.Trace("CurrentDay: " + e.Message); return -1; }
    }

    /// <summary>主菜单判定：Gsm 就绪且不在局内（IsInBattle）。</summary>
    public static bool AtMainMenu
    {
        get
        {
            try { var gsm = Gsm; return gsm != null && !gsm.IsInBattle; }
            catch { return false; }
        }
    }
}
