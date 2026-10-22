/*!
 * CXIM 内嵌挂件（embed.js）
 * 在宿主系统页面右下角显示聊天图标，点击展开/收起聊天面板（iframe）。
 * 无第三方依赖，兼容 IE11+ / 现代浏览器。
 *
 * 用法：
 * <script src="https://chat.example.com/embed.js"
 *         data-base="https://chat.example.com"
 *         data-ticket-url="/api/chat/sso-url"
 *         data-width="380" data-height="600"
 *         data-title="在线聊天"></script>
 *
 * 可选属性：
 *   data-base       聊天系统根地址；省略时按脚本自身地址推断
 *   data-ticket-url 宿主后端接口，返回当前登录用户的 SSO 跳转地址（JSON {url} 或纯文本）
 *   data-width/height 面板尺寸，默认 380 × 600（自动收缩到视口内）
 *   data-open       为 true 时页面加载后自动展开
 *   data-badge      未读消息提醒，默认 true
 */
(function () {
  'use strict';

  var script = document.currentScript || (function () {
    var list = document.getElementsByTagName('script');
    return list[list.length - 1];
  })();

  function attr(name, def) {
    var v = script && script.getAttribute('data-' + name);
    return v === null || v === undefined || v === '' ? def : v;
  }

  // 聊天系统根地址：优先 data-base，其次从脚本地址推断
  var base = String(attr('base', '')).replace(/\/+$/, '');
  if (!base) {
    try {
      var u = new URL(script.src);
      base = u.origin;
    } catch (e) {
      base = location.protocol + '//' + location.host;
    }
  }

  var ticketUrl = attr('ticket-url', '');
  var width = parseInt(attr('width', '380'), 10) || 380;
  var height = parseInt(attr('height', '600'), 10) || 600;
  var title = attr('title', '在线聊天');
  var autoOpen = String(attr('open', 'false')) === 'true';
  var badgeEnabled = String(attr('badge', 'true')) !== 'false';

  var panel = null;      // 面板容器
  var iframe = null;     // 聊天 iframe
  var launcher = null;   // 右下角图标
  var badge = null;      // 未读角标
  var loaded = false;    // iframe 是否已加载
  var opened = false;    // 面板是否展开
  var unread = 0;

  var CSS = [
    '#cxim-embed-launcher{position:fixed;right:22px;bottom:22px;width:56px;height:56px;border-radius:50%;',
    'background:linear-gradient(135deg,#1E9FFF,#0C7BD4);color:#fff;border:0;cursor:pointer;z-index:2147483000;',
    'box-shadow:0 6px 18px rgba(12,123,212,.45);display:flex;align-items:center;justify-content:center;',
    'transition:transform .15s ease, box-shadow .15s ease;}',
    '#cxim-embed-launcher:hover{transform:translateY(-2px);box-shadow:0 10px 22px rgba(12,123,212,.55);}',
    '#cxim-embed-launcher svg{width:28px;height:28px;fill:#fff;}',
    '#cxim-embed-badge{position:absolute;top:-2px;right:-2px;min-width:18px;height:18px;padding:0 5px;',
    'border-radius:9px;background:#FF5722;color:#fff;font:12px/18px Arial,sans-serif;text-align:center;',
    'box-shadow:0 1px 3px rgba(0,0,0,.3);display:none;}',
    '#cxim-embed-panel{position:fixed;right:22px;bottom:90px;width:' + width + 'px;height:' + height + 'px;',
    'max-width:calc(100vw - 24px);max-height:calc(100vh - 120px);background:#fff;border-radius:10px;',
    'box-shadow:0 12px 40px rgba(0,0,0,.28);overflow:hidden;z-index:2147483000;display:none;}',
    '#cxim-embed-panel.cxim-open{display:block;animation:cxim-in .18s ease;}',
    '#cxim-embed-panel iframe{display:block;width:100%;height:100%;border:0;background:#fff;}',
    '@keyframes cxim-in{from{opacity:0;transform:translateY(10px);}to{opacity:1;transform:translateY(0);}}',
    '@media (max-width:768px){#cxim-embed-panel{right:8px;left:8px;bottom:88px;width:auto;height:calc(100vh - 120px);}',
    '#cxim-embed-launcher{right:16px;bottom:16px;}}'
  ].join('');

  function injectStyle() {
    if (document.getElementById('cxim-embed-style')) return;
    var style = document.createElement('style');
    style.id = 'cxim-embed-style';
    style.type = 'text/css';
    style.appendChild(document.createTextNode(CSS));
    (document.head || document.documentElement).appendChild(style);
  }

  function createLauncher() {
    launcher = document.createElement('button');
    launcher.id = 'cxim-embed-launcher';
    launcher.type = 'button';
    launcher.title = title;
    launcher.setAttribute('aria-label', title);
    launcher.innerHTML =
      '<svg viewBox="0 0 24 24"><path d="M12 3C6.5 3 2 6.9 2 11.7c0 2.6 1.3 4.9 3.4 6.5-.1 1.1-.6 2.4-1.6 3.5 1.9-.1 3.4-.8 4.5-1.6 1.2.3 2.4.5 3.7.5 5.5 0 10-3.9 10-8.9S17.5 3 12 3zm-4.6 9.6a1.3 1.3 0 1 1 0-2.6 1.3 1.3 0 0 1 0 2.6zm4.6 0a1.3 1.3 0 1 1 0-2.6 1.3 1.3 0 0 1 0 2.6zm4.6 0a1.3 1.3 0 1 1 0-2.6 1.3 1.3 0 0 1 0 2.6z"/></svg>' +
      '<span id="cxim-embed-badge"></span>';
    launcher.addEventListener('click', function () { toggle(); }, false);
    document.body.appendChild(launcher);
    badge = document.getElementById('cxim-embed-badge');
  }

  function createPanel() {
    panel = document.createElement('div');
    panel.id = 'cxim-embed-panel';
    iframe = document.createElement('iframe');
    iframe.setAttribute('title', title);
    iframe.setAttribute('allow', 'microphone; camera; clipboard-write');
    iframe.setAttribute('allowtransparency', 'true');
    panel.appendChild(iframe);
    document.body.appendChild(panel);
  }

  // 计算 iframe 地址：优先通过宿主后端换取带 ticket 的 SSO 地址
  function resolveSrc() {
    // 加时间戳强制加载最新页面，避免浏览器沿用旧的前端静态资源
    var stamp = '_v=' + Date.now();
    if (!ticketUrl) {
      return Promise.resolve(base + '/index.html?embed=1&' + stamp);
    }
    var url = ticketUrl + (ticketUrl.indexOf('?') >= 0 ? '&' : '?') + '_t=' + Date.now();
    return fetch(url, { credentials: 'include', headers: { 'Accept': 'application/json, text/plain' } })
      .then(function (r) { return r.text(); })
      .then(function (text) {
        var sso = '';
        try {
          var json = JSON.parse(text);
          sso = json.url || (json.data && (json.data.url || json.data)) || '';
        } catch (e) {
          sso = String(text).trim();
        }
        if (!sso) throw new Error('empty sso url');
        return sso + (sso.indexOf('?') >= 0 ? '&' : '?') + 'embed=1&' + stamp;
      })
      .catch(function () {
        // 换取失败时回退为普通页面（依赖已有登录态或展示登录框）
        return base + '/index.html?embed=1&' + stamp;
      });
  }

  function open() {
    if (!panel) createPanel();
    if (!loaded) {
      loaded = true;
      resolveSrc().then(function (src) {
        iframe.src = src;
      });
    }
    panel.className = 'cxim-open';
    opened = true;
    clearUnread();
  }

  function close() {
    if (panel) panel.className = '';
    opened = false;
  }

  function toggle() {
    opened ? close() : open();
  }

  function clearUnread() {
    unread = 0;
    renderBadge();
  }

  function renderBadge() {
    if (!badge || !badgeEnabled) return;
    badge.textContent = unread > 99 ? '99+' : String(unread);
    badge.style.display = unread > 0 ? 'block' : 'none';
  }

  // 接收聊天面板（iframe）的事件
  window.addEventListener('message', function (e) {
    if (!iframe || e.source !== iframe.contentWindow) return;
    var d = e.data || {};
    if (d.source !== 'cxim') return;

    if (d.type === 'cxim:message') {
      if (!opened) {
        unread++;
        renderBadge();
      }
    } else if (d.type === 'cxim:logout') {
      close();
      loaded = false;
      if (iframe) iframe.src = 'about:blank';
      clearUnread();
    }
  }, false);

  injectStyle();
  if (document.body) {
    createLauncher();
  } else {
    document.addEventListener('DOMContentLoaded', createLauncher, false);
  }

  if (autoOpen) {
    if (document.readyState === 'complete') open();
    else window.addEventListener('load', open, false);
  }

  // 宿主页面可直接调用：CXIM.open() / CXIM.close() / CXIM.toggle()
  window.CXIM = {
    open: open,
    close: close,
    toggle: toggle
  };
})();
