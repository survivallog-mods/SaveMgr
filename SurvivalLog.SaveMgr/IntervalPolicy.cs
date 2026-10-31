namespace SurvivalLog.SaveMgr;

/// <summary>自动备份间隔策略（纯逻辑，无游戏依赖）。
/// 语义：intervalDays&lt;=0 关闭恒 false；无 lastAutoDay 且开启 → true（首次即备）；
/// 否则 currentDay-lastAutoDay &gt;= intervalDays。同日幂等由 IsSameDayDuplicate 单独判。</summary>
public static class IntervalPolicy
{
    public static bool ShouldBackup(int currentDay, int? lastAutoDay, int intervalDays)
    {
        if (intervalDays <= 0) return false;
        if (lastAutoDay == null) return true;
        return currentDay - lastAutoDay.Value >= intervalDays;
    }

    public static bool IsSameDayDuplicate(int currentDay, int? lastAutoDay)
        => lastAutoDay != null && currentDay == lastAutoDay.Value;
}
