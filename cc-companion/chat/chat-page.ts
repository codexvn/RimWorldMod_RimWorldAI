/**
 * 聊天页面 HTML 生成 — 单文件自包含，内联 CSS + JS
 * 终端 / 面板风格 UI
 */

export interface ChatPageConfig {
  token: string;
  modelName: string;
  projectPath: string;
}

export function getChatPageHtml(config: ChatPageConfig): string {
  const escapedToken = JSON.stringify(config.token);
  const modelName = escHtml(config.modelName);
  const projectPath = escHtml(config.projectPath);

  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>RimWorld Bridge Agent</title>
<style>
  /* ===== CSS Variables ===== */
  :root {
    --bg: #0d0f14;
    --card: #151820;
    --surface: #1a1e2a;
    --text: #e4e4e7;
    --text-strong: #fafafa;
    --muted: #71717a;
    --border: #27272a;
    --accent: #ff5c5c;
    --blue: #3b82f6;
    --green: #22c55e;
    --amber: #f59e0b;
    --red: #ef4444;
    --cyan: #22d3ee;
    --mono: "Cascadia Code","Fira Code","Consolas",monospace;
    --font: system-ui,-apple-system,"Microsoft YaHei","Segoe UI",sans-serif;
    --radius: 6px;
    color-scheme: dark;
  }

  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { height: 100%; overflow: hidden; background: var(--bg); }
  body {
    font-family: var(--font); color: var(--text);
    display: flex; flex-direction: column;
  }
  #app {
    display: flex; flex-direction: column;
    height: 100vh; max-width: 900px; margin: 0 auto; width: 100%;
  }

  ::-webkit-scrollbar { width: 6px; }
  ::-webkit-scrollbar-track { background: transparent; }
  ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }

  /* ===== Top Header ===== */
  #top-bar {
    display: flex; align-items: center; gap: 12px;
    padding: 10px 16px; background: var(--card);
    border-bottom: 1px solid var(--border);
    flex-shrink: 0;
  }
  #top-bar h1 { font-size: 14px; font-weight: 600; color: var(--text-strong); font-family: var(--mono); }
  #top-bar .colony-info { margin-left: auto; font-size: 12px; color: var(--muted); font-family: var(--mono); }
  #status-dot {
    width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0;
    transition: background 0.3s, box-shadow 0.3s;
  }
  #status-dot.connected { background: var(--green); box-shadow: 0 0 6px rgba(34,197,94,0.5); }
  #status-dot.connecting { background: var(--amber); animation: blink 1s infinite; }
  #status-dot.disconnected { background: var(--red); }
  @keyframes blink { 0%,100% { opacity: 0.4; } 50% { opacity: 1; } }

  #info-btn {
    background: none; border: 1px solid var(--border); color: var(--muted);
    border-radius: 3px; padding: 1px 6px; cursor: pointer;
    font-size: 12px; font-family: var(--mono); transition: background 0.2s;
  }
  #info-btn:hover { background: var(--surface); color: var(--text); }

  /* ===== Stats Bar ===== */
  #stats-bar {
    display: flex; align-items: center; gap: 0;
    padding: 6px 16px; background: var(--card);
    border-bottom: 1px solid var(--border);
    flex-shrink: 0; font-family: var(--mono); font-size: 12px;
    min-height: 32px; display: none;
  }
  #stats-bar .stat + .stat::before {
    content: "|"; margin: 0 10px; color: var(--border); opacity: 0.5;
  }
  #stats-bar .stat-label { color: var(--muted); margin-right: 4px; }
  #stats-bar .stat-value { color: var(--text); }
  #stats-bar .stat-value.danger { color: var(--red); }
  #stats-bar .stat-value.warning { color: var(--amber); }
  #stats-bar .stat-value.ok { color: var(--green); }

  /* ===== Info Overlay ===== */
  #info-overlay {
    display: none; position: fixed; top: 0; left: 0; width: 100%; height: 100%;
    z-index: 100; background: rgba(0,0,0,0.5);
  }
  #info-panel {
    position: absolute; top: 52px; left: 16px; right: 16px;
    background: var(--card); border: 1px solid var(--border);
    border-radius: var(--radius); padding: 14px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.4);
    max-width: 600px;
  }
  #info-close {
    float: right; background: none; border: none; color: var(--muted);
    cursor: pointer; font-size: 16px; line-height: 1;
  }
  #info-close:hover { color: var(--red); }
  #info-panel h2 { font-size: 13px; color: var(--cyan); margin-bottom: 8px; }
  .info-table { width: 100%; border-collapse: collapse; font-size: 12px; }
  .info-table th, .info-table td {
    text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--border);
  }
  .info-table th { color: var(--muted); width: 70px; font-weight: 400; }
  .info-table td { color: var(--text); word-break: break-all; font-family: var(--mono); }

  /* ===== Message Thread ===== */
  #messages {
    flex: 1; overflow-y: auto; padding: 12px;
    display: flex; flex-direction: column; gap: 6px;
    scroll-behavior: smooth;
  }

  /* ===== Panels ===== */
  .chat-panel {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--card);
    overflow: hidden;
    animation: panel-in 180ms ease-out both;
  }
  @keyframes panel-in {
    from { opacity: 0; transform: translateY(3px); }
    to { opacity: 1; transform: translateY(0); }
  }

  /* USER panels — blue left accent */
  .chat-panel.user {
    border-left: 3px solid var(--blue);
    margin-left: 24px;
  }
  .chat-panel.user .panel-header {
    background: rgba(59,130,246,0.08);
    color: var(--blue);
  }

  /* AGENT panels — green left accent */
  .chat-panel.agent {
    border-left: 3px solid var(--green);
    margin-right: 24px;
  }
  .chat-panel.agent .panel-header {
    background: rgba(34,197,94,0.08);
    color: var(--green);
  }

  /* Sub-agent panels — cyan left accent */
  .chat-panel.agent.sub-agent {
    border-left-color: var(--cyan);
  }
  .chat-panel.agent.sub-agent .panel-header {
    background: rgba(34,211,238,0.08);
    color: var(--cyan);
  }

  /* TOOL panels — amber left accent */
  .chat-panel.tool {
    border-left: 3px solid var(--amber);
    margin: 0 24px;
    border-style: dashed;
  }
  .chat-panel.tool .panel-header {
    background: rgba(245,158,11,0.06);
    color: var(--amber);
  }
  .chat-panel.tool .panel-header .tool-timing {
    margin-left: auto; font-size: 11px; color: var(--green);
  }
  .chat-panel.tool .panel-header .tool-timing.error { color: var(--red); }
  .chat-panel.tool .panel-header .tool-name { font-weight: 600; font-family: var(--mono); font-size: 13px; }
  .chat-panel.tool .panel-body { padding: 6px 10px; font-family: var(--mono); font-size: 12px; }

  .panel-header {
    display: flex; align-items: center; gap: 6px;
    padding: 5px 10px; font-size: 11px; font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.5px;
    border-bottom: 1px solid var(--border);
    user-select: none;
  }
  .panel-header .role-tag {
    font-family: var(--mono); text-transform: uppercase;
  }
  .panel-body {
    padding: 8px 10px; font-size: 14px; line-height: 1.6;
    word-wrap: break-word; white-space: pre-wrap;
  }

  /* ===== Tool Output ===== */
  .tool-output { padding: 4px 0; }
  .tool-output .kv { display: flex; gap: 8px; }
  .tool-output .kv-key { color: var(--muted); min-width: 80px; }
  .tool-output .kv-val { color: var(--text); }
  .tool-output .list-item { padding-left: 8px; }
  .tool-output .list-item::before { content: "\\2022"; color: var(--muted); margin-right: 6px; }
  .tool-output .section-title { color: var(--amber); font-weight: 600; margin: 4px 0 2px; }

  .tool-output-wrap {
    max-height: 200px; overflow: hidden; position: relative;
    transition: max-height 0.2s ease;
  }
  .tool-output-wrap.expanded { max-height: none; }
  .tool-output-wrap.collapsed::after {
    content: ""; position: absolute; bottom: 0; left: 0; right: 0;
    height: 24px;
    background: linear-gradient(to bottom, transparent, var(--card));
    pointer-events: none;
  }
  .tool-expand-btn {
    display: block; width: 100%; padding: 3px;
    background: var(--surface); border: none; border-top: 1px solid var(--border);
    color: var(--muted); font-size: 11px; cursor: pointer;
    font-family: var(--mono); transition: color 0.15s;
  }
  .tool-expand-btn:hover { color: var(--text); background: var(--card); }

  /* ===== Divider (result) ===== */
  .chat-divider {
    display: flex; align-items: center; gap: 8px;
    margin: 4px 16px;
  }
  .chat-divider__line { flex: 1; height: 1px; background: var(--border); }
  .chat-divider__label {
    padding: 2px 10px; border-radius: 3px;
    border: 1px solid var(--border);
    font-size: 11px; color: var(--muted); font-family: var(--mono);
    user-select: none; white-space: nowrap;
  }
  .chat-divider__label.ok { border-color: rgba(34,197,94,0.3); color: var(--green); }
  .chat-divider__label.error { border-color: rgba(239,68,68,0.3); color: var(--red); }
  .chat-divider__label.warn { border-color: rgba(245,158,11,0.3); color: var(--amber); }

  /* ===== System message ===== */
  .chat-system {
    text-align: center; font-size: 12px; color: var(--muted);
    padding: 4px 8px; font-family: var(--mono); user-select: none;
  }

  /* ===== Reading Indicator (terminal style) ===== */
  .chat-reading {
    display: flex; align-items: center;
    padding: 8px 10px; margin: 0 24px; gap: 4px;
    font-family: var(--mono); font-size: 14px; color: var(--green);
  }
  .chat-reading .prompt { color: var(--green); }
  .chat-reading .cursor {
    display: inline-block; width: 8px; height: 16px;
    background: var(--green);
    animation: cursor-blink 1s step-end infinite;
    vertical-align: text-bottom;
  }
  @keyframes cursor-blink { 0%,100% { opacity: 1; } 50% { opacity: 0; } }

  /* ===== New Messages Pill ===== */
  #new-msg-pill {
    display: none; align-self: center;
    align-items: center; gap: 4px;
    padding: 4px 12px; font-size: 11px; cursor: pointer;
    color: var(--muted); background: var(--card);
    border: 1px solid var(--border); border-radius: 3px;
    margin: 2px auto; font-family: var(--mono);
    transition: border-color 0.2s; z-index: 10;
  }
  #new-msg-pill:hover { border-color: var(--text); color: var(--text); }

  /* ===== Compose Area ===== */
  .chat-compose {
    flex-shrink: 0; display: flex; align-items: center;
    padding: 8px 12px; background: var(--card);
    border-top: 1px solid var(--border); gap: 0;
  }
  .chat-compose .prompt {
    font-family: var(--mono); font-size: 14px; color: var(--green);
    margin-right: 6px; user-select: none; flex-shrink: 0;
  }
  #chat-input {
    flex: 1; background: none; border: none; outline: none;
    color: var(--text); font-family: var(--mono); font-size: 14px;
    line-height: 1.5; padding: 4px 0;
    min-height: 24px; max-height: 150px; resize: none;
  }
  #chat-input::placeholder { color: var(--muted); }
  #send-btn {
    background: var(--surface); border: 1px solid var(--border);
    border-radius: 3px; color: var(--muted); cursor: pointer;
    padding: 4px 10px; font-size: 12px; font-family: var(--mono);
    transition: background 0.15s, color 0.15s;
    flex-shrink: 0;
  }
  #send-btn:hover:not(:disabled) { background: var(--border); color: var(--text); }
  #send-btn:disabled { opacity: 0.3; cursor: not-allowed; }
