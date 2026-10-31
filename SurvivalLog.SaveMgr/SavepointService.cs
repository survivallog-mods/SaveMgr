using System;
using System.IO;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>存档点服务（2026-11-01 作者终版定案：**原地存、不退出、新增档、零隐藏副本**）。
/// 手动「立即存档」：保护期检查（进档 <30s 拒绝——00:35 进档初期 SaveGame native 崩溃实锤）→
/// Gsm.SaveGame() 持久化当前进度 → 等主档 mtime → 字节复制成**新档名**（身份重写走游戏 API，
/// 原档不动，文件直接落 Saves/）→ 记 pending 注册 → toast；局内读取存档面板立即可见可选。
/// 自动（结算）：等游戏自己的结算写盘（无尽类写主档；普通档不写→超时跳过，绝不自调 SaveGame）
/// → 同样新档名+pending。pending 由 MenuButtons 在主菜单期统一 AddHistoryEntry 补注册。</summary>
public sealed class SavepointService
{
    private sealed class Pending
    {
        public int Day;
        public string Source;      // manual | auto
        public string SaveName;
        public string MainPath;
        public DateTime Mtime0;
        public int DeadlineTick;
        public int Retries;         // SaveGame 被世界安全检查静默拒时重试（每轮重调+再等）
    }

    private readonly StateStore _store;
    private readonly Func<string> _saveName;
    private readonly Func<string> _mainPath;
    private readonly Func<int> _intervalDays;
    private readonly Func<int> _currentDay;
    private readonly Action<string> _trace;
    private readonly string _savesDir;
    private readonly TimeSpan _waitTimeout;
    private readonly Action<string> _onNewSavepoint;  // 新档落成回调（MenuButtons：面板/toast/注册队列）
    private readonly System.Action _persist;          // 持久化入口（插件=SaveMgrPlugin.PersistNow 带战龄保护；纯测直写）

    private Pending _pending;

    public SavepointService(StateStore store, Func<string> saveName, Func<string> mainPath,
        Func<int> intervalDays, Func<int> currentDay, string savesDir,
        Action<string> onNewSavepoint, Action<string> trace, TimeSpan? waitTimeout = null,
        System.Action persist = null)
    {
        _store = store; _saveName = saveName; _mainPath = mainPath;
        _intervalDays = intervalDays; _currentDay = currentDay;
        _savesDir = savesDir; _onNewSavepoint = onNewSavepoint;
        _trace = trace ?? (_ => { });
        _waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(15);
        _persist = persist;
    }

