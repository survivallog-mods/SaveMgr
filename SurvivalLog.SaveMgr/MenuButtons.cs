using System;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>游戏内按钮层（2026-11-01 作者终版）：**只在局内 ESC 暂停菜单一个「立即存档」**。
/// - 读取存档=主菜单官方列表（局内列表已按作者指示移除）；
/// - 立即存档=原地新档（不退出/不覆盖）：SaveGame（结算同构包裹，暂停态=世界安全秒落盘）
///   → 新档落 Saves/ → 身份重写 → pending 注册（主菜单期补进官方列表）；
/// - 按钮三态：准备中（进档 30s 保护期，防与进档流程竞态崩溃）/ 立即存档 / 正在存档…（防重复点击）。
/// 机制照抄 ModCenter MenuEntryInjector 实证模式：Root MutationObserver + iframe load 钩幂等安装。</summary>
internal static class MenuButtons
{
    /// <summary>Root 引导脚本（幂等）。</summary>
    private const string EntryJs = @"
(function () {
  if (window.__smObserver) return;
  window.__smOutbox = [];
  window.__smReady = false;      // 进档 30s 保护期
  window.__smSaving = false;     // 存档进行中（防重复点击）
  function docOf(tail) {
    var fs = document.querySelectorAll('#iframe-container iframe');
    for (var i = 0; i < fs.length; i++) {
      try { var d = fs[i].contentDocument; if (d && d.location.pathname.indexOf(tail) !== -1) return d; } catch (e) {}
    }
    return null;
  }
  window.__smDoc = docOf;
  function toast(doc, msg) {
    try {
      if (!doc || !doc.body) return;
      var t = doc.createElement('div');
      t.textContent = msg;
      t.style.cssText = 'position:fixed;top:16%;left:50%;transform:translateX(-50%);z-index:2147483647;' +
        'background:rgba(12,14,18,.85);color:#f2efe6;padding:10px 24px;border-radius:10px;' +
        'font-size:15px;letter-spacing:1px;pointer-events:none;box-shadow:0 4px 18px rgba(0,0,0,.45);';
      doc.body.appendChild(t);
      setTimeout(function () { t.remove(); }, 3200);
    } catch (e) {}
  }
  window.__smToast = toast;
  function eachSaveBtn(fn) {
    var d = docOf('OutSetting');
    if (!d) return;
    var bs = d.querySelectorAll('.__sm_entry');
    for (var i = 0; i < bs.length; i++) if (bs[i].textContent.indexOf('存档') !== -1) fn(bs[i]);
  }
  function label() {
    return window.__smSaving ? '正在存档…' : (window.__smReady ? '立即存档' : '立即存档（准备中）');
  }
  window.__smRefreshBtn = function () { eachSaveBtn(function (b) { b.textContent = label(); }); };
  window.__smSetReady = function (v) { window.__smReady = !!v; window.__smRefreshBtn(); };
  window.__smSaveDone = function () { window.__smSaving = false; window.__smRefreshBtn(); };
  window.__smSavingSince = 0;
  setInterval(function () {
    if (window.__smSaving && window.__smSavingSince && Date.now() - window.__smSavingSince > 20000) {
      window.__smSaving = false; window.__smRefreshBtn();
    }
  }, 1000);

  function installEsc(d) {
    var list = d.querySelector('.menu-list');
    if (!list || list.querySelector('.__sm_entry')) return;
    var anchor = list.querySelector('.btn-restart, .btn-exit');
    if (!anchor) return;
    var proto = anchor.previousElementSibling && !anchor.previousElementSibling.classList.contains('__sm_entry')
      ? anchor.previousElementSibling : anchor;
    var b = proto.cloneNode(true);
    b.classList.add('__sm_entry');
    b.setAttribute('data-interactive', '1');
    b.textContent = label();
    b.addEventListener('click', function () {
      if (window.__smSaving) return;
      if (!window.__smReady) { window.__smToast(docOf('OutSetting'), '刚进入游戏，稍候片刻即可存档'); return; }
      window.__smSaving = true;
      window.__smSavingSince = Date.now();
      window.__smRefreshBtn();
      window.__smOutbox.push('manualsave');
    });
    list.insertBefore(b, anchor);
  }
  function install() {
    var b = docOf('OutSetting'); if (b) installEsc(b);
  }
  function hookLoads() {
    var fs = document.querySelectorAll('#iframe-container iframe');
    for (var i = 0; i < fs.length; i++) {
      var f = fs[i];
      if (f.__smLoad) continue;
      f.__smLoad = 1;
      f.addEventListener('load', function () { setTimeout(install, 60); setTimeout(install, 700); setTimeout(install, 2000); });
    }
  }
  window.__smSelfCheck = function () {
    try {
      var b = docOf('OutSetting');
      if (b && b.querySelector('.menu-list') && !b.querySelector('.__sm_entry')) return false;
      return true;
    } catch (e) { return false; }
  };
  window.__smDrain = function () { var o = window.__smOutbox || []; window.__smOutbox = []; return JSON.stringify(o); };
  window.__smObserver = new MutationObserver(function () { hookLoads(); install(); });
  window.__smObserver.observe(document.body, { childList: true, subtree: true });
  hookLoads(); install();
})();
";

