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
    private KeyCode _manualKey = KeyCode.F9;   // 键名固定（cfg 极简面不暴露）
    private bool _keyResolved;

    public SaveMgrTicker(IntPtr p) : base(p) { }
    public SaveMgrTicker() { }

    public void Update()
    {
        // 热键每帧轮询（GetKeyDown 是帧边沿信号，栅在 2s 节拍上会几乎必然漏按——终审 Critical）
        try
        {
            if (SaveMgrPlugin.Enabled == null || SaveMgrPlugin.Enabled.Value)
            {
                PollManualHotkey();
                    }
        }
        catch (Exception e) { SaveMgrPlugin.Trace("hotkey frame: " + e.Message); }

        _acc += Time.unscaledDeltaTime;
        if (_acc < 2f) return;
        _acc = 0;
        if (SaveMgrPlugin.Enabled != null && !SaveMgrPlugin.Enabled.Value) return;
        SaveMgrRuntime.Drain();
        try { SaveMgrPlugin.Service?.Tick(); } catch (Exception e) { SaveMgrPlugin.Trace("svcTick: " + e.Message); }
        PollCmdFile();
        MenuButtons.Tick();
        DayWatcher.Step();
    }

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
                    SaveMgrRuntime.Enqueue(MenuButtons.TriggerSavepoint);   // 官方「保存并退出」接力（JS 找不到按钮自动兜底 backup）
                }
                else if (cmd == "tomenu")
                {
                    SaveMgrPlugin.Trace("cmd: tomenu");
                    SaveMgrRuntime.Enqueue(() =>
                    {
                        var ma = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.MainAction>.Instance;
                        var ent = ma == null ? null : ma.Entrance;
                        if (ent == null) { SaveMgrPlugin.Trace("tomenu: Entrance 未就绪"); return; }
                        ent.BattleToMain();
                    });
                }
                else if (cmd.Length > 0) SaveMgrPlugin.Trace("cmd: 未知指令 " + cmd);
            }
        }
        catch (Exception e) { SaveMgrPlugin.Trace("cmd: " + e.Message); }
    }

    private void PollManualHotkey()
    {
        try
        {
            if (!Input.GetKeyDown(_manualKey)) return;
            SaveMgrPlugin.Trace("F9: 手动存档");
            SaveMgrRuntime.Enqueue(MenuButtons.TriggerSavepoint);
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
