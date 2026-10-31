using System;
using System.Text.Json;

namespace SurvivalLog.SaveMgr.Tests;

/// <summary>零依赖自测入口：sdk dotnet run --project SurvivalLog.SaveMgr.Tests -c Release；exit code=失败数。</summary>
internal static class Program
{
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { Console.WriteLine("PASS " + name); return; }
        _fail++; Console.WriteLine("FAIL " + name);
    }

    private static int Main()
    {
        IntervalPolicyTests.Run(Check);
        BackupMetaTests.Run(Check);
        Console.WriteLine(_fail == 0 ? "ALL GREEN" : $"FAILURES={_fail}");
        return _fail;
    }
}

internal static class IntervalPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        // intervalDays<=0 恒 false（关闭自动备份）
        check(!IntervalPolicy.ShouldBackup(10, null, 0), "ShouldBackup_Off_WhenIntervalZero");
        check(!IntervalPolicy.ShouldBackup(10, 7, 0), "ShouldBackup_Off_WhenIntervalZero_EvenWithLast");
        check(!IntervalPolicy.ShouldBackup(10, null, -1), "ShouldBackup_Off_WhenIntervalNegative");

        // 首次：无 lastAutoDay 且 interval>0 → true
        check(IntervalPolicy.ShouldBackup(10, null, 3), "ShouldBackup_First_WhenNoLastDay");

        // 到/超间隔才备：last=7 interval=3 → day9 false、day10 true、day13 true
        check(!IntervalPolicy.ShouldBackup(9, 7, 3), "ShouldBackup_False_BelowInterval");
        check(IntervalPolicy.ShouldBackup(10, 7, 3), "ShouldBackup_True_AtInterval");
        check(IntervalPolicy.ShouldBackup(13, 7, 3), "ShouldBackup_True_BeyondInterval");

        // interval=1 = 每天
        check(IntervalPolicy.ShouldBackup(8, 7, 1), "ShouldBackup_EveryDay_Interval1");

        // 同日幂等
        check(IntervalPolicy.IsSameDayDuplicate(10, 10), "SameDayDuplicate_True");
        check(!IntervalPolicy.IsSameDayDuplicate(11, 10), "SameDayDuplicate_False_DifferentDay");
        check(!IntervalPolicy.IsSameDayDuplicate(10, null), "SameDayDuplicate_False_Null");
    }
}

internal static class BackupMetaTests
{
    public static void Run(Action<bool, string> check)
    {
        var meta = new BackupMeta
        {
            Day = 49, SaveName = "Save_2026_09_19_09_30_35", Source = "auto",
            Label = "第49天", GameBuild = "26157732", CreatedAt = "2026-10-31T20:00:00",
            Md5 = "abc123", Files = new[] { "save.bytes" }
        };
        var json = JsonSerializer.Serialize(meta);
        var back = JsonSerializer.Deserialize<BackupMeta>(json);
        check(back != null && back.Day == 49 && back.SaveName == meta.SaveName && back.Source == "auto"
              && back.Label == "第49天" && back.GameBuild == "26157732" && back.Md5 == "abc123"
              && back.Files != null && back.Files.Length == 1 && back.Files[0] == "save.bytes",
            "BackupMeta_JsonRoundTrip");
    }
}
