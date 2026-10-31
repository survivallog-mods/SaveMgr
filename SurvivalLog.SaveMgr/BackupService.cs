using System;
using System.IO;

namespace SurvivalLog.SaveMgr;

/// <summary>备份编排（纯逻辑，依赖全注入）。字节源=**主档**（2026-10-31 实机定案：
/// .snapN 是 RebirthDaySnapshot 元数据包装非完整档；且普通档结算/退出都不落盘——
/// 无尽档的"每结算写 snap+主档"是无尽系统行为。故自动备份=hook 后主动 SaveGame 强制
/// 持久化，等主档 mtime 变化后抄主档）。
/// 状态机：OnSettlement(day) 判间隔 → 记 mtime0 → forcePersist（游戏主线程）；
/// Tick() 每 ticker 周期查主档 mtime 变化 → BackupStore.CreateBackup（内部 md5 双读）→
/// 记 lastAutoDay → Prune；超时（PersistTimeout）未落盘则放弃并 trace。</summary>
public sealed class BackupService
{
    private sealed class PendingAuto
    {
        public int Day;
        public string SaveName;      // 武装时刻捕获（Tick 不再重读——终审：防结算窗口切档串档）
        public string MainPath;
        public DateTime Mtime0;
        public int DeadlineTick;     // Environment.TickCount（墙钟受校时/DST 干扰）
    }

    private readonly BackupStore _store;
    private readonly Func<string> _saveName;
    private readonly Func<string> _mainPath;
    private readonly Action _forcePersist;
    private readonly Func<bool> _autoEnabled;
    private readonly Func<int> _intervalDays;
    private readonly Func<int> _keepPerSave;
    private readonly Func<int> _currentDay;
    private readonly string _gameBuild;
    private readonly Action<string> _trace;
    private readonly TimeSpan _persistTimeout;

    private PendingAuto _pending;

    public BackupService(BackupStore store, Func<string> saveName, Func<string> mainPath,
        Action forcePersist, Func<int> intervalDays, Func<int> keepPerSave, Func<int> currentDay,
        string gameBuild, Action<string> trace, TimeSpan? persistTimeout = null, Func<bool> autoEnabled = null)
    {
        _store = store; _saveName = saveName; _mainPath = mainPath; _forcePersist = forcePersist;
        _autoEnabled = autoEnabled;
        _intervalDays = intervalDays; _keepPerSave = keepPerSave; _currentDay = currentDay;
        _gameBuild = gameBuild; _trace = trace ?? (_ => { });
        _persistTimeout = persistTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>结算信号（主线程）：AutoBackup 扼点（hook/兜底 watcher 两条路都过这里）→
    /// 判间隔 → 武装持久化等待。同日二次信号在首次未完成前直接让路（防重置基线漏一天）。</summary>
    public void OnSettlement(int day)
    {
        try
        {
            if (_autoEnabled != null && !_autoEnabled()) return;   // cfg AutoBackup=false 唯一扼点
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) { _trace("auto: 无当前档名，跳过"); return; }
            if (_pending != null && _pending.Day == day) return;   // 同日已武装未完成：让路
            var last = _store.ReadLastAutoDay(saveName);
            if (!IntervalPolicy.ShouldBackup(day, last, _intervalDays())) return;

            var main = _mainPath();
            if (string.IsNullOrEmpty(main) || !File.Exists(main)) { _trace($"auto: 第{day}天主档不存在，跳过"); return; }

            _pending = new PendingAuto
            {
                Day = day,
                SaveName = saveName,
                MainPath = main,
                Mtime0 = File.GetLastWriteTime(main),
                DeadlineTick = System.Environment.TickCount + (int)_persistTimeout.TotalMilliseconds
            };
            _trace($"auto: 第{day}天 → 请求游戏持久化（SaveGame）");
            _forcePersist?.Invoke();
        }
        catch (System.Exception e) { _trace("auto: " + e.Message); }
    }

    /// <summary>每 ticker 周期驱动：主档落盘（mtime 越过基线）即抄。非阻塞。</summary>
    public void Tick()
    {
        var p = _pending;
        if (p == null) return;
        try
        {
            if (System.Environment.TickCount - p.DeadlineTick > 0)
            {
                _trace($"auto: 第{p.Day}天持久化超时未落盘，放弃（主档 mtime 未变化）");
                _pending = null;
                return;
            }
            var mtime0 = p.Mtime0;
            if (!File.Exists(p.MainPath) || File.GetLastWriteTime(p.MainPath) <= mtime0) return;

            var saveName = p.SaveName;                            // 武装时刻捕获，防切档串档
            var dir = _store.CreateBackup(p.MainPath, saveName, p.Day, "auto", $"第{p.Day}天", _gameBuild);
            _pending = null;
            if (dir == null) { _trace($"auto: 第{p.Day}天备份失败（源锁/写受阻）"); return; }
            _store.WriteLastAutoDay(saveName, p.Day);
            var pruned = _store.Prune(saveName, _keepPerSave());
            _trace($"auto: 第{p.Day}天 → {Path.GetFileName(dir)}（清理 {pruned}）");
        }
        catch (System.Exception e) { _trace("autoTick: " + e.Message); _pending = null; }
    }

    /// <summary>手动备份：直接抄主档（新鲜度=最近一次持久化；自动备份的强制持久化不在此路径）。</summary>
    public string ManualBackup()
    {
        try
        {
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) { _trace("manual: 无当前档名"); return null; }
            var src = _mainPath();
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { _trace("manual: 主档不存在"); return null; }
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
