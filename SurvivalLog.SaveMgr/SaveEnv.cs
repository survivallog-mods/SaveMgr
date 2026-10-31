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

    public static GameCore.HotUpdate.GameSaveManager Gsm
        => GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.Instance;

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

    /// <summary>最近游玩的档名（GetLastPlayFileName，无扩展名）；空/异常 → null。</summary>
    public static string CurrentSaveName()
    {
        try
        {
            var gsm = Gsm;
            var name = gsm == null ? null : gsm.GetLastPlayFileName();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception e) { SaveMgrPlugin.Trace("CurrentSaveName: " + e.Message); return null; }
    }

    /// <summary>该档在历史列表里的 Day（HistoryChild.Day 匹配 FileName）；无匹配/异常 → -1。</summary>
    public static int CurrentDay(string saveName)
    {
        try
        {
            var gsm = Gsm;
            if (gsm == null || string.IsNullOrEmpty(saveName)) return -1;
            var list = gsm.GetHistoryList();
            if (list == null) return -1;
            foreach (var child in list)
                if (child != null && child.FileName == saveName) return child.Day;
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
