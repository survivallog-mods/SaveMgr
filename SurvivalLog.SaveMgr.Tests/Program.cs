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
        BackupMetaTests.Run(Check);
        BackupStoreTests.Run(Check);
        BackupServiceTests.Run(Check);
        RestoreInjectorTests.Run(Check);
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

internal static class BackupStoreTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "savemgr_t_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Saves"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        return root;
    }

    private static string Md5Of(string file) =>
        System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(file))).ToLower();

    public static void Run(Action<bool, string> check)
    {
        // —— CreateBackup：字节 + meta 落盘，md5=源 ——
        var root = TempRoot();
        try
        {
            var saves = Path.Combine(root, "Saves");
            var data = Path.Combine(root, "data");
            var src = Path.Combine(saves, "Save_x.snap0.bytes");
            File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            var store = new BackupStore(data);
            var dir = store.CreateBackup(src, "Save_x", 49, "auto", "第49天", "26157732");
            var bytesOk = dir != null && File.Exists(Path.Combine(dir, "save.bytes"))
                          && File.ReadAllBytes(Path.Combine(dir, "save.bytes")).Length == 10;
            var meta = dir == null ? null : store.ReadMeta(dir);
            check(bytesOk && meta != null && meta.Day == 49 && meta.Source == "auto"
                  && meta.Md5 == Md5Of(src) && meta.Files.Length == 1,
                "CreateBackup_Writes_Bytes_And_Meta");

            // —— 同秒二次备份：目录名加序号，互不覆盖 ——
            var dir2 = store.CreateBackup(src, "Save_x", 49, "manual", "再来", "26157732");
            check(dir2 != null && dir2 != dir && File.Exists(Path.Combine(dir2, "save.bytes"))
                  && File.Exists(Path.Combine(dir, "save.bytes")),
                "SameSecond_DirNames_Not_Clobbered");

            // —— 列表：孤儿目录（无 meta）与坏 meta 均跳过不抛 ——
            var orphan = Path.Combine(store.BackupDir("Save_x"), "D99_19700101_000000");
            Directory.CreateDirectory(orphan);
            var bad = Path.Combine(store.BackupDir("Save_x"), "D98_19700101_000000");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "meta.json"), "{ not json");
            var list = store.ListBackups("Save_x");
            check(list.Count == 2, "ListBackups_Skips_Orphan");

            // —— 滚动清理：按 Day 升序删超限 ——
            for (int d = 41; d <= 45; d++)
                store.CreateBackup(src, "Save_y", d, "auto", "d" + d, "26157732");
            var removed = store.Prune("Save_y", 3);
            var remain = store.ListBackups("Save_y");
            check(removed == 2 && remain.Count == 3
                  && remain[0].Day == 43 && remain[2].Day == 45,
                "Prune_Keeps_Newest_N");

            // —— 导出 zip ——
            var exports = Path.Combine(root, "exports");
            var zip = store.ExportZip(store.LatestBackupDir("Save_y"), exports);
            check(zip != null && File.Exists(zip) && Path.GetFileName(zip).Contains("D45"),
                "ExportZip_Creates_Named_Zip");

            // —— state 持久化往返 ——
            store.WriteLastAutoDay("Save_y", 45);
            var fresh = new BackupStore(data);
            check(fresh.ReadLastAutoDay("Save_y") == 45 && fresh.ReadLastAutoDay("Save_nope") == null,
                "State_RoundTrip");

            // —— 源文件被独占锁：重试后放弃返回 null，无半截目录 ——
            using (var lockFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var r = store.CreateBackup(src, "Save_z", 50, "auto", "锁", "26157732");
                var zdir = store.BackupDir("Save_z");
                var residue = Directory.Exists(zdir) && Directory.GetDirectories(zdir).Length > 0;
                check(r == null && !residue, "CreateBackup_Null_WhenSourceLocked");
            }

            // —— dest 路径被普通文件占位：null 不抛 ——
            var blocked = Path.Combine(data, "backups", "Save_b");
            Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
            File.WriteAllText(blocked, "i am a file");
            var rb = store.CreateBackup(src, "Save_b", 50, "auto", "挡", "26157732");
            check(rb == null, "CreateBackup_Null_WhenDestBlocked");

            // —— snap 选取：mtime 最新优先；无 snap → null ——
            var s1 = Path.Combine(saves, "Save_w.snap0.bytes");
            var s2 = Path.Combine(saves, "Save_w.snap1.bytes");
            File.WriteAllBytes(s1, new byte[] { 1 });
            File.WriteAllBytes(s2, new byte[] { 2 });
            File.SetLastWriteTime(s1, DateTime.Now.AddMinutes(-5));
            File.SetLastWriteTime(s2, DateTime.Now);
            check(SaveFiles.NewestSnapPath(saves, "Save_w") == s2, "NewestSnap_By_Mtime");
            check(SaveFiles.NewestSnapPath(saves, "Save_none") == null, "NewestSnap_Null_WhenAbsent");
            check(SaveFiles.MainSavePath(saves, "Save_w") == Path.Combine(saves, "Save_w.bytes"),
                "MainSavePath_Format");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

internal static class BackupServiceTests
{
    internal static readonly System.Collections.Generic.List<string> Roots = new();

