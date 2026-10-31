using System;
using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入式一键回档（纯逻辑部分，测试工程直链）。interop 部分见 RestoreInjector.Game.cs。
/// 设计：B 方案（docs/2026-10-31-B方案注入验证.md）——备份字节复制成新档名 + 游戏自己的
/// AddHistoryEntry 注册；对 Saves/ 的唯一写入=新增文件，从不改写现有档。</summary>
public static partial class RestoreInjector
{
    /// <summary>savesDir 下该档名是否未被占用（主文件与成对 .bak 都要干净——孤儿 .bak 会被
    /// LoadWithBackupFallback 配对旧字节）。接受带或不带 .bytes 的名字（实机实证约定带后缀）。</summary>
    public static bool PathIsFree(string savesDir, string fileName)
    {
        try
        {
            var f = fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".bytes";
            return !File.Exists(Path.Combine(savesDir, f)) && !File.Exists(Path.Combine(savesDir, f + ".bak"));
        }
        catch { return false; }
    }

    /// <summary>名字可注册为列表条目：剥壳后必须能按 yyyy_MM_dd_HH_mm_ss 解析（真凶A律：
    /// 非时间串 Name → ParseSaveTimeShort 失败 → 行被静默丢弃）。</summary>
    public static bool IsTimestampName(string fileName)
    {
        var n = fileName ?? "";
        if (n.StartsWith("Save_")) n = n["Save_".Length..];
        if (n.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)) n = n[..^".bytes".Length];
        return System.DateTime.TryParseExact(n, "yyyy_MM_dd_HH_mm_ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>名字重试：generator 产名 → PathIsFree 通过即返回；重试 maxTries 仍撞 → null（不覆盖任何文件）。</summary>
    public static string ResolveFreeName(string savesDir, Func<string> generator, int maxTries = 3)
    {
        for (var i = 0; i < maxTries; i++)
        {
            var name = generator();
            if (string.IsNullOrEmpty(name)) continue;
            if (PathIsFree(savesDir, name)) return name;
        }
        return null;
    }
}
