using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace SurvivalLog.SaveMgr;

/// <summary>存档管理：结算自动备份（间隔可设）+ 手动备份 + 备份管理 + 注入式一键回档。
/// 机制与实证见 DESIGN.md（gamekb 2026-10-23_26157732）。当前为脚手架：仅自证加载，功能未实现。</summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class SaveMgrPlugin : BasePlugin
{
    public const string Guid = "com.local.survivallog.savemgr";
    public const string Name = "SaveMgr";
    public const string Version = "0.0.0"; // 占位：版本号由作者定（硬规则1），release.ps1 校验与本串一致

    internal static ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        Trace("loaded：脚手架阶段（无功能钩子；设计见 DESIGN.md）");
        Log.LogInfo("[SaveMgr] 存档管理 loaded（scaffold，功能未实现）");
    }

    /// <summary>自证文件（硬规则9：不依赖 LogOutput.log）。每行带时间戳。</summary>
    internal static void Trace(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "SaveMgr_trace.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { }
    }
}