    private static (BackupService svc, BackupStore store, string saves, string main) Fixture(
        Func<int> interval, Func<int> day, bool autoPersist = true, int? persistMs = null, bool autoEnabled = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "savemgr_svc_" + Guid.NewGuid().ToString("N"));
        Roots.Add(root);
        var saves = Path.Combine(root, "Saves"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(saves); Directory.CreateDirectory(data);
        var main = Path.Combine(saves, "Save_t.bytes");
        File.WriteAllBytes(main, new byte[] { 1, 1 });
        var store = new BackupStore(data);
        var seq = 0;
        var svc = new BackupService(
            store, () => "Save_t", () => main,
            autoPersist ? (Action)(() => { seq++; WriteFresh(main, (byte)seq); }) : () => { },
            interval, () => 30, day, "26157732", _ => { },
            persistTimeout: persistMs.HasValue ? TimeSpan.FromMilliseconds(persistMs.Value) : TimeSpan.FromSeconds(15),
            autoEnabled: () => autoEnabled,
            synchronous: true);
        return (svc, store, saves, main);
    }

    private static void WriteFresh(string main, byte b)
    {
        File.WriteAllBytes(main, new[] { b });
        File.SetLastWriteTime(main, DateTime.Now.AddSeconds(2));   // 越过基线 mtime
    }

    public static void Run(Action<bool, string> check)
    {
        try
        {
            // 结算→强制持久化→主档落盘→Tick 抄档（interval=1 每天）
            var (svc1, st1, _, _) = Fixture(() => 1, () => 10);
            svc1.OnSettlement(10);
            svc1.OnSettlement(10);                       // 同日重复信号：只应有一份
            svc1.Tick(); svc1.Tick();
            check(st1.ListBackups("Save_t").Count == 1, "OnSettlement_Idempotent_SameDay");

            // 间隔 3：day10 备、day12 不备、day13 备
            var (svc2, st2, _, _) = Fixture(() => 3, () => 10);
            svc2.OnSettlement(10); svc2.Tick();
            svc2.OnSettlement(12); svc2.Tick();
            var midCount = st2.ListBackups("Save_t").Count;
            svc2.OnSettlement(13); svc2.Tick();
            var l2 = st2.ListBackups("Save_t");
            check(midCount == 1 && l2.Count == 2 && l2[0].Day == 10 && l2[1].Day == 13, "OnSettlement_Respects_Interval");

            // 间隔 0 = 关闭
            var (svc3, st3, _, _) = Fixture(() => 0, () => 10);
            svc3.OnSettlement(10); svc3.Tick();
            check(st3.ListBackups("Save_t").Count == 0, "OnSettlement_Off_WhenIntervalZero");

            // cfg AutoBackup=false 扼点（hook/兜底两条路都进 OnSettlement，这里统一拦）
            var (svc3c, st3c, _, _) = Fixture(() => 1, () => 10, autoEnabled: false);
            svc3c.OnSettlement(10); svc3c.Tick();
            check(st3c.ListBackups("Save_t").Count == 0, "OnSettlement_Off_WhenAutoBackupDisabled");

            // 强制持久化没落盘（mtime 未变）→ 超时放弃，零备份
            var (svc4, st4, _, _) = Fixture(() => 1, () => 10, autoPersist: false, persistMs: 60);
            svc4.OnSettlement(10);
            System.Threading.Thread.Sleep(120);
            svc4.Tick();
            check(st4.ListBackups("Save_t").Count == 0, "OnSettlement_Skips_WhenPersistNotWritten");

            // 落盘晚于一次 Tick → 后续 Tick 补上（非阻塞等待）
            var (svc5, st5, _, main5) = Fixture(() => 1, () => 10, autoPersist: false);
            svc5.OnSettlement(10);
            svc5.Tick();                                  // 尚未写 → 无备份
            var before = st5.ListBackups("Save_t").Count;
            WriteFresh(main5, 9);
            svc5.Tick();                                  // 落盘 → 补上
            check(before == 0 && st5.ListBackups("Save_t").Count == 1, "Tick_PicksUp_LatePersist");

            // 手动（按钮路径）：先强制持久化→Tick 抄→回调；Source=manual 且不写 lastAutoDay
            var (svc6, st6, _, _) = Fixture(() => 3, () => 49);
            int doneDay = -99; string doneDir = null;
            svc6.OnManualDone += (d, x) => { doneDay = d; doneDir = x; };
            svc6.ManualBackupNow();
            svc6.Tick();
            var m = doneDir == null ? null : st6.ReadMeta(doneDir);
            check(doneDay == 49 && m != null && m.Source == "manual" && m.Day == 49
                  && st6.ReadLastAutoDay("Save_t") == null, "ManualBackupNow_Persists_Then_Copies");

            // 手动失败路径（无持久化落盘）→ day=-1 + 原因
            var (svc7, st7, _, _) = Fixture(() => 3, () => 49, autoPersist: false, persistMs: 60);
            int failDay = 99; string failWhy = null;
            svc7.OnManualDone += (d, x) => { failDay = d; failWhy = x; };
            svc7.ManualBackupNow();
            System.Threading.Thread.Sleep(120);
            svc7.Tick();
            check(failDay == -1 && failWhy != null && st7.ListBackups("Save_t").Count == 0, "ManualBackupNow_Fails_WhenPersistTimesOut");
        }
        finally
        {
            foreach (var r in Roots) { try { Directory.Delete(r, true); } catch { } }
        }
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
