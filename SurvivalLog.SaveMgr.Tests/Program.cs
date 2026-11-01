using System;
using System.IO;
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
        SavepointTests.Run(Check);
        RestoreInjectorTests.Run(Check);
        PendingRegTests.Run(Check);
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

internal static class SavepointTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "savemgr_sp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var saves = Path.Combine(root, "Saves"); var data = Path.Combine(root, "data");
            Directory.CreateDirectory(saves); Directory.CreateDirectory(data);
            var main = Path.Combine(saves, "Save_t.bytes");
            File.WriteAllBytes(main, new byte[] { 1, 1 });
            var store = new StateStore(data);
            var staged = new System.Collections.Generic.List<string>();
            var svc = new SavepointService(store, () => "Save_t", () => main,
                () => 1, () => 10, saves,
                f => staged.Add(f), _ => { },
                waitTimeout: TimeSpan.FromSeconds(15),
                persist: () => { });

            // 结算只武装；主档未写 → 无新档
            svc.OnSettlement(10);
            svc.Tick();
            check(staged.Count == 0, "Settlement_Arms_Without_Staging");

            // 游戏写盘（mtime 越基线）→ Saves/ 新档 + lastAutoDay + 回调
            File.WriteAllBytes(main, new byte[] { 2, 2, 2 });
            File.SetLastWriteTime(main, DateTime.Now.AddSeconds(2));
            svc.Tick();
            check(staged.Count == 1 && staged[0].StartsWith("Save_") && staged[0].EndsWith(".bytes")
                  && File.Exists(Path.Combine(saves, staged[0]))
                  && store.ReadLastAutoDay("Save_t") == 10, "NewSave_On_MainWrite");

            // 同日重复结算：幂等
            svc.OnSettlement(10); svc.Tick();
            check(staged.Count == 1, "SameDay_Idempotent");

            // 间隔 3：day11/12 不备，day13 备
            var staged2 = new System.Collections.Generic.List<string>();
            var svc2 = new SavepointService(store, () => "Save_t", () => main,
                () => 3, () => 10, saves, f => staged2.Add(f), _ => { });
            foreach (var day in new[] { 11, 12, 13 })
            {
                svc2.OnSettlement(day);
                File.WriteAllBytes(main, new byte[] { (byte)day });
                File.SetLastWriteTime(main, DateTime.Now.AddSeconds(2));
                svc2.Tick();
            }
            check(staged2.Count == 1, "Interval_Respected");

            // 普通档不写盘 → 超时跳过零暂存（auto 无重试）
            var staged3 = new System.Collections.Generic.List<string>();
            var svc3 = new SavepointService(store, () => "Save_t", () => main,
                () => 1, () => 10, saves, f => staged3.Add(f), _ => { },
                waitTimeout: TimeSpan.FromMilliseconds(50));
            svc3.OnSettlement(20);
            System.Threading.Thread.Sleep(100);
            svc3.Tick();
            check(staged3.Count == 0, "Skip_When_No_Persist");

            // 手动：SaveGame 前两轮被静默拒 → 第 3 轮落盘成功（重试机制）
            var staged4 = new System.Collections.Generic.List<string>();
            var persistCalls = 0;
            var svc4 = new SavepointService(store, () => "Save_t", () => main,
                () => 1, () => 10, saves, f => staged4.Add(f), _ => { },
                waitTimeout: TimeSpan.FromMilliseconds(50),
                persist: () => { if (++persistCalls >= 3) { File.WriteAllBytes(main, new byte[] { 7 }); File.SetLastWriteTime(main, DateTime.Now.AddSeconds(2)); } });
            svc4.ManualSaveNow();
            for (var k = 0; k < 8; k++) { System.Threading.Thread.Sleep(60); svc4.Tick(); }
            check(staged4.Count == 1 && persistCalls == 3, "Manual_Retries_Until_Persist");

            // StateStore 往返
            store.WriteLastAutoDay("Save_x", 7);
            check(new StateStore(data).ReadLastAutoDay("Save_x") == 7, "State_RoundTrip");

            // ReadStable mtime 栅栏
            var src = Path.Combine(root, "w.bytes");
            File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
            var fence = false;
            try { BackupStore.ReadStable(src, () => { File.WriteAllBytes(src, new byte[] { 9 }); File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddSeconds(3)); }); }
            catch (System.IO.IOException) { fence = true; }
            check(fence, "ReadStable_Fence_Rejects_MidWriteChange");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

