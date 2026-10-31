using System;
using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>注入式一键回档（纯逻辑部分，测试工程直链）。interop 部分见 RestoreInjector.Game.cs。
/// 设计：B 方案（docs/2026-10-31-B方案注入验证.md）——备份字节复制成新档名 + 游戏自己的
/// AddHistoryEntry 注册；对 Saves/ 的唯一写入=新增文件，从不改写现有档。</summary>
public static partial class RestoreInjector
{
    /// <summary>savesDir 下该档名是否未被占用。接受带或不带 .bytes 的名字（游戏生成器输出带后缀，
    /// 2026-10-31 实机实证）。</summary>
    public static bool PathIsFree(string savesDir, string fileName)
    {
        try
        {
            var f = fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".bytes";
            return !File.Exists(Path.Combine(savesDir, f));
        }
        catch { return false; }
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