    private static readonly System.Collections.Generic.List<string> _pendingReg = new();
    private static string _pendingRegPath;
    private static bool _lastReady;
    private static bool _saving;

    /// <summary>Ticker 2s 拍：注册泵/就绪态 → 守卫自愈 → drain outbox。</summary>
    internal static void Tick()
    {
        try
        {
            TickSavepoint();

            var web = GetWebView();
            if (web == null) return;

            var guard = "window.__smObserver && window.__smSelfCheck && window.__smSelfCheck()";
            Exec(web, "if(!(" + guard + ")){" + EntryJs + "}");

            Action<string> cb = r => SaveMgrRuntime.Enqueue(() => Handle(r));
            var ok = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(cb);
            web.ExecuteJavaScript("window.__smDrain ? window.__smDrain() : '[]'", ok);
        }
        catch (Exception e) { SaveMgrPlugin.Trace("btnTick: " + e.Message); }
    }

    /// <summary>注册泵 + 就绪态：主菜单期补注册 pending 新档（AddHistoryEntry）；
    /// 战斗期推送 30s 保护期就绪态（按钮「准备中」→「立即存档」）。</summary>
    private static void TickSavepoint()
    {
        try
        {
            if (SaveEnv.AtMainMenu)
            {
                string[] batch = null;
                lock (_pendingReg) { if (_pendingReg.Count > 0) { batch = _pendingReg.ToArray(); _pendingReg.Clear(); SavePendingReg(); } }
                if (batch != null)
                    foreach (var file in batch)
                    {
                        var r = RestoreInjector.Register(file);
                        SaveMgrPlugin.Trace("新档注册" + (r ? "成功 " + file : "跳过 " + file));
                    }
            }
            var ready = SaveMgrPlugin.BattleReady();
            if (ready != _lastReady)
            {
                _lastReady = ready;
                ExecJs("window.__smSetReady && window.__smSetReady(" + (ready ? "true" : "false") + ")");
            }
        }
        catch (Exception e) { SaveMgrPlugin.Trace("regPump: " + e.Message); }
    }

    /// <summary>新档落成回调（SavepointService.onNewSavepoint）：身份重写 → pending 入册 →
    /// 复位按钮 → toast。file=null=持久化超时失败。</summary>
    internal static void OnNewSavepoint(string newFile)
    {
        try
        {
            if (string.IsNullOrEmpty(newFile))
            {
                SaveMgrPlugin.Trace("onNew: 持久化超时，手动存档失败");
                SaveDone();
                Toast("OutSetting", "存档失败：游戏未在期限内落盘（详见 SaveMgr_trace）");
                return;
            }
            if (!RestoreInjector.RewriteIdentity(newFile))
            {
                SaveMgrPlugin.Trace("onNew: 身份重写失败 → 删除地雷档 " + newFile);
                try { System.IO.File.Delete(System.IO.Path.Combine(SaveEnv.SavesDir() ?? "", newFile)); } catch { }
                SaveDone();
                Toast("OutSetting", "存档失败：身份重写未成（详见 SaveMgr_trace）");
                return;
            }
            lock (_pendingReg) _pendingReg.Add(newFile);
            SavePendingReg();
            SaveMgrPlugin.Trace("onNew: 新档就绪 " + newFile + "（主菜单时补注册进官方列表）");
            SaveDone();
            Toast("OutSetting", "已存档 · 下次到主菜单出现在读档列表");
        }
        catch (Exception e) { SaveMgrPlugin.Trace("onNew: " + e.Message); SaveDone(); }
    }

