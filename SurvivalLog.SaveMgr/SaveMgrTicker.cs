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
        PollManualHotkey();
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
