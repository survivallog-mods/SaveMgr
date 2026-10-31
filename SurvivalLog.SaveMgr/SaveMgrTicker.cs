using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.SaveMgr;

/// <summary>主线程动作队列（Harmony postfix/Ticker 自身投递，Update 排空执行，单条异常隔离）。</summary>
public static class SaveMgrRuntime
{
    private static readonly Queue<Action> Queue = new();

    public static void Enqueue(Action a)
    {
        lock (Queue) { Queue.Enqueue(a); }
    }

    internal static void Drain()
    {
        while (true)
        {
            Action a;
            lock (Queue) { if (Queue.Count == 0) return; a = Queue.Dequeue(); }
            try { a(); }
            catch (Exception e) { SaveMgrPlugin.Trace("runtime: " + e.Message); }
        }
    }
}

/// <summary>2s tick：排空主线程队列 + F9 手动备份。
/// Update 必须 public + 双构造器（RoleUnlock 实证模式：interop 注入类只桥接 public 生命周期方法）。</summary>
internal sealed class SaveMgrTicker : MonoBehaviour
{
    private float _acc;
    private KeyCode _manualKey = KeyCode.F9;
    private KeyCode _restoreKey = KeyCode.F10;
    private bool _keyResolved;

    public SaveMgrTicker(IntPtr p) : base(p) { }
    public SaveMgrTicker() { }

    public void Update()
    {
        _acc += Time.unscaledDeltaTime;
        if (_acc < 2f) return;
        _acc = 0;
        if (SaveMgrPlugin.Enabled != null && !SaveMgrPlugin.Enabled.Value) return;
        SaveMgrRuntime.Drain();
        try { SaveMgrPlugin.Service?.Tick(); } catch (Exception e) { SaveMgrPlugin.Trace("svcTick: " + e.Message); }
        PollManualHotkey();
        PollRestoreHotkey();
        PollCmdFile();
        DayWatcher.Step();
    }

    /// <summary>文件触发通道（本机调试用，等同热键）：plugins-data/SaveMgr/cmd.txt 每行一条
    /// —— manual=手动备份 / restore=一键回档；读后即删。键盘注入对游戏不可靠（2026-10-31
    /// SendInput 实证），此通道走文件系统与游戏无关，自动化验收靠它。</summary>
    private static void PollCmdFile()
    {
        try
        {
            var p = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins-data", "SaveMgr", "cmd.txt");
            if (!System.IO.File.Exists(p)) return;
            var cmds = System.IO.File.ReadAllLines(p);
            try { System.IO.File.Delete(p); } catch { }
            foreach (var raw in cmds)
            {
                var cmd = (raw ?? "").Trim().ToLowerInvariant();
                if (cmd == "manual")
                {
                    SaveMgrPlugin.Trace("cmd: manual");
                    var svc = SaveMgrPlugin.Service;
                    if (svc != null) SaveMgrRuntime.Enqueue(() => svc.ManualBackup());
                }
                else if (cmd == "restore")
                {
                    SaveMgrPlugin.Trace("cmd: restore");
                    SaveMgrRuntime.Enqueue(() =>
                    {
                        var r = RestoreInjector.InjectLatest();
                        SaveMgrPlugin.Trace("restore: ok=" + r.Ok + " reason=" + r.Reason);
                    });
                }
                else if (cmd.Length > 0) SaveMgrPlugin.Trace("cmd: 未知指令 " + cmd);
            }
        }
        catch (Exception e) { SaveMgrPlugin.Trace("cmd: " + e.Message); }
    }

    private void PollManualHotkey()
    {
        if (!_keyResolved)
        {
            var s = SaveMgrPlugin.ManualHotkey?.Value;
            if (!string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out KeyCode k)) { _manualKey = k; _keyResolved = true; }
        }
        try
        {
            if (!Input.GetKeyDown(_manualKey)) return;
            var svc = SaveMgrPlugin.Service;
            if (svc == null) { SaveMgrPlugin.Trace("F9: service 未就绪"); return; }
            SaveMgrPlugin.Trace("F9: 手动备份触发");
            SaveMgrRuntime.Enqueue(() => svc.ManualBackup());
        }
        catch (Exception e) { SaveMgrPlugin.Trace("hotkey: " + e.Message); }
    }

    private void PollRestoreHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(_restoreKey)) return;
            SaveMgrPlugin.Trace("F10: 一键回档触发");
            SaveMgrRuntime.Enqueue(() => RestoreInjector.InjectLatest());
        }
        catch (Exception e) { SaveMgrPlugin.Trace("restore hotkey: " + e.Message); }
    }
}

internal static class TickerHost
{
    internal static void Install()
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<SaveMgrTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<SaveMgrTicker>();
        var go = new GameObject("SaveMgr.Ticker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<SaveMgrTicker>();
    }
}
