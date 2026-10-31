using System;
using System.Text.Json;

namespace SurvivalLog.SaveMgr;

/// <summary>游戏内实体按钮层（2026-10-31 作者四次反馈定案）：**只在局内 ESC 暂停菜单（OutSetting）**
/// 加两个官方样式菜单项——「立即存档」「读取存档」，插在「保存并退出」前。主菜单不加按钮；
/// 不做 HUD 悬浮按钮（作者竖屏窗口实测找不到且挡画面）。
/// - 立即存档 → BackupService.ManualBackupNow（先 SaveGame 强制持久化当前进度→落盘→抄，Tick 状态机
///   非阻塞）→ 完成回调 toast；
/// - 读取存档 → MainAction.Entrance.BattleToMain()（官方存档+回主菜单全流程）→ MainUI 渲染后
///   自动点官方「读取存档」→ 官方存档列表弹出（回档=官方列表里挑注入档）。
/// 机制照抄 ModCenter MenuEntryInjector 实证模式：Root MutationObserver + iframe load 钩幂等安装；
/// [data-interactive] 才能吃到 clickThrough 鼠标；点击只投 outbox，C# 2s 拍 drain 主线程执行；
/// __sm* 命名空间独立于 __mc*。</summary>
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

  // ESC 暂停菜单：官方样式菜单项×2，插在「保存并退出」（.btn-restart/.btn-exit）前。
  // 原型取锚点前一个兄弟（普通菜单项；cloneNode 不带事件监听，安全）；
  // 绝不插进 .btn-restart + .btn-exit 相邻对之间（ModCenter ANCHORS 同规）。
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
        window.__smOutbox.push(label === '立即存档' ? 'backup' : 'loadsave');
      });
      list.insertBefore(b, anchor);
    });
  }
  // 读取存档接力：BattleToMain 落回主菜单后自动点官方「读取存档」菜单项
  function autoOpenLoadList() {
    var d = docOf('MainUI');
    if (!d || !window.__smAutoLoad) return;
    var items = d.querySelectorAll('#app .container .menu .menu-item');
    for (var i = 0; i < items.length; i++) {
      var t = items[i].querySelector('.menu-text');
      if (t && t.textContent.trim() === '读取存档') {
        window.__smAutoLoad = false;
        try { t.closest('.menu-item').click(); } catch (e) { t.click(); }
        return;
      }
    }
  }
  function install() {
    var b = docOf('OutSetting'); if (b) installEsc(b);
    autoOpenLoadList();
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

    /// <summary>Ticker 2s 拍：守卫自愈（失守重发引导）→ drain outbox → 主线程执行动作 → toast 反馈。</summary>
    internal static void Tick()
    {
        try
        {
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
            if (act == "backup")
            {
                SaveMgrPlugin.Trace("按钮：立即存档（先持久化再抄）");
                var svc = SaveMgrPlugin.Service;
                if (svc == null) { Toast("OutSetting", "存档失败：服务未就绪"); return; }
                svc.ManualBackupNow();                    // 完成/失败经 OnManualDone → toast（插件 Load 订阅）
            }
            else if (act == "loadsave")
            {
                SaveMgrPlugin.Trace("按钮：读取存档 → 回主菜单 + 自动开官方列表");
                // GameSaveManager.BattleToMain 只做存档侧（实机实证）；场景切换在 GameEntrance.BattleToMain
                var ma = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.MainAction>.Instance;
                var entrance = ma == null ? null : ma.Entrance;
                if (entrance == null)
                {
                    SaveMgrPlugin.Trace("按钮：读取存档失败——MainAction.Entrance 未就绪");
                    Toast("OutSetting", "读取存档失败：场景入口未就绪");
                    return;
                }
                ExecJs("window.__smAutoLoad = true;");
                entrance.BattleToMain();                  // 官方 存档+回主菜单 全流程
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