    /// <summary>手动「立即存档」：原地持久化当前进度 → 新增一份档（不退出、不覆盖）。
    /// 返回错误文案；null=已受理（完成经 _onNewSavepoint 回调）。</summary>
    public string ManualSaveNow()
    {
        try
        {
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) return "无当前档";
            var main = _mainPath();
            if (string.IsNullOrEmpty(main) || !File.Exists(main)) return "主档不存在";
            _pending = new Pending
            {
                Day = _currentDay(), Source = "manual",
                SaveName = saveName, MainPath = main,
                Mtime0 = File.GetLastWriteTime(main),
                DeadlineTick = System.Environment.TickCount + (int)_waitTimeout.TotalMilliseconds
            };
            _trace("manual: 立即存档 → 请求游戏持久化（SaveGame）");
            _persist?.Invoke();                                    // 插件侧带战龄保护（进档 <30s 拒绝）
            return null;
        }
        catch (System.Exception e) { _trace("manual: " + e.Message); return e.Message; }
    }

    /// <summary>结算信号：间隔策略 → 武装等待游戏自己的写盘（不主动持久化）。</summary>
    public void OnSettlement(int day)
    {
        try
        {
            if (_intervalDays() <= 0) return;
            var saveName = _saveName();
            if (string.IsNullOrEmpty(saveName)) return;
            if (_pending != null && _pending.Day == day) return;
            var last = _store.ReadLastAutoDay(saveName);
            if (!IntervalPolicy.ShouldBackup(day, last, _intervalDays())) return;

            var main = _mainPath();
            if (string.IsNullOrEmpty(main) || !File.Exists(main)) return;

            _pending = new Pending
            {
                Day = day, Source = "auto",
                SaveName = saveName,
                MainPath = main,
                Mtime0 = File.GetLastWriteTime(main),
                DeadlineTick = System.Environment.TickCount + (int)_waitTimeout.TotalMilliseconds
            };
            _trace($"auto: 第{day}天 → 等待游戏结算写盘（15s 内主档 mtime 变化即取）");
        }
        catch (System.Exception e) { _trace("auto: " + e.Message); }
    }

    /// <summary>每 ticker 周期：主档被游戏写盘（mtime 越基线）→ 字节暂存 → 入注入队列。</summary>
    public void Tick()
    {
        var p = _pending;
        if (p == null) return;
        try
        {
            if (System.Environment.TickCount - p.DeadlineTick > 0)
            {
                if (p.Retries < 2 && p.Source == "manual")
                {
                    // SaveGame 被静默拒（IsWorldSafeToPersist/进档初期）→ 重调再等一轮
                    p.Retries++;
                    p.Mtime0 = File.Exists(p.MainPath) ? File.GetLastWriteTime(p.MainPath) : p.Mtime0;
                    p.DeadlineTick = System.Environment.TickCount + (int)_waitTimeout.TotalMilliseconds;
                    _trace($"{p.Source}: 第{p.Day}天未落盘 → 重试 #{p.Retries}");
                    _persist?.Invoke();
                    return;
                }
                _trace($"{p.Source}: 第{p.Day}天持久化超时未落盘 → 放弃本次");
                _pending = null;
                if (p.Source == "manual") try { _onNewSavepoint?.Invoke(null); } catch { }
                return;
            }
            if (!File.Exists(p.MainPath) || File.GetLastWriteTime(p.MainPath) <= p.Mtime0) return;

            _pending = null;
            // 稳定读（md5 双读+mtime 栅栏）→ 新档落 Saves/（身份重写在 MenuButtons 注入侧完成）
            var (bytes, _) = BackupStore.ReadStable(p.MainPath);
            Directory.CreateDirectory(_savesDir);
            var newFile = "Save_" + System.DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
            for (var n = 2; ; n++)
            {
                if (!File.Exists(Path.Combine(_savesDir, newFile + ".bytes"))) break;
                newFile = "Save_" + System.DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + "_" + n;
            }
            File.WriteAllBytes(Path.Combine(_savesDir, newFile + ".bytes"), bytes);
            if (p.Source == "auto") _store.WriteLastAutoDay(p.SaveName, p.Day);
            _trace($"{p.Source}: 第{p.Day}天新档落成 → {newFile}.bytes");
            try { _onNewSavepoint?.Invoke(newFile + ".bytes"); }
            catch (System.Exception e) { _trace("onNew: " + e.Message); }
        }
        catch (System.Exception e) { _trace("autoTick: " + e.Message); _pending = null; }
    }
}

/// <summary>极小状态：{saveName: lastAutoDay}（state.json，tmp+原子覆盖）。</summary>
public sealed class StateStore
{
    private readonly string _path;

    public StateStore(string dataRoot)
    {
        _path = Path.Combine(dataRoot ?? ".", "state.json");
    }

    public int? ReadLastAutoDay(string saveName) => Read().TryGetValue(saveName, out var d) ? d : null;

    public void WriteLastAutoDay(string saveName, int day)
    {
        try
        {
            var st = Read();
            st[saveName] = day;
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }

    private System.Collections.Generic.Dictionary<string, int> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new System.Collections.Generic.Dictionary<string, int>();
            return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, int>>(File.ReadAllText(_path))
                   ?? new System.Collections.Generic.Dictionary<string, int>();
        }
        catch { return new System.Collections.Generic.Dictionary<string, int>(); }
    }
}
