using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace SurvivalLog.SaveMgr;

/// <summary>备份仓库（纯文件系统，零游戏依赖）。
/// 布局：&lt;dataRoot&gt;/backups/&lt;saveName&gt;/D&lt;day&gt;_&lt;ts&gt;[_n]/{save.bytes,meta.json}；state.json 存各档 lastAutoDay。
/// 安全性：源双读 md5 稳定才收；先读后建目录（失败零残留）；落盘一律 tmp+改名；IO 异常重试 ≤3×500ms 后返回 null，不抛穿。</summary>
public sealed class BackupStore
{
    private const string SaveFileName = "save.bytes";
    private const string MetaFileName = "meta.json";
    private const string StateFileName = "state.json";

    private readonly string _savesDir;
    private readonly string _dataRoot;

    public BackupStore(string savesDir, string dataRoot)
    {
        _savesDir = savesDir;
        _dataRoot = dataRoot;
    }

    public string BackupDir(string saveName) => Path.Combine(_dataRoot, "backups", saveName);

    /// <summary>字节级备份。成功返回备份目录；任何失败（源锁/读不稳/dest 受阻）重试后返回 null。</summary>
    public string CreateBackup(string sourceFile, string saveName, int day, string source, string label, string gameBuild)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (bytes, md5) = ReadStable(sourceFile);          // 先读稳，失败时目录零残留
                var dir = AllocBackupDir(saveName, day);
                Directory.CreateDirectory(dir);
                WriteFileAtomic(Path.Combine(dir, SaveFileName), bytes);
                WriteMetaAtomic(dir, new BackupMeta
                {
                    Day = day, SaveName = saveName, Source = source, Label = label,
                    GameBuild = gameBuild, CreatedAt = System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                    Md5 = md5, Files = new[] { SaveFileName },
                    DirName = new DirectoryInfo(dir).Name
                });
                return dir;
            }
            catch
            {
                if (attempt >= 3) return null;
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>列出完整备份（孤儿目录/坏 meta 跳过），按 (Day, CreatedAt) 升序。</summary>
    public System.Collections.Generic.List<BackupMeta> ListBackups(string saveName)
    {
        var result = new System.Collections.Generic.List<BackupMeta>();
        var root = BackupDir(saveName);
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var meta = ReadMeta(dir);
            if (meta != null) result.Add(meta);
        }
        result.Sort((a, b) => a.Day != b.Day ? a.Day.CompareTo(b.Day) : string.CompareOrdinal(a.CreatedAt, b.CreatedAt));
        return result;
    }

    /// <summary>读 meta.json；任何异常（缺文件/坏 json）返回 null，不抛。</summary>
    public BackupMeta ReadMeta(string backupDir)
    {
        try
        {
            var p = Path.Combine(backupDir, MetaFileName);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<BackupMeta>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    /// <summary>按 (Day, CreatedAt) 升序删超限的完整备份，返回删除数；孤儿目录不动。</summary>
    public int Prune(string saveName, int keep)
    {
        var metas = ListBackups(saveName);
        if (keep < 0) keep = 0;
        if (metas.Count <= keep) return 0;
        var root = BackupDir(saveName);
        var removed = 0;
        foreach (var meta in metas.GetRange(0, metas.Count - keep))
        {
            if (string.IsNullOrEmpty(meta.DirName)) continue;       // 旧版/手写 meta 无目录名 → 不动
            var dir = Path.Combine(root, meta.DirName);
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, true); removed++; } catch { }
        }
        return removed;
    }

    /// <summary>导出 zip 到 destDir（自动创建），返回 zip 路径；失败 null。</summary>
    public string ExportZip(string backupDir, string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            var name = new DirectoryInfo(backupDir).Name + ".zip";
            var zip = Path.Combine(destDir, name);
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(backupDir, zip);
            return zip;
        }
        catch { return null; }
    }

    /// <summary>最新完整备份目录；无 → null。</summary>
    public string LatestBackupDir(string saveName)
    {
        var metas = ListBackups(saveName);
        if (metas.Count == 0) return null;
        var meta = metas[metas.Count - 1];
        if (string.IsNullOrEmpty(meta.DirName)) return null;
        var dir = Path.Combine(BackupDir(saveName), meta.DirName);
        return Directory.Exists(dir) ? dir : null;
    }

    public int? ReadLastAutoDay(string saveName) => ReadState().TryGetValue(saveName, out var d) ? d : null;

    public void WriteLastAutoDay(string saveName, int day)
    {
        var st = ReadState();
        st[saveName] = day;
        try
        {
            Directory.CreateDirectory(_dataRoot);
            var p = Path.Combine(_dataRoot, StateFileName);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }
        catch { }
    }

    // ---- internals ----

    private System.Collections.Generic.Dictionary<string, int> ReadState()
    {
        try
        {
            var p = Path.Combine(_dataRoot, StateFileName);
            if (!File.Exists(p)) return new System.Collections.Generic.Dictionary<string, int>();
            return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, int>>(File.ReadAllText(p))
                   ?? new System.Collections.Generic.Dictionary<string, int>();
        }
        catch { return new System.Collections.Generic.Dictionary<string, int>(); }
    }

    private (byte[] bytes, string md5) ReadStable(string path)
    {
        var a = File.ReadAllBytes(path);
        var h1 = System.Convert.ToHexString(MD5.HashData(a)).ToLower();
        var b = File.ReadAllBytes(path);
        var h2 = System.Convert.ToHexString(MD5.HashData(b)).ToLower();
        if (h1 != h2) throw new IOException("source unstable: " + path);
        return (a, h1);
    }

    private string AllocBackupDir(string saveName, int day)
    {
        var root = BackupDir(saveName);
        var baseName = "D" + day + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(root, baseName);
        for (var n = 2; ; n++)
        {
            if (!Directory.Exists(dir) && !File.Exists(dir)) return dir;
            dir = Path.Combine(root, baseName + "_" + n);
        }
    }

    private static void WriteFileAtomic(string dest, byte[] bytes)
    {
        var tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    private static void WriteMetaAtomic(string dir, BackupMeta meta)
    {
        var dest = Path.Combine(dir, MetaFileName);
        var tmp = dest + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta));
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }
}
