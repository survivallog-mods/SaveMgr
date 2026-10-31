using System;
using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>备份编排（纯逻辑，依赖全注入：路径/间隔/保留数/构建号/trace）。
/// 自动：OnSettlement(day)——间隔策略 → autoSource（结算时刻的新鲜 snap，无则跳过不回退主档，
/// 防旧档标新天）→ 落备份 → 记 lastAutoDay → 滚动清理。
/// 手动：ManualBackup()——manualSource（新鲜 snap 优先，回退主档）。</summary>
public sealed class BackupService
{
    private readonly BackupStore _store;
    private readonly Func<string> _saveName;
    private readonly Func<string> _autoSource;    // 结算 snap（或 null=跳过）
    private readonly Func<string> _manualSource;  // snap ?? 主档
    private readonly Func<int> _intervalDays;
    private readonly Func<int> _keepPerSave;
    private readonly Func<int> _currentDay;
    private readonly string _gameBuild;
    private readonly Action<string> _trace;

    public BackupService(BackupStore store, Func<string> saveName,
        Func<string> autoSource, Func<string> manualSource,
        Func<int> intervalDays, Func<int> keepPerSave, Func<int> currentDay,
        string gameBuild, Action<string> trace)
    {
        _store = store; _saveName = saveName; _autoSource = autoSource; _manualSource = manualSource;
        _intervalDays = intervalDays; _keepPerSave = keepPerSave; _currentDay = currentDay;
        _gameBuild = gameBuild; _trace = trace ?? (_ => { });
    }

    public void OnSettlement(int day)
    {
        try
        {
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) { _trace("auto: 无当前档名，跳过"); return; }
            var last = _store.ReadLastAutoDay(saveName);
            if (!IntervalPolicy.ShouldBackup(day, last, _intervalDays())) return;

            var src = _autoSource();
            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            { _trace($"auto: 第{day}天无新鲜 snap，跳过（防旧档标新天）"); return; }

            var dir = _store.CreateBackup(src, saveName, day, "auto", $"第{day}天", _gameBuild);
            if (dir == null) { _trace($"auto: 第{day}天备份失败（源锁/写受阻）"); return; }
            _store.WriteLastAutoDay(saveName, day);
            var pruned = _store.Prune(saveName, _keepPerSave());
            _trace($"auto: 第{day}天 → {Path.GetFileName(dir)}（清理 {pruned}）");
        }
        catch (System.Exception e) { _trace("auto: " + e.Message); }
    }

    public string ManualBackup()
    {
        try
        {
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) { _trace("manual: 无当前档名"); return null; }
            var src = _manualSource();
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { _trace("manual: 无可用存档文件"); return null; }
            var day = _currentDay();
            var dir = _store.CreateBackup(src, saveName, day, "manual", $"手动·第{day}天", _gameBuild);
            if (dir == null) { _trace("manual: 备份失败"); return null; }
            var pruned = _store.Prune(saveName, _keepPerSave());
            _trace($"manual: 第{day}天 → {Path.GetFileName(dir)}（清理 {pruned}）");
            return dir;
        }
        catch (System.Exception e) { _trace("manual: " + e.Message); return null; }
    }
}
