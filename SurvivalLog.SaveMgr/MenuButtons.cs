using System;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>游戏内实体按钮层（2026-11-01 作者定案终版）：局内 ESC 暂停菜单（OutSetting）两个官方样式
/// 菜单项——「立即存档」「读取存档」。
/// - 立即存档 → BackupService.ManualBackupNow（SaveGame 强制持久化当前进度→主档 mtime 越基线→
///   **后台线程**字节级抄档（主线程零 IO 卡顿）→ 主线程回调 toast）；
/// - 读取存档 → **原地弹出存档列表浮层**（官方风卡片，数据=Gsm.GetHistoryList 同原生过滤）→ 手点某档
///   → 先存当前进度 → MainAction.Entrance.EnterBattle(fileName, isLoading:true)（官方读档 API 直切，
///   不退主菜单）。
/// 机制照抄 ModCenter MenuEntryInjector 实证模式：Root MutationObserver + iframe load 钩幂等安装；
/// [data-interactive] 吃 clickThrough；点击只投 outbox，C# 2s 拍 drain 主线程执行。</summary>
internal static class MenuButtons
{
    /// <summary>Root 引导脚本（幂等）。</summary>
    private const string EntryJs = @"
(function () {
  if (window.__smObserver) return;
  window.__smOutbox = [];
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
  function fire(el) {
    ['pointerdown','mousedown','pointerup','mouseup','click'].forEach(function (ev) {
      el.dispatchEvent(new MouseEvent(ev, { bubbles: true, cancelable: true }));
    });
  }
  window.__smFire = fire;
  function clickBackToGame() {
    try {
      var d = docOf('OutSetting');
      var back = d && Array.from(d.querySelectorAll('.menu-list *')).filter(function (e) {
        return e.childElementCount === 0 && e.textContent.trim() === '返回游戏';
      })[0];
      if (back) fire(back);
    } catch (e) {}
  }

  // ESC 暂停菜单：官方样式菜单项×2，插在「保存并退出」（.btn-restart/.btn-exit）前。
  function installEsc(d) {
    var list = d.querySelector('.menu-list');
    if (!list || list.querySelector('.__sm_entry')) return;
    var anchor = list.querySelector('.btn-restart, .btn-exit');
    if (!anchor) return;
    var proto = anchor.previousElementSibling && !anchor.previousElementSibling.classList.contains('__sm_entry')
      ? anchor.previousElementSibling : anchor;
    ['立即存档', '读取存档'].forEach(function (label) {
      var b = proto.cloneNode(true);
      b.classList.add('__sm_entry');
      b.setAttribute('data-interactive', '1');
      b.textContent = label;
      b.addEventListener('click', function () {
        if (label === '读取存档') { window.__smOutbox.push('loadsave'); return; }
        window.__smSavepointClick();
      });
      list.insertBefore(b, anchor);
    });
  }

  // 存档列表浮层（暂停菜单之上；官方风卡片，手选读档）
  window.__smShowList = function (rowsJson) {
    var d = docOf('OutSetting');
    if (!d || !d.body) return;
    var old = d.getElementById('__sm_list');
    if (old) old.remove();
    var rows = [];
    try { rows = JSON.parse(rowsJson); } catch (e) {}
    var wrap = d.createElement('div');
    wrap.id = '__sm_list';
    wrap.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:rgba(8,10,13,.82);' +
      'display:flex;flex-direction:column;align-items:center;justify-content:center;';
    var card = d.createElement('div');
    card.style.cssText = 'position:relative;width:min(86%,420px);max-height:76vh;overflow:auto;' +
      'background:rgba(18,20,26,.97);border:1px solid rgba(242,239,230,.22);border-radius:14px;padding:14px;';
    var title = d.createElement('div');
    title.textContent = '存档列表 · 点击读取';
    title.style.cssText = 'color:#f2efe6;font-size:16px;letter-spacing:3px;text-align:center;padding:4px 0 10px;';
    card.appendChild(title);
    var x = d.createElement('button');
    x.type = 'button'; x.setAttribute('data-interactive', '1'); x.textContent = '×';
    x.style.cssText = 'all:unset;cursor:pointer;position:absolute;top:8px;right:12px;color:#f2efe6;' +
      'font-size:20px;padding:4px 10px;pointer-events:auto;';
    x.addEventListener('click', function () { wrap.remove(); });
    card.appendChild(x);
    if (!rows.length) {
      var empty = d.createElement('div');
      empty.textContent = '没有可读取的存档';
      empty.style.cssText = 'color:#9aa0a8;text-align:center;padding:18px 0;font-size:14px;';
      card.appendChild(empty);
    }
    rows.forEach(function (r) {
      var it = d.createElement('button');
      it.type = 'button';
      it.setAttribute('data-interactive', '1');
      it.style.cssText = 'all:unset;box-sizing:border-box;display:block;width:100%;cursor:pointer;' +
        'margin:8px 0;padding:10px 14px;background:rgba(30,33,40,.9);' +
        'border:1px solid rgba(242,239,230,.15);border-radius:10px;color:#f2efe6;' +
        'text-align:left;pointer-events:auto;';
      var l1 = d.createElement('div');
      l1.textContent = '第 ' + r.d + ' 天 · ' + r.p;
      l1.style.cssText = 'font-size:15px;letter-spacing:1px;';
      var l2 = d.createElement('div');
      l2.textContent = r.t;
      l2.style.cssText = 'color:#9aa0a8;font-size:12px;margin-top:3px;letter-spacing:1px;';
      it.appendChild(l1); it.appendChild(l2);
      it.addEventListener('mouseenter', function () { it.style.background = 'rgba(48,52,62,.95)'; });
      it.addEventListener('mouseleave', function () { it.style.background = 'rgba(30,33,40,.9)'; });
      it.addEventListener('click', function () {
        wrap.remove();
        clickBackToGame();                                   // 收暂停菜单（防挡场景切换）
        setTimeout(function () { window.__smOutbox.push('enter:' + r.f); }, 400);
      });
      card.appendChild(it);
    });
    wrap.appendChild(card);
    wrap.addEventListener('click', function (e) { if (e.target === wrap) wrap.remove(); });
    d.body.appendChild(wrap);
  };

  // 立即存档 = 点官方「保存并退出」（.btn-exit 安全点持久化+回主菜单）+ 通知 C# 菜单期接力
  // （注入存档点+自动重进）。2026-11-01 实锤：任意时刻 Gsm.SaveGame() 会 native 崩（进档初期），
  // 官方按钮是唯一安全的手动持久化路径。找不到按钮（菜单未开）→ C# 兜底抄最近主档。
  window.__smSavepointClick = function () {
    var d = docOf('OutSetting');
    var exit = d && d.querySelector('.menu-list .btn-exit');
    if (exit) {
      window.__smOutbox.push('savepoint:' + (window.__smCurFile || ''));
      fire(exit);
      return true;
    }
    window.__smOutbox.push('backup');
    return false;
  };

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

    private static bool _spArmed;
    private static string _spFile;
    private static int _spTicks;
    private static readonly System.Collections.Generic.List<(string path, int day)> _autoQueue = new();

    /// <summary>自动存档点入队（SavepointService.onStaged 回调；崩溃残留由 BootScanStaging 补扫）。</summary>
    internal static void EnqueueAutoSavepoint(string stagedPath, int day)
    {
        lock (_autoQueue) _autoQueue.Add((stagedPath, day));
    }

    /// <summary>启动补扫：上次会话暂存未注册的存档点（崩溃/未到菜单）重新入队。</summary>
    internal static void BootScanStaging(string stagingDir)
    {
        try
        {
            if (!System.IO.Directory.Exists(stagingDir)) return;
            foreach (var f in System.IO.Directory.GetFiles(stagingDir, "D*.bytes"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                var day = name.Length > 1 && name[0] == 'D' && int.TryParse(name.Substring(1, name.IndexOf('_') - 1), out var d) ? d : 0;
                EnqueueAutoSavepoint(f, day);
            }
        }
        catch (System.Exception e) { SaveMgrPlugin.Trace("bootScan: " + e.Message); }
    }

    /// <summary>存档点接力泵（Tick 每拍）：
    /// ①手动（官方保存退出）→ 主菜单落地缓冲 2 拍 → 注入主档副本 → EnterBattle 自动重进；
    /// ②自动（结算暂存）→ 主菜单落地即静默注册（不打扰，不重进），注册后删暂存。</summary>
    private static void TickSavepoint()
    {
        try
        {
            // ②自动队列：菜单期逐个注册
            if (SaveEnv.AtMainMenu)
            {
                (string path, int day)[] batch = null;
                lock (_autoQueue) { if (_autoQueue.Count > 0) { batch = _autoQueue.ToArray(); _autoQueue.Clear(); } }
                if (batch != null)
                    foreach (var (path, day) in batch)
                    {
                        var inj = RestoreInjector.InjectFile(path);
                        SaveMgrPlugin.Trace("自动存档点注册" + (inj.Ok ? "成功 → " + inj.Reason : "失败：" + inj.Reason));
                        if (inj.Ok) { try { System.IO.File.Delete(path); } catch { } SaveListPanel.RefreshCache(); }
                    }
            }
            if (!_spArmed) return;
            if (!SaveEnv.AtMainMenu) { _spTicks = 0; return; }   // 还没到菜单（读条中）
            if (++_spTicks < 2) return;                          // 菜单缓冲 2 拍
            _spArmed = false;
            var file = _spFile;
            _spFile = null;
            if (string.IsNullOrEmpty(file)) { SaveMgrPlugin.Trace("存档点：原档名缺失，放弃"); return; }
            var savesDir = SaveEnv.SavesDir();
            var src = savesDir == null ? null : System.IO.Path.Combine(savesDir,
                file.EndsWith(".bytes") ? file : file + ".bytes");
            if (src == null || !System.IO.File.Exists(src)) { SaveMgrPlugin.Trace("存档点：主档缺失"); return; }
            var r = RestoreInjector.InjectFile(src);
            SaveMgrPlugin.Trace("存档点注入" + (r.Ok ? "成功 → " + r.Reason : "失败：" + r.Reason));
            SaveListPanel.RefreshCache();
            if (!r.Ok) return;
            Toast("MainUI", "已存档 · 读档列表顶部（自动回到游戏）");
            var ma = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.MainAction>.Instance;
            var ent = ma == null ? null : ma.Entrance;
            if (ent != null) ent.EnterBattle(file, true, false);  // 重进原档继续玩
            else SaveMgrPlugin.Trace("存档点：Entrance 未就绪，不自动重进");
        }
        catch (System.Exception e) { _spArmed = false; SaveMgrPlugin.Trace("savepoint: " + e.Message); }
    }

    /// <summary>Ticker 2s 拍：存档点接力泵 → 守卫自愈 → drain outbox → 主线程执行动作。</summary>
    internal static void Tick()
    {
        try
        {
            TickSavepoint();
            SaveListPanel.RefreshCache();                           // 主菜单期刷新列表快照（局内读档用；注入后再刷，新存档点即入快照）

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

    /// <summary>F9/cmd 触发：经 JS 走官方「保存并退出」（菜单未开时 JS 自动投兜底 backup）。</summary>
    internal static void TriggerSavepoint()
    {
        ExecJs("window.__smSavepointClick && window.__smSavepointClick()");
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
            if (act == "backup")                        // .btn-exit 不在场（未开暂停菜单）：提示引导
            {
                SaveMgrPlugin.Trace("按钮：立即存档（未开暂停菜单，引导提示）");
                Toast("CoreUI1", "请先按 ESC 打开暂停菜单，再点「立即存档」");
            }
            else if (act != null && act.StartsWith("savepoint:", System.StringComparison.Ordinal))
            {
                // 官方「保存并退出」已被点击：菜单落地后注入存档点+自动重进（TickSavepoint 泵）
                _spArmed = true;
                _spFile = act["savepoint:".Length..];
                _spTicks = 0;
                SaveMgrPlugin.Trace("存档点武装（等主菜单接力）");
            }
            else if (act == "loadsave")
            {
                SaveMgrPlugin.Trace("按钮：读取存档 → 原地弹存档列表");
                var json = SaveListPanel.BuildListJson();
                ExecJs("window.__smShowList && window.__smShowList(" + json + ")");
            }
            else if (act != null && act.StartsWith("enter:", System.StringComparison.Ordinal))
            {
                SaveListPanel.EnterSave(act["enter:".Length..]);
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
