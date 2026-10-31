using System.IO;
using System.Linq;

namespace SurvivalLog.SaveMgr;

/// <summary>存档文件选取（纯函数，零游戏依赖）：按 mtime 选最新 snap；主档路径拼装。</summary>
public static class SaveFiles
{
    /// <summary>&lt;savesDir&gt;/&lt;saveName&gt;.snap*.bytes 中 mtime 最新者；无 → null。</summary>
    public static string NewestSnapPath(string savesDir, string saveName)
    {
        try
        {
            var snaps = Directory.GetFiles(savesDir, saveName + ".snap*.bytes");
            return snaps.Length == 0
                ? null
                : snaps.OrderByDescending(f => File.GetLastWriteTime(f)).First();
        }
        catch { return null; }
    }

    public static string MainSavePath(string savesDir, string saveName)
        => Path.Combine(savesDir, saveName + ".bytes");
}
