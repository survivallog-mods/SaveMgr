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
            var store = new BackupStore(saves, data);
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
            var fresh = new BackupStore(saves, data);
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

    private static (BackupService svc, BackupStore store) Fixture(Func<int> interval, Func<int> day,
        Func<string> autoSource = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "savemgr_svc_" + Guid.NewGuid().ToString("N"));
        Roots.Add(root);
        var saves = Path.Combine(root, "Saves"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(saves); Directory.CreateDirectory(data);
        var snap = Path.Combine(saves, "Save_t.snap0.bytes");
        var main = Path.Combine(saves, "Save_t.bytes");
        File.WriteAllBytes(snap, new byte[] { 1, 1 });
        File.WriteAllBytes(main, new byte[] { 2, 2, 2 });
        var store = new BackupStore(saves, data);
        var svc = new BackupService(
            store, () => "Save_t", autoSource ?? (() => snap),
            () => File.Exists(snap) ? snap : main,
            interval, () => 30, day, "26157732", _ => { });
        return (svc, store);
    }

    public static void Run(Action<bool, string> check)
    {
        try
        {
            // 同日重复结算信号：只备一次（幂等）
            var (svc1, st1) = Fixture(() => 1, () => 10);
            svc1.OnSettlement(10);
            svc1.OnSettlement(10);
            check(st1.ListBackups("Save_t").Count == 1, "OnSettlement_Idempotent_SameDay");

            // 间隔 3：day10 备、day12 不备、day13 备
            var (svc2, st2) = Fixture(() => 3, () => 10);
            svc2.OnSettlement(10); svc2.OnSettlement(12); svc2.OnSettlement(13);
            var l2 = st2.ListBackups("Save_t");
            check(l2.Count == 2 && l2[0].Day == 10 && l2[1].Day == 13, "OnSettlement_Respects_Interval");

            // 间隔 0 = 关闭
            var (svc3, st3) = Fixture(() => 0, () => 10);
            svc3.OnSettlement(10); svc3.OnSettlement(11);
            check(st3.ListBackups("Save_t").Count == 0, "OnSettlement_Off_WhenIntervalZero");

            // 自动路径无新鲜 snap（getter 返回 null）：跳过本次，不回退主档（防旧档标新天）
            var (svc3b, st3b) = Fixture(() => 1, () => 10, () => null);
            svc3b.OnSettlement(10);
            check(st3b.ListBackups("Save_t").Count == 0, "OnSettlement_Skips_WhenNoFreshSnap");

            // 手动：无 snap 时回退主档，Source=manual
            var (svc4, st4) = Fixture(() => 3, () => 49);
            var dir = svc4.ManualBackup();
            var m = dir == null ? null : st4.ReadMeta(dir);
            check(m != null && m.Source == "manual" && m.Day == 49, "ManualBackup_NoSnap_FallsBackToMain");
        }
        finally
        {
            foreach (var r in Roots) { try { Directory.Delete(r, true); } catch { } }
        }
    }
}