</style>
</head>
<body>
<div id="app">
  <!-- Top Header -->
  <div id="top-bar">
    <span id="status-dot" class="disconnected"></span>
    <h1>RimWorld Bridge Agent</h1>
    <span class="colony-info" id="colony-info">Colony: --</span>
    <button id="info-btn">i</button>
  </div>

  <!-- Stats Bar -->
  <div id="stats-bar">
    <span class="stat"><span class="stat-label">Pawns:</span><span class="stat-value" id="stat-pawns">--</span></span>
    <span class="stat"><span class="stat-label">Mood:</span><span class="stat-value" id="stat-mood">--</span></span>
    <span class="stat"><span class="stat-label">Food:</span><span class="stat-value" id="stat-food">--</span></span>
  </div>

  <!-- Info Overlay -->
  <div id="info-overlay">
    <div id="info-panel">
      <button id="info-close">&times;</button>
      <h2>SDK 信息</h2>
      <table class="info-table">
        <tr><th>Model</th><td><span id="info-model">${modelName}</span></td></tr>
        <tr><th>Project</th><td>${projectPath}</td></tr>
        <tr><th>Session</th><td><span id="info-session">-</span></td></tr>
        <tr><th>Version</th><td><span id="info-version">-</span></td></tr>
        <tr><th>Permission</th><td><span id="info-permission">-</span></td></tr>
      </table>
    </div>
  </div>

  <div id="messages"></div>
  <button id="new-msg-pill" onclick="scrollToBottom()">↓ 回到底部</button>

  <!-- Compose -->
  <div class="chat-compose">
    <span class="prompt">&gt;</span>
    <textarea id="chat-input" placeholder="输入消息..." rows="1"></textarea>
    <button id="send-btn" disabled>Send</button>
  </div>
