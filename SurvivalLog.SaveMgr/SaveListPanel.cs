using System;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>局内存档列表浮层（2026-11-01 作者拍板：读取存档**原地弹列表手选**，不退主菜单）：
/// 「读取存档」→ C# 从 Gsm.GetHistoryList() 组列表数据（同原生 RebuildSaveList 三过滤）推 JS →
/// 暂停菜单之上渲染官方风卡片列表 → 手点某档 → C# 先 Gsm.SaveGame() 存当前 →
/// MainAction.Entrance.EnterBattle(fileName, isLoading:true, ...)（官方主菜单读档同款 API，局内直切）。
/// 失败安全：先存当前档，切档失败玩家无损。</summary>
internal static class SaveListPanel
{
    /// <summary>主菜单期快照（2026-11-01 实机：局内 GetHistoryList 拿不到数据——战时 historyData
    /// 卸载，返回空；菜单期 Ticker 每拍刷新缓存，局内弹列表回落缓存）。</summary>
    private static string _cachedJson = "[]";

    /// <summary>主菜单期刷新缓存（MenuButtons.Tick 每拍调；非菜单期跳过）。</summary>
    internal static void RefreshCache()
    {
        if (!SaveEnv.AtMainMenu) return;
        var live = BuildLive();
        if (!string.IsNullOrEmpty(live)) _cachedJson = live;
    }

    /// <summary>组列表 JSON：实时优先，局内（实时空）回落主菜单期缓存。</summary>
    internal static string BuildListJson()
    {
        var live = BuildLive();
        return string.IsNullOrEmpty(live) ? _cachedJson : (_cachedJson = live);
    }

    private static string BuildLive()
    {
        try
        {
            var gsm = SaveEnv.Gsm;
            if (gsm == null) return null;
            var list = gsm.GetHistoryList();
            if (list == null || list.Count == 0) return null;
            var rows = new System.Collections.Generic.List<string>();
            for (var i = list.Count - 1; i >= 0; i--)          // 倒序=最新在前（同原生显示序）
            {
                var c = list[i];
                if (c == null || string.IsNullOrEmpty(c.FileName)) continue;
                if (!c.HasProgressMetaV2) continue;            // 原生 RebuildSaveList 同款过滤
                if (GameCore.HotUpdate.GameKey.IsLegacySave(c.SaveOrigin)) continue;
                if (!GameCore.HotUpdate.GameSaveManager.IsSaveFilePresent(c.FileName)) continue;
                var row = JsonSerializer.Serialize(new Row
                {
                    f = c.FileName,
                    d = c.Day,
                    p = PlayerName(c.PlayerSelectId),
                    t = c.FileName.StartsWith("Save_") ? c.FileName["Save_".Length..].Replace(".bytes", "") : c.FileName
                });
                rows.Add(row);
            }
            if (rows.Count == 0) return null;
            return "[" + string.Join(",", rows) + "]";
        }
        catch { return null; }                                   // 局内 NRE 等——回落缓存
    }

    private static string PlayerName(int id) => id switch
    {
        1 => "打工仔", 2 => "大学生", 3 => "仓库管理员", 4 => "退伍军人", _ => "角色" + id
    };

    /// <summary>手选某档：先存当前进度 → 官方 EnterBattle 直切（主菜单读档同款 API）。</summary>
    internal static void EnterSave(string fileName)
    {
        try
        {
            SaveMgrPlugin.Trace("列表读档：" + fileName);
            var ma = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.MainAction>.Instance;
            var entrance = ma == null ? null : ma.Entrance;
            if (entrance == null) { SaveMgrPlugin.Trace("列表读档失败：Entrance 未就绪"); return; }
            try { SaveEnv.Gsm?.SaveGame(); } catch (Exception e) { SaveMgrPlugin.Trace("列表读档前存当前失败: " + e.Message); }
            entrance.EnterBattle(fileName, true, false);       // isLoading=true 读档，官方同参
        }
        catch (Exception e) { SaveMgrPlugin.Trace("enterSave: " + e.Message); }
    }

    private sealed class Row
    {
        public string f { get; set; }
        public int d { get; set; }
        public string p { get; set; }
        public string t { get; set; }
    }
}
