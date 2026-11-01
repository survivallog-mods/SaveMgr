using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SurvivalLog.SaveMgr;

/// <summary>待注册新档的落盘队列模型（纯逻辑）：file=新档名（Save_*.bytes），source=原档名
/// （注册时无尽 meta 的复制源）。读档列表的类型标签（无尽/剧情无尽）由 HistoryChild 无尽块承载，
/// AddHistoryEntry 不回填（AssignChildMetaFromGameSave 反汇编实锤 2026-11-01）——注册时须按
/// 原档条目复制，source 即为此落盘；崩溃/换档后补注册仍能取到正确类型。</summary>
public sealed class PendingReg
{
    [JsonPropertyName("f")] public string File { get; set; }
    [JsonPropertyName("s")] public string Source { get; set; }

    public PendingReg() { }

    public PendingReg(string file, string source) { File = file; Source = source; }

    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>序列化（[{"f":..,"s":..}]；source=null 省略，null 队列=空数组）。</summary>
    public static string Serialize(IReadOnlyList<PendingReg> items)
    {
        return JsonSerializer.Serialize(items ?? Array.Empty<PendingReg>(), _opts);
    }

    /// <summary>反序列化（不抛异常）。兼容旧版 pending.json 的纯 string[] 条目（→ source=null，
    /// 注册侧再回落当前档名取类型）。坏 JSON/空 → 空表。</summary>
    public static List<PendingReg> Deserialize(string json)
    {
        var r = new List<PendingReg>();
        if (string.IsNullOrWhiteSpace(json)) return r;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return r;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)                 // 旧格式：纯档名
                    r.Add(new PendingReg(e.GetString(), null));
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    string f = null, s = null;
                    if (e.TryGetProperty("f", out var fe) && fe.ValueKind == JsonValueKind.String) f = fe.GetString();
                    if (e.TryGetProperty("s", out var se) && se.ValueKind == JsonValueKind.String) s = se.GetString();
                    if (!string.IsNullOrEmpty(f)) r.Add(new PendingReg(f, s));
                }
            }
            return r;
        }
        catch { return r; }
    }
}
