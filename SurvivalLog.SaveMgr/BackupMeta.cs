using System.Text.Json.Serialization;

namespace SurvivalLog.SaveMgr;

/// <summary>单份备份的元数据（meta.json）。Source: "auto"=结算自动 / "manual"=手动。</summary>
public sealed class BackupMeta
{
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("saveName")] public string SaveName { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; }
    [JsonPropertyName("gameBuild")] public string GameBuild { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; }
    [JsonPropertyName("md5")] public string Md5 { get; set; }
    [JsonPropertyName("files")] public string[] Files { get; set; }
    /// <summary>备份目录名（D&lt;day&gt;_&lt;ts&gt;[_n]，Prune/Latest 定位用；由创建时写入）。</summary>
    [JsonPropertyName("dirName")] public string DirName { get; set; }
}
