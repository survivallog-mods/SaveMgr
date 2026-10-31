using System.IO;
using System.Security.Cryptography;

namespace SurvivalLog.SaveMgr;

/// <summary>稳定读（原 BackupStore 唯一幸存者）：md5 双读 + mtime 栅栏（读前/读后 mtime 变化=仍在写，
/// 拒收）。midReadHook 供单测注入。备份目录体系已随"零隐藏副本"定案移除（2026-11-01）。</summary>
public static class BackupStore
{
    public static (byte[] bytes, string md5) ReadStable(string path, System.Action midReadHook = null)
    {
        var m0 = File.GetLastWriteTimeUtc(path);
        var a = File.ReadAllBytes(path);
        var h1 = System.Convert.ToHexString(MD5.HashData(a)).ToLower();
        midReadHook?.Invoke();
        var b = File.ReadAllBytes(path);
        var h2 = System.Convert.ToHexString(MD5.HashData(b)).ToLower();
        var m1 = File.GetLastWriteTimeUtc(path);
        if (h1 != h2 || m0 != m1) throw new IOException("source unstable: " + path);
        return (a, h1);
    }
}