</div>
<script>
(function() {
  const TOKEN = ${escapedToken};
  const RECONNECT_BASE = 5000;
  const RECONNECT_MAX = 30000;

  const messagesEl = document.getElementById('messages');
  const inputEl = document.getElementById('chat-input');
  const sendBtn = document.getElementById('send-btn');
  const statusDot = document.getElementById('status-dot');
  const colonyInfo = document.getElementById('colony-info');
  const newMsgPill = document.getElementById('new-msg-pill');

  // Stats bar
  const statsBar = document.getElementById('stats-bar');
  const statPawns = document.getElementById('stat-pawns');
  const statMood = document.getElementById('stat-mood');
  const statFood = document.getElementById('stat-food');

  // Info panel
  const infoOverlay = document.getElementById('info-overlay');
  const infoBtn = document.getElementById('info-btn');
  const infoClose = document.getElementById('info-close');

  infoBtn.addEventListener('click', function() { infoOverlay.style.display = 'block'; });
  infoClose.addEventListener('click', function() { infoOverlay.style.display = 'none'; });
  infoOverlay.addEventListener('click', function(e) {
    if (e.target === infoOverlay) infoOverlay.style.display = 'none';
  });

  let ws = null;
  let reconnectTimer = null;
  let reconnectDelay = RECONNECT_BASE;
  let connected = false;

  // Tool timing
  var toolStartTimes = {};

  // Reading indicator state
  var readingEl = null;
  var awaitingResponse = false;

  // ===== WebSocket =====
  function connect() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    setStatus('connecting');
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = protocol + '//' + location.host;
    ws = new WebSocket(url);

    ws.onopen = () => {
      reconnectDelay = RECONNECT_BASE;
      const hello = { type: 'hello', client: { name: 'ChatPage', version: '1.0' } };
      if (TOKEN) hello.auth = { token: TOKEN };
      ws.send(JSON.stringify(hello));
    };

    ws.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);
        handleMessage(msg);
      } catch {}
    };

    ws.onclose = () => {
      connected = false;
      setStatus('disconnected');
      sendBtn.disabled = true;
      hideReading();
      scheduleReconnect();
    };

    ws.onerror = () => {
      if (ws) ws.close();
    };
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      reconnectDelay = Math.min(reconnectDelay * 1.5, RECONNECT_MAX);
      connect();
    }, reconnectDelay);
  }

  function setStatus(state) {
    statusDot.className = state;
  }

  // ===== Colony Stats =====
  function updateColonyStats(data) {
    if (data.colonyName) {
      colonyInfo.textContent = 'Colony: ' + data.colonyName;
    }
    if (data.colonistCount !== undefined) {
      statPawns.textContent = data.colonistCount;
    }
    if (data.avgMood !== undefined) {
      var mood = Number(data.avgMood);
      statMood.textContent = mood + '%';
      statMood.className = 'stat-value' + (mood < 30 ? ' danger' : mood < 50 ? ' warning' : ' ok');
    }
    if (data.foodDays !== undefined) {
      var fd = Number(data.foodDays);
      statFood.textContent = fd + 'd';
      statFood.className = 'stat-value' + (fd < 1 ? ' danger' : fd < 3 ? ' warning' : ' ok');
    }
    statsBar.style.display = 'flex';
  }

  // ===== Message handling =====
  function handleMessage(msg) {
    switch (msg.type) {
      case 'hello-ok':
        connected = true;
        setStatus('connected');
        sendBtn.disabled = false;
        hideReading();
        break;

      case 'colony-stats':
        updateColonyStats(msg);
        break;

      case 'assistant':
      case 'user': {
        const isAssistant = msg.type === 'assistant';
        const content = msg.message?.content;
        if (!content) return;
        if (isAssistant) hideReading();
        addMessage(isAssistant, content, msg.agent_type);
        break;
      }

      case 'system': {
        if (msg.subtype === 'init') updateSdkInfo(msg);
        break;
      }

      case 'result': {
        const sub = msg.subtype || 'unknown';
        const label = sub === 'success' ? '✓ Done'
                     : sub === 'aborted' ? '⏹ Aborted'
                     : '✗ Failed';
        const cls = sub === 'success' ? 'ok' : sub === 'aborted' ? 'warn' : 'error';
        hideReading();
        addResult(cls, label);
        break;
      }
    }
  }

  function updateSdkInfo(msg) {
    var el;
    el = document.getElementById('info-model'); if (el) el.textContent = msg.model || '?';
    el = document.getElementById('info-version'); if (el) el.textContent = msg.claude_code_version || '?';
    el = document.getElementById('info-session'); if (el) el.textContent = msg.session_id || '?';
    el = document.getElementById('info-permission'); if (el) el.textContent = msg.permissionMode || '?';
  }

  // ===== Panel Factory =====
  function createPanel(role, opts) {
    opts = opts || {};
    var panel = document.createElement('div');
    panel.className = 'chat-panel ' + role;

    var header = document.createElement('div');
    header.className = 'panel-header';

    var tag = document.createElement('span');
    tag.className = 'role-tag';
    tag.textContent = opts.title || (role === 'user' ? 'USER' : role === 'tool' ? 'TOOL' : 'AGENT');
    header.appendChild(tag);

    if (opts.subtitle) {
      var sub = document.createElement('span');
      sub.className = 'tool-name';
      sub.textContent = opts.subtitle;
      header.appendChild(sub);
    }

    if (opts.timing) {
      var timing = document.createElement('span');
      timing.className = 'tool-timing' + (opts.timingError ? ' error' : '');
      timing.textContent = '\\u2713 ' + opts.timing + 'ms';
      header.appendChild(timing);
    }

    panel.appendChild(header);

    var body = document.createElement('div');
    body.className = 'panel-body';
    panel.appendChild(body);

    messagesEl.appendChild(panel);
    checkScroll();
    return { panel: panel, body: body };
  }

  // ===== Tool Panel =====
  function renderToolPanel(block, timing) {
    var isResult = block.type === 'tool_result';
    var opt = { title: 'TOOL', subtitle: block.name || '?' };

    if (isResult && timing !== undefined && timing !== null) {
      opt.timing = timing;
      opt.timingError = block.isError;
    }

    var p = createPanel('tool', opt);
    var body = p.body;
    body.style.padding = '6px 10px';

    var wrap = document.createElement('div');
    wrap.className = 'tool-output-wrap collapsed';

    var out = document.createElement('div');
    out.className = 'tool-output';

    if (isResult) {
      // Extract content string
      var content = '';
      if (typeof block.content === 'string') content = block.content;
      else if (Array.isArray(block.content)) content = block.content.map(function(c) { return c.text || ''; }).join('\\n');
      else if (block.content) { try { content = JSON.stringify(block.content, null, 2); } catch { content = String(block.content); } }

      if (block.isError) {
        var errDiv = document.createElement('div');
        errDiv.style.color = 'var(--red)';
        errDiv.textContent = content || 'Error';
        out.appendChild(errDiv);
      } else if (content.length > 120) {
        // Long output — truncated with expand
        var textDiv = document.createElement('div');
        textDiv.textContent = content;
        out.appendChild(textDiv);
        wrap.classList.add('has-expand');
        wrap.dataset.full = content;
      } else {
        var textDiv = document.createElement('div');
        textDiv.textContent = content || '(empty)';
        out.appendChild(textDiv);
      }
    } else {
      // tool_use — show args
      if (block.input) {
        var argsStr = '';
        try { argsStr = JSON.stringify(block.input, null, 2); } catch { argsStr = String(block.input || ''); }
        var argsDiv = document.createElement('div');
        argsDiv.textContent = argsStr || '(no args)';
        out.appendChild(argsDiv);
      }
    }

    wrap.appendChild(out);
    body.appendChild(wrap);

    // Expand button for long output
    if (wrap.classList.contains('has-expand')) {
      var btn = document.createElement('button');
      btn.className = 'tool-expand-btn';
      btn.textContent = '... show more';
      btn.addEventListener('click', function() {
        var isExp = wrap.classList.toggle('expanded');
        btn.textContent = isExp ? 'show less' : '... show more';
        if (isExp && wrap.dataset.full) {
          out.textContent = wrap.dataset.full;
        }
      });
      body.appendChild(btn);
    }

    return p;
  }

  // ===== Add Message =====
  function addMessage(isAssistant, content, agentType) {
    var role = isAssistant ? 'agent' : 'user';

    // Group: consecutive same-role messages append content
    var lastPanel = messagesEl.lastElementChild;
    var sameRole = lastPanel && lastPanel.classList.contains('chat-panel') && lastPanel.classList.contains(role)
      && role === 'agent' && !agentType;

    if (!sameRole) {
      var opts = { title: role === 'user' ? 'USER' : 'AGENT' };
      if (isAssistant && agentType) {
        opts.title = agentType.toUpperCase();
      }
      createPanel(role, opts);
      lastPanel = messagesEl.lastElementChild;
    }

    var body = lastPanel.querySelector('.panel-body');

    if (typeof content === 'string') {
      var textDiv = document.createElement('div');
      textDiv.textContent = content;
      body.appendChild(textDiv);
    } else if (Array.isArray(content)) {
      for (var i = 0; i < content.length; i++) {
        var block = content[i];
        if (block.type === 'text') {
          var textDiv = document.createElement('div');
          textDiv.textContent = block.text || '';
          body.appendChild(textDiv);
        } else if (block.type === 'tool_use') {
          var tid = block.id;
          toolStartTimes[tid] = Date.now();
          renderToolPanel(block, null);
          // Reset panel target — tool panels are separate
          lastPanel = null;
        } else if (block.type === 'tool_result') {
          var tid = block.id;
          var elapsed = null;
          if (tid && toolStartTimes[tid] !== undefined) {
            elapsed = Date.now() - toolStartTimes[tid];
            delete toolStartTimes[tid];
          }
          renderToolPanel(block, elapsed);
          lastPanel = null;
        }
      }
    }

    checkScroll();
  }

  // ===== Add Result (divider) =====
  function addResult(cls, label) {
    var div = document.createElement('div');
    div.className = 'chat-divider';
    div.innerHTML = '<span class="chat-divider__line"></span>'
      + '<span class="chat-divider__label ' + cls + '">' + label + '</span>'
      + '<span class="chat-divider__line"></span>';
    messagesEl.appendChild(div);
    checkScroll();
  }

  // ===== Reading Indicator (terminal style) =====
  function showReading() {
    if (readingEl) return;
    readingEl = document.createElement('div');
    readingEl.className = 'chat-reading';
    readingEl.innerHTML = '<span class="prompt">></span> <span>...</span> <span class="cursor"></span>';
    messagesEl.appendChild(readingEl);
    checkScroll();
  }

  function hideReading() {
    if (readingEl) {
      readingEl.remove();
      readingEl = null;
    }
    awaitingResponse = false;
  }

  // ===== Auto-scroll =====
  let userScrolledUp = false;

  function checkScroll() {
    const threshold = 80;
    const diff = messagesEl.scrollHeight - messagesEl.clientHeight - messagesEl.scrollTop;
    userScrolledUp = diff > threshold;
    newMsgPill.style.display = userScrolledUp ? 'inline-flex' : 'none';
    if (!userScrolledUp) messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function scrollToBottom() {
    messagesEl.scrollTop = messagesEl.scrollHeight;
    userScrolledUp = false;
    newMsgPill.style.display = 'none';
  }
  window.scrollToBottom = scrollToBottom;

  messagesEl.addEventListener('scroll', checkScroll);

  // ===== Send message =====
  function sendMessage() {
    const text = inputEl.value.trim();
    if (!text || !connected) return;
    ws.send(JSON.stringify({
      type: 'event', event: 'chat',
      payload: { text: text }
    }));
    inputEl.value = '';
    adjustHeight();
    awaitingResponse = true;
    showReading();
  }

  function adjustHeight() {
    inputEl.style.height = 'auto';
    var newH = Math.min(Math.max(inputEl.scrollHeight, 24), 150);
    inputEl.style.height = newH + 'px';
  }

  sendBtn.addEventListener('click', sendMessage);
  inputEl.addEventListener('keydown', function(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  });

  inputEl.addEventListener('input', function() {
    adjustHeight();
    sendBtn.disabled = !connected || !inputEl.value.trim();
  });

  // ===== Init =====
  connect();
  inputEl.focus();
})();
</script>
</body>
</html>`;
}

function escHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
          .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}
