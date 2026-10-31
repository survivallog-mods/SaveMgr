using System;
using System.IO;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>存档点服务（2026-11-01 作者定案：**零隐藏备份**，所有存档点=官方读档列表条目）。
/// 自动：结算 hook → 武装等待 → 游戏自己写主档（无尽类结算会写；普通档不写→超时跳过，
/// **绝不自调 Gsm.SaveGame()**——00:35 native 崩溃实锤）→ 主档字节暂存 staging/ →
/// 下次到主菜单由 MenuButtons 注册为官方存档点（注入后暂存即删）。
/// 手动：暂停菜单「立即存档」=官方「保存并退出」接力（MenuButtons 全权处理，不经本服务）。</summary>
public sealed class SavepointService
{
    private sealed class Pending
    {
        public int Day;
        public string SaveName;
        public string MainPath;
        public DateTime Mtime0;
        public int DeadlineTick;
    }

    private readonly StateStore _store;
    private readonly Func<string> _saveName;
    private readonly Func<string> _mainPath;
    private readonly Func<int> _intervalDays;
    private readonly Func<int> _currentDay;
    private readonly Action<string> _trace;
    private readonly string _stagingDir;
    private readonly TimeSpan _waitTimeout;
    private readonly Action<string, int> _onStaged;   // (暂存路径, day) —— 注入队列

    private Pending _pending;

    public SavepointService(StateStore store, Func<string> saveName, Func<string> mainPath,
        Func<int> intervalDays, Func<int> currentDay, string stagingDir,
        Action<string, int> onStaged, Action<string> trace, TimeSpan? waitTimeout = null)
    {
        _store = store; _saveName = saveName; _mainPath = mainPath;
        _intervalDays = intervalDays; _currentDay = currentDay;
        _stagingDir = stagingDir; _onStaged = onStaged;
        _trace = trace ?? (_ => { });
        _waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(15);
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
                Day = day,
                SaveName = saveName,
                MainPath = main,
                Mtime0 = File.GetLastWriteTime(main),
                DeadlineTick = System.Environment.TickCount + (int)_waitTimeout.TotalMilliseconds
            };
            _trace($"auto: 第{day}天 → 等待游戏结算写盘（15s 内主档 mtime 变化即暂存）");
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
                _trace($"auto: 第{p.Day}天结算未写主档（普通档结算不落盘）→ 跳过自动存档点");
                _pending = null;
                return;
            }
            if (!File.Exists(p.MainPath) || File.GetLastWriteTime(p.MainPath) <= p.Mtime0) return;

            _pending = null;
            // 稳定读（md5 双读+mtime 栅栏）→ 暂存（注入在主菜单期，崩溃/未到菜单均可恢复）
            var (bytes, _) = BackupStore.ReadStable(p.MainPath);
            Directory.CreateDirectory(_stagingDir);
            var staged = Path.Combine(_stagingDir, "D" + p.Day + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bytes");
            File.WriteAllBytes(staged, bytes);
            _store.WriteLastAutoDay(p.SaveName, p.Day);
            _trace($"auto: 第{p.Day}天已暂存 → {Path.GetFileName(staged)}（主菜单时注册为存档点）");
            try { _onStaged?.Invoke(staged, p.Day); }
            catch (System.Exception e) { _trace("onStaged: " + e.Message); }
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
