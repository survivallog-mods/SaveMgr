using System;
using System.Reflection;
using HarmonyLib;

namespace SurvivalLog.SaveMgr;

/// <summary>结算时机信号（Task 1 定案锚点）：
/// postfix GameSaveManager.CaptureDaySnapshot(int day)（公开常规方法，反汇编 0x28D2640，
/// 自然日翻转全模式无门控；写盘异步未等待 → 本 postfix 只投递信号，字节源新鲜度由
/// BackupService.autoSource（≤120s）把关）。TargetMethod 参数类型钉死（FallBlocker 同款，防重载漂移）。</summary>
[HarmonyPatch]
internal static class Patch_CaptureDaySnapshot
{
    /// <summary>hook 最近触发时刻（DayWatcher 抑制双触发用）。</summary>
    internal static DateTime LastFire = DateTime.MinValue;

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameCore.HotUpdate.GameSaveManager), "CaptureDaySnapshot",
            new[] { typeof(int) });

    private static void Postfix(int day)
    {
        try
        {
            LastFire = DateTime.Now;
            SaveMgrPlugin.Trace($"hook: CaptureDaySnapshot(day={day})");
            var svc = SaveMgrPlugin.Service;
            if (svc == null) return;
            if (SaveMgrPlugin.AutoBackup != null && !SaveMgrPlugin.AutoBackup.Value) return;
            SaveMgrRuntime.Enqueue(() => svc.OnSettlement(day));
        }
        catch (Exception e) { SaveMgrPlugin.Trace("hook: " + e.Message); }
    }
}

/// <summary>兜底日翻检测：轮询 HistoryChild.Day 增量（结算时 SaveGameFileCore 会同步历史 meta，
/// 实证 docs/2026-10-31-锚点实证.md）。hook 60s 内触发过则让路；OnSettlement 本身同日幂等双保险。</summary>
internal static class DayWatcher
{
    private static string _watchName;
    private static int _watchDay = -1;

    internal static void Step()
    {
        try
        {
            var name = SaveEnv.CurrentSaveName();
            if (name == null) { _watchName = null; _watchDay = -1; return; }
            var day = SaveEnv.CurrentDay(name);
            if (name != _watchName)
            {
                _watchName = name;
                _watchDay = day;
                return;
            }
            if (day <= 0 || _watchDay <= 0 || day <= _watchDay) { if (day > 0) _watchDay = day; return; }

            var advanced = day;
            _watchDay = day;
            if ((DateTime.Now - Patch_CaptureDaySnapshot.LastFire).TotalSeconds < 60)
            {
                SaveMgrPlugin.Trace($"watcher: 第{advanced}天（hook 已处理，让路）");
                return;
            }
            SaveMgrPlugin.Trace($"watcher: 检测日翻 第{advanced}天（兜底触发）");
            var svc = SaveMgrPlugin.Service;
            if (svc != null) SaveMgrRuntime.Enqueue(() => svc.OnSettlement(advanced));
        }
        catch (Exception e) { SaveMgrPlugin.Trace("watcher: " + e.Message); }
    }
}
