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
        public string Source;        // "auto" | "manual"（按钮/F9 同路径：先强制持久化再抄）
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
    private readonly bool _synchronous;   // 单测=同步执行抄档；实机=后台线程（主线程零 IO 卡顿）
    private readonly System.Action<System.Action> _toMainThread;  // 后台完成回主线程（实机=SaveMgrRuntime.Enqueue；纯测=null 同步跑）

    private PendingAuto _pending;

    public BackupService(BackupStore store, Func<string> saveName, Func<string> mainPath,
        Action forcePersist, Func<int> intervalDays, Func<int> keepPerSave, Func<int> currentDay,
        string gameBuild, Action<string> trace, TimeSpan? persistTimeout = null, Func<bool> autoEnabled = null,
        bool synchronous = false, System.Action<System.Action> toMainThread = null)
    {
        _store = store; _saveName = saveName; _mainPath = mainPath; _forcePersist = forcePersist;
        _autoEnabled = autoEnabled; _synchronous = synchronous; _toMainThread = toMainThread;
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
                Day = day, Source = "auto",
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

    /// <summary>手动「立即存档」（按钮/F9/cmd 同路径）：先强制持久化当前进度，落盘后抄走。
    /// 与 OnSettlement 同一状态机（Tick 驱动非阻塞）；完成经 OnManualDone 回调（UI toast）。</summary>
    public void ManualBackupNow()
    {
        try
        {
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) { _trace("manual: 无当前档名"); OnManualDone?.Invoke(-1, "无当前档"); return; }
            var main = _mainPath();
            if (string.IsNullOrEmpty(main) || !File.Exists(main)) { _trace("manual: 主档不存在"); OnManualDone?.Invoke(-1, "主档不存在"); return; }
            _pending = new PendingAuto
            {
                Day = _currentDay(), Source = "manual",
                SaveName = saveName,
                MainPath = main,
                Mtime0 = File.GetLastWriteTime(main),
                DeadlineTick = System.Environment.TickCount + (int)_persistTimeout.TotalMilliseconds
            };
            _trace("manual: 请求游戏持久化（SaveGame）→ 落盘后抄");
            _forcePersist?.Invoke();
        }
        catch (System.Exception e) { _trace("manual: " + e.Message); OnManualDone?.Invoke(-1, e.Message); }
    }

    /// <summary>手动存档完成回调：day≥0=成功（dir=备份目录）；day=-1=失败（第二参=原因）。</summary>
    public event System.Action<int, string> OnManualDone;

    /// <summary>每 ticker 周期驱动：主档落盘（mtime 越过基线）即抄。非阻塞。</summary>
    public void Tick()
    {
        var p = _pending;
        if (p == null) return;
        try
        {
            if (System.Environment.TickCount - p.DeadlineTick > 0)
            {
                _trace($"{p.Source}: 第{p.Day}天持久化超时未落盘，放弃（主档 mtime 未变化）");
                _pending = null;
                if (p.Source == "manual")
                {
                    try { OnManualDone?.Invoke(-1, "持久化超时"); }
                    catch { }
                }
                return;
            }
            var mtime0 = p.Mtime0;
            if (!File.Exists(p.MainPath) || File.GetLastWriteTime(p.MainPath) <= mtime0) return;

            var saveName = p.SaveName;                            // 武装时刻捕获，防切档串档
            _pending = null;                                      // 先摘牌防重入（重活马上离场主线程）
            var keep = _keepPerSave();
            void Finish(string dir, int pruned)
            {
                if (dir == null) { _trace($"{p.Source}: 第{p.Day}天备份失败（源锁/写受阻）"); }
                else
                {
                    if (p.Source == "auto") _store.WriteLastAutoDay(saveName, p.Day);
                    _trace($"{p.Source}: 第{p.Day}天 → {Path.GetFileName(dir)}（清理 {pruned}）");
                }
                if (p.Source == "manual")
                {
                    try { OnManualDone?.Invoke(dir == null ? -1 : p.Day, dir ?? "备份失败"); }
                    catch (System.Exception e) { _trace("manualDone: " + e.Message); }
                }
            }
            void Work()                                           // 字节抄档+md5+清理：纯文件 IO
            {
                var dir = _store.CreateBackup(p.MainPath, saveName, p.Day, p.Source,
                    (p.Source == "manual" ? "手动·" : "第") + p.Day + "天", _gameBuild);
                var pruned = dir == null ? 0 : _store.Prune(saveName, keep);
                if (_synchronous || _toMainThread == null) Finish(dir, pruned);
                else _toMainThread(() => Finish(dir, pruned));
            }
            if (_synchronous) Work();
            else System.Threading.Tasks.Task.Run(Work);           // 后台线程：主线程只留 mtime 判定
        }
        catch (System.Exception e) { _trace("autoTick: " + e.Message); _pending = null; }
    }

}