internal static class RestoreInjectorTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "savemgr_ri_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Save_a.bytes"), "x");
            check(RestoreInjector.PathIsFree(root, "Save_b"), "PathIsFree_True_New");
            check(!RestoreInjector.PathIsFree(root, "Save_a"), "PathIsFree_False_Existing");
            check(!RestoreInjector.PathIsFree(root, "Save_a.bytes"), "PathIsFree_False_Existing_WithExt");
            check(RestoreInjector.PathIsFree(root, "Save_b.bytes"), "PathIsFree_True_New_WithExt");

            // 名字生成器恒返回已占名 → 重试 3 次后失败，且目录零写入
            var made = RestoreInjector.ResolveFreeName(root, () => "Save_a", maxTries: 3);
            var wrote = Directory.GetFiles(root, "*.bytes").Length;
            check(made == null && wrote == 1, "RetryUntilFree_Fails_After3");

            // 第二次返回新名 → 成功
            var n = 0;
            var ok = RestoreInjector.ResolveFreeName(root, () => n++ == 0 ? "Save_a" : "Save_new", maxTries: 3);
            check(ok == "Save_new", "RetryUntilFree_SecondTry_Wins");

            // 孤儿 .bak 占用也算不空闲（会被 LoadWithBackupFallback 配对旧字节）
            File.WriteAllText(Path.Combine(root, "Save_c.bytes.bak"), "old");
            check(!RestoreInjector.PathIsFree(root, "Save_c"), "PathIsFree_False_WhenOnlyBakExists");

            // Name 时间戳律校验
            check(RestoreInjector.IsTimestampName("Save_2026_10_31_21_15_55.bytes"), "IsTimestampName_True_Valid");
            check(RestoreInjector.IsTimestampName("Save_2026_10_31_21_15_55"), "IsTimestampName_True_NoExt");
            check(!RestoreInjector.IsTimestampName("Save_2026_10_31_21_15_55_2.bytes"), "IsTimestampName_False_Suffixed");
            check(!RestoreInjector.IsTimestampName("探针注入"), "IsTimestampName_False_NotTimestamp");

            // ReadStable mtime 栅栏：读取中途文件被改写+mtime 翻新 → 抛 IOException
            var src = Path.Combine(root, "writable.bytes");
            File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
            var fenceThrown = false;
            try
            {
                BackupStore.ReadStable(src, () =>
                {
                    File.WriteAllBytes(src, new byte[] { 9, 9, 9, 9 });
                    File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddSeconds(3));
                });
            }
            catch (System.IO.IOException) { fenceThrown = true; }
            check(fenceThrown, "ReadStable_Fence_Rejects_MidWriteChange");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

internal static class PendingRegTests
{
    public static void Run(Action<bool, string> check)
    {
        // 往返：file+source 保留（无尽类型回填源随队列落盘）
        var a = PendingReg.Serialize(new[] { new PendingReg("Save_1.bytes", "主档.bytes") });
        var b = PendingReg.Deserialize(a);
        check(b.Count == 1 && b[0].File == "Save_1.bytes" && b[0].Source == "主档.bytes",
            "PendingReg_RoundTrip_KeepsFileAndSource");

        // 旧格式兼容：纯 string[]（1.0.0 pending.json）→ source=null（注册侧回落当前档名）
        var legacy = PendingReg.Deserialize("[\"Save_a.bytes\",\"Save_b.bytes\"]");
        check(legacy.Count == 2 && legacy[0].File == "Save_a.bytes" && legacy[0].Source == null
              && legacy[1].File == "Save_b.bytes" && legacy[1].Source == null,
            "PendingReg_LegacyStringArray_SourceNull");

        // 新旧混排（升级瞬间崩溃的最坏现场）
        var mixed = PendingReg.Deserialize("[\"Save_a.bytes\",{\"f\":\"Save_b.bytes\",\"s\":\"x.bytes\"}]");
        check(mixed.Count == 2 && mixed[0].Source == null && mixed[1].File == "Save_b.bytes" && mixed[1].Source == "x.bytes",
            "PendingReg_MixedFormats_Tolerated");

        // source=null 序列化省略 s，往返仍 null
        var c = PendingReg.Serialize(new[] { new PendingReg("Save_2.bytes", null) });
        check(!c.Contains("\"s\""), "PendingReg_NullSource_OmittedInJson");
        check(PendingReg.Deserialize(c)[0].Source == null, "PendingReg_NullSource_RoundTrip");

        // 空队列 / null 队列
        check(PendingReg.Deserialize(PendingReg.Serialize(new PendingReg[0])).Count == 0, "PendingReg_EmptyRoundTrip");
        check(PendingReg.Deserialize(PendingReg.Serialize(null)).Count == 0, "PendingReg_NullList_SerializesEmpty");

        // 坏 JSON / 空 / 非数组根 / 缺 f 的对象 → 空表或跳过，不抛
        check(PendingReg.Deserialize(null).Count == 0, "PendingReg_NullJson_Empty");
        check(PendingReg.Deserialize("").Count == 0, "PendingReg_EmptyJson_Empty");
        check(PendingReg.Deserialize("garbage{").Count == 0, "PendingReg_BadJson_Empty");
        check(PendingReg.Deserialize("{\"f\":\"x\"}").Count == 0, "PendingReg_NonArrayRoot_Empty");
        check(PendingReg.Deserialize("[{\"s\":\"only_source\"}]").Count == 0, "PendingReg_ObjectWithoutFile_Skipped");
    }
}