    private static void SaveDone()
    {
        _saving = false;
        ExecJs("window.__smSaveDone && window.__smSaveDone()");
    }

    /// <summary>启动补扫：上次会话未注册的新档（崩溃/未到菜单）重新入册。</summary>
    internal static void BootScanPending(string pendingPath)
    {
        try
        {
            _pendingRegPath = pendingPath;
            if (!System.IO.File.Exists(pendingPath)) return;
            foreach (var f in JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(pendingPath)) ?? new string[0])
                lock (_pendingReg) _pendingReg.Add(f);
            SaveMgrPlugin.Trace("bootScan: 补注册 " + _pendingReg.Count + " 个新档");
        }
        catch (Exception e) { SaveMgrPlugin.Trace("bootScan: " + e.Message); }
    }

    private static void SavePendingReg()
    {
        try
        {
            if (_pendingRegPath == null) return;
            lock (_pendingReg)
                System.IO.File.WriteAllText(_pendingRegPath, JsonSerializer.Serialize(_pendingReg.ToArray()));
        }
        catch { }
    }

    private static void Handle(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json) || json == "[]") return;
            string[] acts;
            try { acts = JsonSerializer.Deserialize<string[]>(json); }
            catch { return; }
            if (acts == null) return;
            foreach (var act in acts)
                HandleOne(act);
        }
        catch (Exception e) { SaveMgrPlugin.Trace("btnHandle: " + e.Message); }
    }

    private static void HandleOne(string act)
    {
        try
        {
            if (act == "manualsave")
            {
                if (_saving) return;                                   // 与按钮侧双保险
                _saving = true;
                SaveMgrPlugin.Trace("按钮：立即存档（原地新档）");
                var err = SaveMgrPlugin.Service?.ManualSaveNow();
                if (err != null)
                {
                    SaveMgrPlugin.Trace("manual 拒绝：" + err);
                    SaveDone();
                    Toast("OutSetting", "存档失败：" + err);
                }
            }
        }
        catch (Exception e) { SaveMgrPlugin.Trace("btnAct: " + e.Message); }
    }

    /// <summary>页内 toast（经 Root realm 的 __smToast/docOf）。</summary>
    internal static void Toast(string pageTail, string msg)
    {
        try
        {
            var web = GetWebView();
            if (web == null) return;
            var js = "window.__smToast && window.__smDoc && window.__smToast(window.__smDoc('" + pageTail + "'), '" +
                     msg.Replace("'", "\\'") + "')";
            Exec(web, js);
        }
        catch (Exception e) { SaveMgrPlugin.Trace("toast: " + e.Message); }
    }

    private static void ExecJs(string js)
    {
        var web = GetWebView();
        if (web != null) Exec(web, js);
    }

    private static Vuplex.WebView.IWebView GetWebView()
    {
        try
        {
            if (!GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.IsInstanceCreated)
                return null;
            var rus = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance;
            var layer = rus?.GetWebUILayer();
            var prefab = layer?.canvasWebViewPrefab;
            return prefab?.WebView;
        }
        catch { return null; }
    }

    private static void Exec(Vuplex.WebView.IWebView web, string js)
    {
        try { web.ExecuteJavaScript(js); }
        catch (Exception e) { SaveMgrPlugin.Trace("exec: " + e.Message); }
    }
}
