/**
 * 聊天页面 HTML 生成 — 单文件自包含，内联 CSS + JS
 * 现代简约 UI 风格
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
  /* ===== Design Tokens ===== */
  :root {
    --bg: #f5f6f9;
    --card: #ffffff;
    --surface: #eeeff3;
    --hover: #e4e6ec;
    --text: #1a1c24;
    --text-strong: #0c0d14;
    --muted: #6f7488;
    --muted-subtle: #9ca1b5;
    --border: #e2e4eb;
    --border-strong: #ccd0d9;
    --accent: #e54b4b;
    --blue: #3570d6;
    --blue-bg: rgba(53,112,214,0.07);
    --blue-border: rgba(53,112,214,0.16);
    --green: #1d914d;
    --green-bg: rgba(29,145,77,0.06);
    --green-border: rgba(29,145,77,0.18);
    --amber: #c4890c;
    --amber-bg: rgba(196,137,12,0.06);
    --amber-border: rgba(196,137,12,0.18);
    --red: #d93d3d;
    --cyan: #188585;
    --mono: "Cascadia Code","Fira Code","JetBrains Mono","Consolas",monospace;
    --font: system-ui,-apple-system,"Microsoft YaHei","PingFang SC","Segoe UI",sans-serif;
    --radius-sm: 6px;
    --radius: 10px;
    --radius-lg: 14px;
    color-scheme: light;
  }

  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { height: 100%; height: 100dvh; overflow: hidden; background: var(--bg); }
  body {
    font-family: var(--font); color: var(--text);
    font-size: 14px; line-height: 1.5;
    -webkit-font-smoothing: antialiased;
  }
  #app {
    display: grid;
    grid-template-columns: 90px 1fr minmax(400px, 700px) 1fr 220px;
    height: 100dvh; width: 100%;
  }
  #sidebar { grid-column: 1; }
  #main {
    grid-column: 3;
    display: flex; flex-direction: column;
    min-width: 0; overflow: hidden;
  }
  #rightbar { grid-column: 5; }
  #sidebar {
    display: flex; flex-direction: column; gap: 6px;
    padding: 10px 6px;
    border-right: 1px solid var(--border);
    background: var(--card);
    overflow-y: auto;
  }
  #rightbar {
    display: flex; flex-direction: column;
    border-left: 1px solid var(--border);
    background: var(--card);
    overflow-y: auto;
    font-size: 12px; font-family: var(--mono);
  }
  .sb-stat {
    text-align: center; padding: 6px 4px;
    border-radius: var(--radius-sm);
    background: var(--surface);
    overflow-wrap: break-word; word-break: break-word;
  }
  .sb-stat .sb-val {
    font-size: 14px; font-weight: 700; font-variant-numeric: tabular-nums;
    color: var(--text-strong); line-height: 1.3;
  }
  .sb-stat .sb-label {
    font-size: 10px; color: var(--muted); margin-top: 2px;
  }
  .sb-stat .sb-val.danger { color: var(--red); }
  .sb-stat .sb-val.warning { color: var(--amber); }
  .sb-stat .sb-val.ok { color: var(--green); }

  /* Scrollbar */
  ::-webkit-scrollbar { width: 5px; }
  ::-webkit-scrollbar-track { background: transparent; }
  ::-webkit-scrollbar-thumb { background: var(--border-strong); border-radius: 3px; }
  ::-webkit-scrollbar-thumb:hover { background: var(--muted-subtle); }

  /* ===== Header ===== */
  #header {
    flex-shrink: 0;
    padding: 14px 20px 10px;
    border-bottom: 1px solid var(--border);
    background: var(--bg);
  }
  .header-row {
    display: flex; align-items: center; gap: 10px;
  }
  #status-dot {
    width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0;
    transition: background 0.3s, box-shadow 0.3s;
  }
  #status-dot.connected { background: var(--green); box-shadow: 0 0 5px rgba(29,145,77,0.25); }
  #status-dot.connecting { background: var(--amber); animation: dot-blink 1.2s ease-in-out infinite; }
  #status-dot.disconnected { background: var(--red); }
  @keyframes dot-blink { 0%,100% { opacity: 0.3; } 50% { opacity: 1; } }

  .header-title {
    font-size: 14px; font-weight: 600; color: var(--text-strong);
    letter-spacing: -0.2px;
  }
  .header-spacer { flex: 1; }
  .header-colony {
    font-size: 12px; color: var(--muted); font-weight: 500;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 180px;
  }
  .header-agent {
    font-size: 11px; color: var(--blue); font-weight: 500;
    padding: 1px 8px; border-radius: 4px;
    background: var(--blue-bg); border: 1px solid var(--blue-border);
    white-space: nowrap; flex-shrink: 0;
  }
  #info-btn {
    background: none; border: 1px solid var(--border-strong); color: var(--muted);
    border-radius: 4px; width: 22px; height: 22px; cursor: pointer;
    font-size: 11px; font-weight: 600; font-family: var(--mono);
    display: flex; align-items: center; justify-content: center;
    flex-shrink: 0; transition: all 0.15s;
  }
  #info-btn:hover { background: var(--surface); color: var(--text); border-color: var(--muted-subtle); }

  /* Header budget / tools */
  .header-meta {
    display: flex; align-items: center; gap: 8px;
    margin-top: 6px; font-size: 11px;
  }
  .header-meta .meta-item {
    display: inline-flex; align-items: center; gap: 4px;
    padding: 2px 8px; border-radius: 4px;
    background: var(--surface); color: var(--muted);
    font-family: var(--mono); white-space: nowrap;
  }
  .header-meta .meta-val { color: var(--text); font-weight: 500; }
  .hdr-budget {
    display: inline-flex; align-items: center; gap: 4px;
    font-family: var(--mono); font-size: 11px;
    padding: 1px 8px; border-radius: 4px;
    background: var(--surface); color: var(--muted);
    white-space: nowrap;
  }
  .hdr-budget .budget-bar {
    display: inline-block; height: 4px; border-radius: 2px;
    background: var(--green); vertical-align: middle; width: 32px;
    transition: background 0.3s;
  }
  .hdr-budget .budget-bar.warning { background: var(--amber); }
  .hdr-budget .budget-bar.danger { background: var(--red); }
  .think-dd {
    position: relative; margin-left: auto; font-size: 11px;
  }
  .think-dd .dd-btn {
    display: inline-flex; align-items: center; gap: 3px;
    padding: 1px 8px; border-radius: 4px;
    background: var(--surface); color: var(--text);
    cursor: pointer; white-space: nowrap;
  }
  .think-dd .dd-btn:hover { background: var(--hover); }
  .think-dd .dd-menu {
    display: none; position: absolute; right: 0; top: 100%; z-index: 50;
    margin-top: 2px; background: var(--card);
    border: 1px solid var(--border-strong); border-radius: var(--radius-sm);
    padding: 4px 0; min-width: 130px; box-shadow: 0 4px 12px rgba(0,0,0,0.08);
  }
  .think-dd.open .dd-menu { display: block; }
  .think-dd .dd-item {
    display: flex; align-items: center; gap: 6px;
    padding: 4px 10px; cursor: pointer; color: var(--text);
    white-space: nowrap;
  }
  .think-dd .dd-item:hover { background: var(--surface); }
  .think-dd .dd-item .dd-check { width: 14px; color: var(--green); font-weight: 700; visibility: hidden; }
  .think-dd .dd-item.on .dd-check { visibility: visible; }
  .think-dd .dd-sub {
    display: none; padding: 2px 0 2px 20px; font-size: 10px;
  }
  .think-dd .dd-item.selected + .dd-sub { display: block; }
  .think-dd .dd-sub span {
    display: inline-block; padding: 2px 6px; margin: 1px 2px; border-radius: 3px;
    cursor: pointer; color: var(--muted);
  }
  .think-dd .dd-sub span:hover { color: var(--text); background: var(--hover); }
  .think-dd .dd-sub span.on { color: var(--blue); font-weight: 500; }
  .think-dd .dd-sub input {
    width: 60px; padding: 2px 4px; border: 1px solid var(--border-strong);
    border-radius: 3px; font-family: var(--mono); font-size: 10px;
  }

  /* ===== TODO Panel (rightbar) ===== */
  #todo-panel-header {
    display: flex; align-items: center; gap: 6px;
    padding: 8px 10px;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    color: var(--muted);
    font-size: 11px; font-weight: 600;
    position: sticky; top: 0; z-index: 1;
  }
  #todo-panel-header .todo-count { color: var(--cyan); }
  #todo-list { padding: 4px 0; }
  .todo-item {
    display: flex; align-items: center; gap: 4px;
    padding: 3px 10px;
    border-bottom: 1px solid var(--border);
  }
  .todo-item:last-child { border-bottom: none; }
  .todo-item .todo-prio { flex-shrink: 0; width: 18px; font-size: 10px; font-weight: 700; }
  .todo-item .todo-prio.p-high { color: var(--red); }
  .todo-item .todo-prio.p-mid { color: var(--amber); }
  .todo-item .todo-prio.p-low { color: var(--muted); }
  .todo-item .todo-desc { flex: 1; color: var(--text); word-break: break-all; overflow-wrap: break-word; }
  .todo-item.done .todo-desc { color: var(--muted); text-decoration: line-through; }
  .todo-item.cancelled .todo-desc { color: var(--muted-subtle); text-decoration: line-through; }
  .todo-item .todo-id { font-size: 10px; color: var(--muted); flex-shrink: 0; }

  /* SDKTasks */
  #sdk-tasks-header {
    display: flex; align-items: center; gap: 6px;
    padding: 8px 10px;
    background: var(--surface);
    border-top: 1px solid var(--border);
    border-bottom: 1px solid var(--border);
    color: var(--muted);
    font-size: 11px; font-weight: 600;
    position: sticky; top: 0; z-index: 1;
  }
  #sdk-tasks-header .task-count { color: var(--blue); }
  #sdk-task-list { padding: 4px 0; }
  .sdk-task-item {
    display: flex; align-items: center; gap: 4px;
    padding: 3px 10px; font-size: 11px;
    border-bottom: 1px solid var(--border);
  }
  .sdk-task-item:last-child { border-bottom: none; }
  .sdk-task-item .task-status {
    flex-shrink: 0; font-size: 10px; font-weight: 500; width: 28px; text-align: center;
  }
  .sdk-task-item .task-status.pending { color: var(--muted); }
  .sdk-task-item .task-status.in_progress { color: var(--amber); }
  .sdk-task-item .task-status.completed { color: var(--green); }
  .sdk-task-item .task-subject { flex: 1; color: var(--text); word-break: break-all; overflow-wrap: break-word; }
  .sdk-task-item.completed .task-subject { color: var(--muted); text-decoration: line-through; }

  /* ===== Info Overlay ===== */
  #info-overlay {
    display: none; position: fixed; inset: 0;
    z-index: 100; background: rgba(0,0,0,0.25);
    backdrop-filter: blur(2px);
  }
  #info-panel {
    position: absolute; top: 50%; left: 50%; transform: translate(-50%,-50%);
    background: var(--card); border: 1px solid var(--border-strong);
    border-radius: var(--radius); padding: 18px 20px;
    box-shadow: 0 8px 32px rgba(0,0,0,0.10);
    width: 460px; max-width: 90vw; max-height: 80vh; overflow-y: auto;
  }
  #info-close {
    float: right; background: none; border: none; color: var(--muted);
    cursor: pointer; font-size: 18px; line-height: 1; padding: 0 2px;
  }
  #info-close:hover { color: var(--text); }
  #info-panel h2 {
    font-size: 13px; font-weight: 600; color: var(--text-strong);
    margin-bottom: 12px; letter-spacing: -0.2px;
  }
  .info-table { width: 100%; border-collapse: collapse; font-size: 12px; }
  .info-table th, .info-table td {
    text-align: left; padding: 5px 0; border-bottom: 1px solid var(--border);
  }
  .info-table th { color: var(--muted); width: 68px; font-weight: 400; }
  .info-table td { color: var(--text); word-break: break-all; font-family: var(--mono); font-size: 11px; }
  .info-list { max-height: 120px; overflow-y: auto; line-height: 1.6; }
  .info-list div { white-space: nowrap; }

  /* ===== Messages ===== */
  #messages {
    flex: 1; overflow-y: auto; padding: 14px 20px;
    display: flex; flex-direction: column; gap: 10px;
    scroll-behavior: smooth;
  }

  /* Base message */
  .msg {
    animation: msg-in 200ms ease-out both;
    position: relative;
  }
  @keyframes msg-in {
    from { opacity: 0; transform: translateY(6px); }
    to { opacity: 1; transform: translateY(0); }
  }
  .msg-label {
    font-size: 10px; font-weight: 600; text-transform: uppercase;
    letter-spacing: 0.6px; margin-bottom: 4px;
    user-select: none;
  }

  /* --- USER --- */
  .msg-user {
    align-self: flex-end; max-width: 72%;
  }
  .msg-user .msg-label { color: var(--blue); text-align: right; }
  .msg-user .msg-body {
    background: var(--blue-bg);
    border: 1px solid var(--blue-border);
    border-radius: var(--radius-lg) 4px var(--radius-lg) var(--radius-lg);
    padding: 10px 14px;
    font-size: 14px; line-height: 1.6;
    white-space: pre-wrap; word-wrap: break-word;
    color: var(--text);
  }

  /* --- AGENT --- */
  .msg-agent {
    align-self: flex-start; max-width: 88%;
  }
  .msg-agent .msg-label { color: var(--green); padding-left: 12px; }
  .msg-agent .msg-body {
    background: var(--card);
    border: 1px solid var(--border-strong);
    border-left: 3px solid var(--green-border);
    border-radius: 4px var(--radius) var(--radius) 4px;
    padding: 10px 14px;
    font-size: 14px; line-height: 1.65;
    white-space: pre-wrap; word-wrap: break-word;
    color: var(--text);
  }

  /* --- SUB-AGENT --- */
  .msg-agent.sub-agent { margin-left: 12px; }
  .msg-agent.sub-agent .msg-label { color: #8b5cf0; }
  .msg-agent.sub-agent .msg-body {
    border-left-color: rgba(139,92,240,0.22);
  }

  /* --- SUB-AGENT GROUP --- */
  .sa-group {
    align-self: stretch; margin: 4px 0 4px 8px;
    border: 1px solid rgba(139,92,240,0.15);
    border-radius: var(--radius);
    overflow: hidden;
  }
  .sa-group-header {
    display: flex; align-items: center; gap: 6px;
    padding: 6px 12px;
    background: rgba(139,92,240,0.04);
    cursor: pointer; user-select: none;
    font-size: 11px; color: #8b5cf0; font-weight: 500;
  }
  .sa-group-header:hover { background: rgba(139,92,240,0.07); }
  .sa-group-header .tgl-arrow { width: 10px; text-align: center; }
  .sa-group-header .sa-count { font-size: 10px; color: var(--muted); margin-left: auto; }
  .sa-group-body {
    padding: 6px 6px 4px;
    display: flex; flex-direction: column; gap: 6px;
  }
  .sa-group.collapsed .sa-group-body { display: none; }

  /* --- THINKING --- */
  .msg-thinking {
    align-self: stretch; margin: 2px 0;
  }
  .msg-thinking .th-header {
    display: flex; align-items: center; gap: 6px;
    padding: 5px 12px;
    border-radius: var(--radius-sm);
    cursor: pointer; user-select: none;
    font-size: 11px; color: var(--muted);
    transition: background 0.15s;
  }
  .msg-thinking .th-header:hover { background: var(--card); }
  .msg-thinking .th-icon { font-size: 12px; transition: transform 0.2s; width: 14px; text-align: center; }
  .msg-thinking.collapsed .th-icon { transform: rotate(-90deg); }
  .msg-thinking .th-label { font-weight: 500; }
  .msg-thinking .th-status {
    font-size: 10px; opacity: 0.6;
  }
  .msg-thinking .th-cursor {
    display: inline-block; width: 2px; height: 12px;
    background: var(--muted); margin-left: 2px;
    animation: think-cursor 0.8s ease infinite;
  }
  @keyframes think-cursor {
    0%, 100% { opacity: 1; }
    50% { opacity: 0; }
  }
  .msg-thinking .th-body {
    border-left: 2px solid var(--border-strong);
    margin-left: 11px; padding: 4px 0 8px 12px;
    font-size: 12px; line-height: 1.6;
    color: var(--muted); font-style: italic;
    white-space: pre-wrap; word-wrap: break-word;
    display: none;
  }
  .msg-thinking:not(.collapsed) .th-body { display: block; }
  .msg-thinking.done .th-cursor { display: none; }
  .msg-thinking.done .th-header { cursor: pointer; }

  /* --- TOOL --- */
  .msg-tool {
    align-self: stretch;
  }
  .msg-tool .msg-header {
    display: flex; align-items: center; gap: 8px;
    padding: 7px 12px;
    background: var(--card);
    border: 1px solid var(--border-strong);
    border-left: 3px solid var(--amber-border);
    border-radius: var(--radius-sm) var(--radius-sm) 0 0;
    font-size: 12px;
  }
  .msg-tool .msg-header .msg-label {
    color: var(--amber); margin-bottom: 0; font-size: 10px;
  }
  .msg-tool .msg-header .tgl-arrow {
    font-size: 10px; color: var(--muted); transition: transform 0.15s;
    width: 10px; text-align: center; flex-shrink: 0;
  }
  .msg-tool .msg-header .tool-name {
    font-family: var(--mono); font-size: 12px; font-weight: 500; color: var(--text);
    flex: 1;
  }
  .msg-tool .msg-header .tool-timing {
    font-family: var(--mono); font-size: 11px; color: var(--green);
    white-space: nowrap;
  }
  .msg-tool .msg-header .tool-timing.error { color: var(--red); }
  .msg-tool .msg-header .tool-status {
    font-size: 10px; font-weight: 500;
    margin-left: 6px; white-space: nowrap;
  }
  .msg-tool .msg-header .tool-status.running { color: var(--amber); }
  .msg-tool .msg-header .tool-status.done { color: var(--green); }
  .msg-tool .msg-header .tool-status.error { color: var(--red); }
  .tool-result-text {
    font-family: var(--mono); font-size: 12px; line-height: 1.5;
    padding: 6px 0; color: var(--text);
    white-space: pre-wrap; word-break: break-all;
    border-top: 1px solid var(--border);
  }
  .msg-tool .msg-body {
    background: var(--card);
    border: 1px solid var(--border-strong);
    border-top: none;
    border-left: 3px solid var(--amber-border);
    border-radius: 0 0 var(--radius-sm) var(--radius-sm);
    padding: 8px 12px;
    font-family: var(--mono); font-size: 12px; line-height: 1.55;
    white-space: pre-wrap; word-wrap: break-word;
    color: var(--text);
  }

  /* Tool expand/collapse */
  .tool-expand-btn {
    display: block; width: 100%; padding: 4px;
    background: var(--surface); border: none; border-top: 1px solid var(--border-strong);
    color: var(--muted); font-size: 11px; cursor: pointer;
    font-family: var(--font); transition: all 0.15s;
    border-radius: 0 0 var(--radius-sm) var(--radius-sm);
  }
  .tool-expand-btn:hover { color: var(--text); background: var(--hover); }

  /* --- Result divider --- */
  .msg-result {
    display: flex; align-items: center; gap: 10px;
    margin: 0 8px;
  }
  .msg-result .result-line { flex: 1; height: 1px; background: var(--border-strong); }
  .msg-result .result-label {
    font-size: 11px; font-weight: 500; padding: 2px 10px;
    border-radius: 4px; border: 1px solid var(--border-strong);
    white-space: nowrap; user-select: none;
    font-family: var(--mono);
  }
  .msg-result .result-label.ok { color: var(--green); border-color: var(--green-border); }
  .msg-result .result-label.error { color: var(--red); border-color: rgba(217,61,61,0.25); }
  .msg-result .result-label.warn { color: var(--amber); border-color: var(--amber-border); }

  /* Token usage */
  /* Header budget */

  /* --- Typing indicator --- */
  .typing-indicator {
    align-self: flex-start; display: flex; align-items: center; gap: 4px;
    padding: 10px 14px; margin-left: 4px;
  }
  .typing-dot {
    width: 5px; height: 5px; border-radius: 50%;
    background: var(--muted);
    animation: typing-bounce 1.2s ease-in-out infinite;
  }
  .typing-dot:nth-child(2) { animation-delay: 0.15s; }
  .typing-dot:nth-child(3) { animation-delay: 0.3s; }
  @keyframes typing-bounce {
    0%,60%,100% { opacity: 0.2; transform: translateY(0); }
    30% { opacity: 1; transform: translateY(-3px); }
  }

  /* ===== New Messages Pill ===== */
  #new-msg-pill {
    display: none; align-self: center;
    padding: 5px 14px; font-size: 11px; cursor: pointer;
    color: var(--muted); background: var(--card);
    border: 1px solid var(--border-strong); border-radius: 20px;
    margin: 2px auto; z-index: 10;
    font-weight: 500; transition: all 0.15s;
    box-shadow: 0 2px 8px rgba(0,0,0,0.07);
  }
  #new-msg-pill:hover { border-color: var(--muted-subtle); color: var(--text); }

  /* ===== Compose ===== */
  .compose {
    flex-shrink: 0; display: flex; align-items: center; gap: 8px;
    padding: 10px 20px; padding-bottom: max(10px, env(safe-area-inset-bottom));
    background: var(--bg);
    border-top: 1px solid var(--border);
  }
  .compose .prompt {
    font-family: var(--mono); font-size: 15px; color: var(--muted);
    user-select: none; flex-shrink: 0;
  }
  #chat-input {
    flex: 1; background: var(--surface); border: 1px solid var(--border-strong);
    border-radius: 8px; outline: none;
    color: var(--text); font-family: var(--font); font-size: 15px;
    line-height: 1.5; padding: 10px 14px;
    min-height: 44px; max-height: 160px; resize: none;
    transition: border-color 0.15s;
  }
  #chat-input:focus { border-color: var(--muted-subtle); }
  #chat-input::placeholder { color: var(--muted); }
  #send-btn {
    background: var(--surface); border: 1px solid var(--border-strong);
    border-radius: 8px; color: var(--muted); cursor: pointer;
    padding: 7px 14px; font-size: 12px; font-weight: 500;
    transition: all 0.15s; flex-shrink: 0;
  }
  #send-btn:hover:not(:disabled) {
    background: var(--blue); border-color: var(--blue); color: #fff;
  }
  #send-btn:disabled { opacity: 0.3; cursor: not-allowed; }
  #abort-btn {
    background: none; border: 1px solid rgba(217,61,61,0.3);
    border-radius: 8px; color: var(--red); cursor: pointer;
    padding: 7px 12px; font-size: 12px; font-weight: 500;
    transition: all 0.15s; flex-shrink: 0;
    opacity: 0.3;
  }
  #abort-btn.enabled { opacity: 1; }
  #abort-btn.enabled:hover { background: var(--red); border-color: var(--red); color: #fff; }
  #abort-btn:disabled { opacity: 0.4; cursor: not-allowed; }

  /* ===== Mobile ===== */
  @media (max-width: 768px) {
    #app {
      grid-template-columns: 1fr;
      grid-template-rows: auto 1fr;
    }
    #sidebar {
      grid-column: 1; grid-row: 1;
      flex-direction: row; gap: 6px;
      padding: 6px 8px; overflow-x: auto;
      border-right: none; border-bottom: 1px solid var(--border);
      flex-shrink: 0;
    }
    .sb-stat {
      flex: 0 0 auto; min-width: 52px;
      padding: 4px 8px; text-align: center;
    }
    .sb-stat .sb-val { font-size: 13px; }
    .sb-stat .sb-label { font-size: 9px; }
    #main { grid-column: 1; grid-row: 2; }
    #rightbar { display: none; }

    /* Header */
    #header { padding: 8px 10px 6px; }
    .header-title { font-size: 13px; }
    .header-colony { max-width: 100px; font-size: 11px; }
    .header-meta { flex-wrap: wrap; gap: 4px; margin-top: 4px; font-size: 10px; }
    .header-meta .meta-item { font-size: 10px; padding: 1px 6px; }
    .hdr-budget { font-size: 10px; padding: 1px 6px; }
    .think-dd { font-size: 10px; margin-left: 0; }

    /* Messages */
    #messages { padding: 8px 10px; gap: 6px; }
    .msg-user { max-width: 92%; }
    .msg-agent { max-width: 96%; }
    .msg-user .msg-body,
    .msg-agent .msg-body { font-size: 13px; padding: 8px 12px; line-height: 1.55; }
    .msg-tool .msg-header { font-size: 11px; padding: 6px 8px; }
    .msg-tool .msg-body { font-size: 11px; padding: 6px 8px; }
    .msg-thinking .th-header { font-size: 10px; }
    .msg-thinking .th-body { font-size: 11px; }

    /* Compose */
    .compose {
      padding: 8px 10px; gap: 6px;
      flex-wrap: wrap;
    }
    .compose .prompt { display: none; }
    #chat-input {
      flex: 1 1 100%; font-size: 16px;
      padding: 8px 10px; min-height: 40px; max-height: 120px;
    }
    #send-btn {
      flex: 1 1 auto; min-width: 44px; min-height: 44px;
      padding: 10px 12px; font-size: 13px;
    }
    #abort-btn {
      padding: 10px 12px; font-size: 12px;
      min-height: 44px;
    }

    /* Overlays */
    #info-panel { width: 94vw; max-width: none; padding: 12px 14px; }
    .info-table th { width: 56px; font-size: 11px; }
    .info-table td { font-size: 10px; }
  }
</style>
</head>
<body>
<div id="app">
  <!-- Sidebar -->
  <div id="sidebar">
    <div class="sb-stat"><div class="sb-val" id="sb-pawns">--</div><div class="sb-label">Pawns</div></div>
    <div class="sb-stat"><div class="sb-val" id="sb-mood">--</div><div class="sb-label">Mood</div></div>
    <div class="sb-stat"><div class="sb-val" id="sb-food">--</div><div class="sb-label">Food</div></div>
  </div>

  <!-- Main -->
  <div id="main">
  <!-- Header -->
  <div id="header">
    <div class="header-row">
      <span id="status-dot" class="disconnected"></span>
      <span class="header-title">RimWorld Bridge Agent</span>
      <span class="header-spacer"></span>
      <span class="header-colony" id="colony-name">--</span>
      <span class="header-agent" id="agent-role">休眠中</span>
	      <button id="info-btn" title="SDK 信息">i</button>
    </div>
    <div class="header-meta" id="header-meta">
      <span class="hdr-budget" id="hdr-budget">Token --</span>
      <span class="meta-item">Tools <span class="meta-val" id="meta-tools">0</span></span>
      <div class="think-dd" id="think-dd">
        <span class="dd-btn" id="think-label">💭 默认 ▾</span>
        <div class="dd-menu" id="think-menu">
          <div class="dd-item" data-m="default">默认<span class="dd-check">✓</span></div>
          <div class="dd-item" data-m="disabled">禁用思考<span class="dd-check">✓</span></div>
          <div class="dd-item" data-m="adaptive">引导深度<span class="dd-check">✓</span></div>
          <div class="dd-sub" id="effort-sub">
            <span data-e="medium">中</span><span data-e="high">高</span><span data-e="xhigh">极高</span><span data-e="max">最大</span>
          </div>
          <div class="dd-item" data-m="fixed">固定 Token<span class="dd-check">✓</span></div>
          <div class="dd-sub" id="token-sub">
            <input id="token-input" type="number" min="1000" max="32000" step="1000" value="8000" placeholder="Token数">
          </div>
        </div>
      </div>
    </div>
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
        <tr><th>API Key</th><td><span id="info-apikey">-</span></td></tr>
        <tr><th>MCP</th><td><div class="info-list" id="info-mcp">-</div></td></tr>
        <tr><th>Tools</th><td><div class="info-list" id="info-tools">-</div></td></tr>
        <tr><th>Skills</th><td><div class="info-list" id="info-skills">-</div></td></tr>
      </table>
    </div>
  </div>

  <div id="messages"></div>
  <button id="new-msg-pill" onclick="scrollToBottom()">↓ 回到底部</button>

  <!-- Compose -->
  <div class="compose">
    <span class="prompt">&gt;</span>
    <textarea id="chat-input" placeholder="输入消息..." rows="1"></textarea>
    <button id="send-btn" disabled>Send</button>
    <button id="abort-btn" title="中断当前 AI 回复">中断</button>
  </div>
  </div><!-- /#main -->

  <!-- Rightbar -->
  <div id="rightbar">
    <div id="todo-panel-header">
      <span>&#9744; TODO</span>
      <span class="todo-count" id="todo-count">0</span>
    </div>
    <div id="todo-list"></div>
    <div id="sdk-tasks-header">
      <span>&#9776; AI 计划</span>
      <span class="task-count" id="task-count">0</span>
    </div>
    <div id="sdk-task-list"></div>
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
  const abortBtn = document.getElementById('abort-btn');
  const statusDot = document.getElementById('status-dot');
  const colonyNameEl = document.getElementById('colony-name');
  const newMsgPill = document.getElementById('new-msg-pill');
  // Sidebar stats
  const sbPawns = document.getElementById('sb-pawns');
  const sbMood = document.getElementById('sb-mood');
  const sbFood = document.getElementById('sb-food');
  // Header meta
  const hdrBudget = document.getElementById('hdr-budget');
  const metaTools = document.getElementById('meta-tools');
  const thinkDd = document.getElementById('think-dd');
  const thinkLabel = document.getElementById('think-label');
  const thinkMenu = document.getElementById('think-menu');
  const effortSub = document.getElementById('effort-sub');
  const tokenSub = document.getElementById('token-sub');
  const tokenInput = document.getElementById('token-input');
  var currentMode = 'default';
  var currentEffort = 'medium';
  var currentTokens = 8000;
  var toolCount = 0;

  // 按钮：开关菜单
  thinkLabel.addEventListener('click', function(e) { e.stopPropagation(); thinkDd.classList.toggle('open'); });
  document.addEventListener('click', function() { thinkDd.classList.remove('open'); });

  // 下拉项点击
  thinkMenu.addEventListener('click', function(e) {
    var item = e.target.closest('.dd-item');
    if (!item) return;
    e.stopPropagation();
    var m = item.dataset.m;
    currentMode = m;
    // 更新勾选
    var items = thinkMenu.querySelectorAll('.dd-item');
    for (var j = 0; j < items.length; j++) { items[j].classList.remove('on', 'selected'); }
    item.classList.add('on', 'selected');
    // 子面板
    effortSub.style.display = m === 'adaptive' ? 'block' : 'none';
    tokenSub.style.display = m === 'fixed' ? 'block' : 'none';
    // 标签
    var labels = { default: '默认', disabled: '禁用', adaptive: '引导:' + currentEffort, fixed: '固定' + (currentTokens/1000) + 'K' };
    thinkLabel.innerHTML = '💭 ' + (labels[m] || m) + ' ▾';
    // 发送
  });

  // effort 子选项
  effortSub.addEventListener('click', function(e) {
    var t = e.target;
    if (!t.dataset.e) return;
    var all = effortSub.querySelectorAll('span');
    for (var j = 0; j < all.length; j++) all[j].classList.remove('on');
    t.classList.add('on');
    currentEffort = t.dataset.e;
    thinkLabel.innerHTML = '💭 引导:' + currentEffort + ' ▾';
  });
  effortSub.querySelector('span[data-e="medium"]').classList.add('on');

  // token 输入
  tokenInput.addEventListener('change', function() {
    currentTokens = parseInt(tokenInput.value) || 8000;
    thinkLabel.innerHTML = '💭 固定' + (currentTokens/1000) + 'K ▾';
  });

  function esc(s) { return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }

  // SDKTasks
  var sdkTasks = [];
  var taskCountEl = document.getElementById('task-count');
  var sdkTaskListEl = document.getElementById('sdk-task-list');

  function trackSdkTask(name, input) {
    if (!input) return;
    if (name === 'TaskCreate') {
      var subj = input.subject || input.activeForm || '?';
      sdkTasks.push({ id: sdkTasks.length + 1, subject: subj, status: 'pending' });
    } else if (name === 'TaskUpdate') {
      var tid = input.taskId;
      var st = input.status;
      for (var i = 0; i < sdkTasks.length; i++) {
        if (String(sdkTasks[i].id) === String(tid)) { sdkTasks[i].status = st; break; }
      }
    }
    renderSdkTasks();
  }

  function renderSdkTasks() {
    var pending = 0;
    for (var i = 0; i < sdkTasks.length; i++) {
      if (sdkTasks[i].status !== 'completed') pending++;
    }
    taskCountEl.textContent = pending;
    sdkTaskListEl.innerHTML = '';
    for (var i = 0; i < sdkTasks.length; i++) {
      var t = sdkTasks[i];
      var div = document.createElement('div');
      div.className = 'sdk-task-item' + (t.status === 'completed' ? ' completed' : '');
      var icon = t.status === 'in_progress' ? '进行' : t.status === 'completed' ? '完成' : '待办';
      var cls = 'task-status ' + (t.status === 'in_progress' ? 'in_progress' : t.status === 'completed' ? 'completed' : 'pending');
      div.innerHTML = '<span class="' + cls + '">' + icon + '</span>'
        + '<span class="task-subject" title="' + esc(t.subject) + '">' + esc(t.subject) + '</span>';
      sdkTaskListEl.appendChild(div);
    }
  }

  const todoListEl = document.getElementById('todo-list');
  const todoCountEl = document.getElementById('todo-count');

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
  var toolStartTimes = {};
  var toolPanels = {};    // tool_use_id → panel DOM
  var readingEl = null;
  var thinkingPanels = {};     // msgIndex -> panel element (streaming)
  var currentThinkingIdx = -1;
  var currentStreamAgent = '';
  var hasStreamedText = false;
  var streamedIndices = {};  // stream_event 已渲染的 index → true，assistant 中跳过
  var discarding = false;   // 中断后丢弃中间消息
  var subAgentGroups = {};   // agentType -> { panel, body, count }

  // ===== Sub-agent group =====
  function getSubAgentGroup(agentType) {
    var key = agentType.toLowerCase();
    if (subAgentGroups[key]) {
      subAgentGroups[key].count++;
      var hdr = subAgentGroups[key].panel.querySelector('.sa-count');
      if (hdr) hdr.textContent = subAgentGroups[key].count + ' 条';
      return subAgentGroups[key];
    }
    var panel = document.createElement('div');
    panel.className = 'sa-group';
    var header = document.createElement('div');
    header.className = 'sa-group-header';
    var arrow = document.createElement('span');
    arrow.className = 'tgl-arrow';
    arrow.textContent = '▾';
    header.appendChild(arrow);
    var label = document.createElement('span');
    label.textContent = '子代理 · ' + (agentType || 'sub');
    header.appendChild(label);
    var count = document.createElement('span');
    count.className = 'sa-count';
    count.textContent = '1 条';
    header.appendChild(count);
    header.addEventListener('click', function() {
      var collapsed = panel.classList.toggle('collapsed');
      arrow.textContent = collapsed ? '▸' : '▾';
      scrollDeferred();
    });
    panel.appendChild(header);
    var body = document.createElement('div');
    body.className = 'sa-group-body';
    panel.appendChild(body);
    // 将匹配的 Agent tool_use 面板移入分组顶部
    var tp = toolPanels[agentType];
    if (tp) { tp.remove(); body.appendChild(tp); }
    messagesEl.appendChild(panel);
    subAgentGroups[key] = { panel: panel, body: body, count: 1, lastBody: null };
    return subAgentGroups[key];
  }

  // ===== Thinking panel helpers =====
  function createThinkingPanel(text, idx) {
    var panel = document.createElement('div');
    panel.className = 'msg-thinking';
    panel.dataset.tidx = String(idx);

    var header = document.createElement('div');
    header.className = 'th-header';
    header.innerHTML = '<span class="th-icon">&#9660;</span>'
      + '<span class="th-label">思考中...</span>'
      + '<span class="th-status"></span>'
      + '<span class="th-cursor"></span>';
    header.addEventListener('click', function() {
      panel.classList.toggle('collapsed');
    });
    panel.appendChild(header);

    var body = document.createElement('div');
    body.className = 'th-body';
    body.textContent = text || '';
    panel.appendChild(body);

    messagesEl.appendChild(panel);
    return panel;
  }

  function finalizeThinkingPanel(panel) {
    if (!panel || panel.classList.contains('done')) return;
    panel.classList.add('done');
    var label = panel.querySelector('.th-label');
    if (label) label.textContent = '思考过程';
    var cursor = panel.querySelector('.th-cursor');
    if (cursor) cursor.style.display = 'none';
    panel.classList.add('collapsed');
  }

  function appendThinkingDelta(panel, delta) {
    if (!panel) return;
    var body = panel.querySelector('.th-body');
    if (body) body.textContent += delta;
  }

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
      try { const msg = JSON.parse(event.data); handleMessage(msg); } catch {}
    };

    ws.onclose = () => {
      connected = false;
      setStatus('disconnected');
      sendBtn.disabled = true;
      hideReading();
      scheduleReconnect();
    };

    ws.onerror = () => { if (ws) ws.close(); };
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
      colonyNameEl.textContent = data.colonyName;
    }
    if (data.colonistCount !== undefined && sbPawns) {
      sbPawns.textContent = data.colonistCount;
    }
    if (data.avgMood !== undefined && sbMood) {
      var mood = Number(data.avgMood);
      sbMood.textContent = mood + '%';
      sbMood.className = 'sb-val' + (mood < 30 ? ' danger' : mood < 50 ? ' warning' : ' ok');
    }
    if (data.foodDays !== undefined && sbFood) {
      var fd = Number(data.foodDays);
      sbFood.textContent = fd + 'd';
      sbFood.className = 'sb-val' + (fd < 1 ? ' danger' : fd < 3 ? ' warning' : ' ok');
    }
  }

  function updateAgentRole(role) {
    var el = document.getElementById('agent-role');
    if (el) el.textContent = role || '休眠中';
  }

  function updateBudgetStatus(data) {
    if (!hdrBudget) return;
    var limit = data.limit || 0;
    var used = data.used || 0;
    var fmt = function(v) { return v >= 1e6 ? (v/1e6).toFixed(1)+'M' : v >= 1e3 ? (v/1e3).toFixed(0)+'K' : String(v); };
    if (limit <= 0) {
      hdrBudget.innerHTML = 'Token ' + (used > 0 ? fmt(used) : '--');
      return;
    }
    var pct = used / limit * 100;
    var barCls = pct >= 95 ? ' danger' : pct >= 80 ? ' warning' : '';
    hdrBudget.innerHTML = 'Token ' + fmt(used) + '/' + fmt(limit) +
      ' <span class="budget-bar' + barCls + '"></span>';
  }

  function updateToolCount() {
    if (metaTools) metaTools.textContent = toolCount;
  }

  // ===== TODO Panel =====
  function updateTodoPanel(items) {
    var pendingCount = 0;
    todoListEl.innerHTML = '';
    if (!items || items.length === 0) {
      todoCountEl.textContent = '0';
      return;
    }
    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      var isDone = item.status === 'done';
      var isCancelled = item.status === 'cancelled';
      if (!isDone && !isCancelled) pendingCount++;

      var div = document.createElement('div');
      var extraCls = isDone ? ' done' : isCancelled ? ' cancelled' : '';
      div.className = 'todo-item' + extraCls;

      var prio = document.createElement('span');
      var pLevel = item.priority >= 4 ? 'p-high' : item.priority >= 2 ? 'p-mid' : 'p-low';
      prio.className = 'todo-prio ' + pLevel;
      prio.textContent = 'P' + (item.priority || 3);
      div.appendChild(prio);

      var desc = document.createElement('span');
      desc.className = 'todo-desc';
      desc.textContent = item.description || '';
      desc.title = (item.createdAtStr ? item.createdAtStr + ' — ' : '') + (item.description || '');
      div.appendChild(desc);

      var idSpan = document.createElement('span');
      idSpan.className = 'todo-id';
      idSpan.textContent = '#' + (item.id || '');
      div.appendChild(idSpan);

      todoListEl.appendChild(div);
    }
    todoCountEl.textContent = pendingCount;
  }

  // ===== Message handling =====
  function handleMessage(msg) {
    // 历史加载期间缓冲 WS 消息，防止乱序
    if (loadingHistory) { pendingWs.push(msg); return; }
    // 中断后丢弃中间消息直到 result
    if (discarding && msg.type !== 'result' && msg.type !== 'aborted') return;
    // assistant 消息按 uuid 去重
    if (msg.type === 'assistant' && msg.uuid) {
      if (seenUuids[msg.uuid]) return;
      seenUuids[msg.uuid] = true;
    }
    switch (msg.type) {
      case 'hello-ok':
        connected = true;
        setStatus('connected');
        sendBtn.disabled = false;
        hideReading();
        loadHistory();
        break;

      case 'agent-status':
        updateAgentRole(msg.role);
        break;

      case 'colony-stats':
        updateColonyStats(msg);
        break;

      case 'budget-status':
        updateBudgetStatus(msg);
        break;

      case 'todo-state':
        updateTodoPanel(msg.todoItems || []);
        break;

      case 'sdk-tasks':
        if (msg.tasks && msg.tasks.length) {
          sdkTasks = msg.tasks;
          renderSdkTasks();
        }
        break;

      case 'assistant':
      case 'user': {
        const isAssistant = msg.type === 'assistant';
        const content = msg.message?.content;
        if (!content) return;
        if (isAssistant) { hideReading(); setAbort(true); }
        var saType = msg.agent_type || (msg.parent_tool_use_id ? 'sub:' + msg.parent_tool_use_id.slice(0, 8) : '');
        addMessage(isAssistant, content, saType);
        break;
      }

      case 'model-info': {
        var mel = document.getElementById('info-model');
        if (mel) mel.textContent = msg.model || '?';
        break;
      }

      case 'system': {
        if (msg.subtype === 'init') updateSdkInfo(msg);
        break;
      }

      case 'stream_event': {
        setAbort(true);
        var evt = msg.event || {};
        var evtType = evt.type;
        // content_block_start
        if (evtType === 'content_block_start') {
          var block = evt.content_block || {};
          var idx = evt.index;
          if (block.type === 'thinking') {
            streamedIndices[idx] = true;
            var panel = createThinkingPanel('', idx);
            if (currentStreamAgent) {
              panel.remove();
              getSubAgentGroup(currentStreamAgent).body.appendChild(panel);
            }
            thinkingPanels[idx] = panel;
            currentThinkingIdx = idx;
          } else if (block.type === 'text') {
            hasStreamedText = true;
            // thinking done, finalize previous thinking panel
            if (currentThinkingIdx >= 0) {
              finalizeThinkingPanel(thinkingPanels[currentThinkingIdx]);
              currentThinkingIdx = -1;
            }
            hideReading();
          } else if (block.type === 'tool_use') {
            if (currentThinkingIdx >= 0) {
              finalizeThinkingPanel(thinkingPanels[currentThinkingIdx]);
              currentThinkingIdx = -1;
            }
            toolCount++;
            updateToolCount();
            trackSdkTask(block.name, block.input);
            toolStartTimes[block.id] = Date.now();
            var tup3 = makeToolUsePanel(block.name, block.input, block.id);
            if (currentStreamAgent) {
              var sg = getSubAgentGroup(currentStreamAgent);
              tup3.remove(); sg.body.appendChild(tup3);
            }
            lastAgentBody = null;
          }
        }
        // content_block_delta
        else if (evtType === 'content_block_delta') {
          var delta = evt.delta || {};
          var dIdx = evt.index;
          if (delta.type === 'thinking_delta') {
            appendThinkingDelta(thinkingPanels[dIdx], delta.thinking || '');
          } else if (delta.type === 'text_delta') {
            // text delta: show in agent panel (streaming)
            if (currentThinkingIdx >= 0) {
              finalizeThinkingPanel(thinkingPanels[currentThinkingIdx]);
              currentThinkingIdx = -1;
            }
            hideReading();
            hasStreamedText = true;
            var textContent = delta.text || '';
            var sg2 = currentStreamAgent ? subAgentGroups[currentStreamAgent.toLowerCase()] : null;
            var activeBody = sg2 ? sg2.lastBody : lastAgentBody;
            if (activeBody) {
              activeBody.textContent += textContent;
              scrollDeferred();
            } else {
              var subLabel = currentStreamAgent || null;
              var p = makeAgentPanel(textContent, subLabel);
              if (sg2) { p.remove(); sg2.body.appendChild(p); }
              var newBody = p.querySelector('.msg-body');
              if (sg2) sg2.lastBody = newBody; else lastAgentBody = newBody;
            }
          }
        }
        // message_start
        else if (evtType === 'message_start') {
          lastAgentBody = null;
          hasStreamedText = false;
          streamedIndices = {};
          currentStreamAgent = (msg.parent_tool_use_id || msg.agent_type || evt.message?.agent_type || '');
        }
        // message_delta — usage info only, ignore
        break;
      }

      case 'aborted':
        discarding = false;
        abortBtn.disabled = false;
        abortBtn.textContent = '中断';
        setAbort(false);
        hideReading();
        break;

      case 'result': {
        discarding = false;
        abortBtn.disabled = false;
        abortBtn.textContent = '中断';
        setAbort(false);
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
    el = document.getElementById('info-apikey'); if (el) el.textContent = msg.apiKeySource || '-';
    el = document.getElementById('info-mcp'); if (el) {
      var mcps = msg.mcp_servers || [];
      el.innerHTML = mcps.length
        ? mcps.map(function(s) { return '<div>' + esc(s.name) + ' <span style="color:var(--green)">' + esc(s.status) + '</span></div>'; }).join('')
        : '-';
    }
    el = document.getElementById('info-tools'); if (el) {
      var tools = msg.tools || [];
      el.innerHTML = tools.length
        ? tools.map(function(t) { return '<div>' + esc(t) + '</div>'; }).join('')
        : '-';
    }
    el = document.getElementById('info-skills'); if (el) {
      var skills = msg.skills || [];
      el.innerHTML = skills.length
        ? skills.map(function(s) { return '<div>' + esc(s) + '</div>'; }).join('')
        : '-';
    }
  }

  // ===== Panel builders =====
  function makeUserPanel(text) {
    var panel = document.createElement('div');
    panel.className = 'msg msg-user';

    var label = document.createElement('div');
    label.className = 'msg-label';
    label.textContent = 'USER';
    panel.appendChild(label);

    var body = document.createElement('div');
    body.className = 'msg-body';
    body.textContent = text;
    panel.appendChild(body);

    messagesEl.appendChild(panel);
    return panel;
  }

  function makeAgentPanel(text, agentType) {
    var panel = document.createElement('div');
    panel.className = 'msg msg-agent';
    if (agentType) panel.classList.add('sub-agent');

    var label = document.createElement('div');
    label.className = 'msg-label';
    if (agentType) {
      label.style.cursor = 'pointer';
      var arrow = document.createElement('span');
      arrow.className = 'tgl-arrow';
      arrow.textContent = '▾';
      label.appendChild(arrow);
      var lt = document.createElement('span');
      lt.textContent = agentType.toUpperCase();
      label.appendChild(lt);
      // header click toggles body
      label.addEventListener('click', function() {
        var b = panel.querySelector('.msg-body');
        var hidden = b.style.display === 'none';
        b.style.display = hidden ? '' : 'none';
        arrow.textContent = hidden ? '▾' : '▸';
        scrollDeferred();
      });
    } else {
      label.textContent = 'AGENT';
    }
    panel.appendChild(label);

    var body = document.createElement('div');
    body.className = 'msg-body';
    body.textContent = text;
    panel.appendChild(body);

    if (agentType) {
      var group = getSubAgentGroup(agentType);
      group.body.appendChild(panel);
    } else {
      messagesEl.appendChild(panel);
    }
    scrollDeferred();
    return panel;
  }

  function makeToolUsePanel(name, input, toolId) {
    var argsStr = '';
    try { argsStr = JSON.stringify(input, null, 2); } catch(e) { argsStr = String(input || ''); }
    // 已有同 toolId 的面板 → 更新 input 内容
    if (toolId && toolPanels[toolId]) {
      var existing = toolPanels[toolId];
      var eb2 = existing.querySelector('.msg-body');
      if (eb2 && argsStr) {
        var ew = eb2.querySelector('.tool-output-wrap') || eb2;
        if (argsStr.length > 120) ew.classList.add('truncated');
        ew.textContent = argsStr;
        // 重建展开按钮
        var oldBtn = existing.querySelector('.tool-expand-btn');
        if (oldBtn) oldBtn.remove();
        if (argsStr.length > 120) {
          var newBtn = document.createElement('button');
          newBtn.className = 'tool-expand-btn';
          newBtn.textContent = '展开';
          newBtn.style.display = eb2.style.display === 'none' ? 'none' : '';
          (function(w, fullText, btn) {
            btn.addEventListener('click', function(e) {
              e.stopPropagation();
              var isExp = w.classList.toggle('expanded');
              w.classList.toggle('truncated', !isExp);
              btn.textContent = isExp ? '收起' : '展开';
              if (isExp) w.textContent = fullText;
              scrollDeferred();
            });
          })(ew, argsStr, newBtn);
          existing.appendChild(newBtn);
        }
      }
      return existing;
    }
    var panel = document.createElement('div');
    panel.className = 'msg msg-tool';
    if (toolId) { panel.dataset.toolId = toolId; toolPanels[toolId] = panel; }

    var header = document.createElement('div');
    header.className = 'msg-header';
    header.style.cursor = 'pointer';

    var arrow = document.createElement('span');
    arrow.className = 'tgl-arrow';
    arrow.textContent = '▸';
    header.appendChild(arrow);

    var hlabel = document.createElement('span');
    hlabel.className = 'msg-label';
    hlabel.textContent = 'TOOL';
    header.appendChild(hlabel);

    var hname = document.createElement('span');
    hname.className = 'tool-name';
    hname.textContent = name || '?';
    header.appendChild(hname);

    var statusEl = document.createElement('span');
    statusEl.className = 'tool-status running';
    statusEl.textContent = '执行中...';
    header.appendChild(statusEl);

    panel.appendChild(header);

    // 始终创建 body（input 为空时占位，等后续 assistant 消息更新）
    var body = document.createElement('div');
    body.className = 'msg-body';
    body.style.display = 'none';
    panel.appendChild(body);
    var hasContent = argsStr && argsStr !== '{}';
    if (hasContent) {
      var wrap = document.createElement('div');
      wrap.className = 'tool-output-wrap';
      if (argsStr.length > 120) wrap.classList.add('truncated');
      wrap.textContent = argsStr || '(no args)';

      body.appendChild(wrap);

      // Expand button for long content
      var expandBtn = null;
      if (argsStr.length > 120) {
        expandBtn = document.createElement('button');
        expandBtn.className = 'tool-expand-btn';
        expandBtn.textContent = '展开';
        expandBtn.style.display = 'none';
        (function(w, fullText, btn) {
          btn.addEventListener('click', function(e) {
            e.stopPropagation();
            var isExp = w.classList.toggle('expanded');
            w.classList.toggle('truncated', !isExp);
            btn.textContent = isExp ? '收起' : '展开';
            if (isExp) w.textContent = fullText;
            scrollDeferred();
          });
        })(wrap, argsStr, expandBtn);
        panel.appendChild(expandBtn);
      }

    } // if (hasContent)

    // 始终加 click handler（body 永存，后续可能更新内容）
    header.addEventListener('click', function() {
      var bd = panel.querySelector('.msg-body');
      if (!bd) return;
      var hidden = bd.style.display === 'none';
      bd.style.display = hidden ? '' : 'none';
      var a = panel.querySelector('.tgl-arrow');
      if (a) a.textContent = hidden ? '▾' : '▸';
      var allBtns = panel.querySelectorAll('.tool-expand-btn');
      for (var bi = 0; bi < allBtns.length; bi++)
        allBtns[bi].style.display = hidden ? '' : 'none';
      scrollDeferred();
    });

    messagesEl.appendChild(panel);
    scrollDeferred();
    return panel;
  }

  function applyToolResult(block, timing) {
    var toolId = block.tool_use_id || block.id;
    var panel = toolPanels[toolId];
    if (panel) {
      // 更新状态
      var statusEl = panel.querySelector('.tool-status');
      if (statusEl) {
        statusEl.className = 'tool-status ' + (block.is_error ? 'error' : 'done');
        statusEl.textContent = block.is_error ? '✗ 失败' : '✓ ' + (timing ? timing + 'ms' : '完成');
      }
      // 追加结果到 body
      var resultText = '';
      if (typeof block.content === 'string') resultText = block.content;
      else if (Array.isArray(block.content)) resultText = block.content.map(function(c) { return c.text || ''; }).join('\\\\n');
      if (resultText) {
        var body = panel.querySelector('.msg-body');
        if (!body) {
          body = document.createElement('div');
          body.className = 'msg-body';
          body.style.display = 'none';
          panel.appendChild(body);
        }
        var wrap = document.createElement('div');
        wrap.className = 'tool-result-text';
        if (resultText.length > 120) wrap.classList.add('truncated');
        if (block.is_error) wrap.style.color = 'var(--red)';
        wrap.textContent = resultText;
        body.appendChild(wrap);

        // 长结果加展开按钮
        if (resultText.length > 120) {
          var expandBtn = document.createElement('button');
          expandBtn.className = 'tool-expand-btn';
          expandBtn.textContent = '展开';
          (function(w, fullText, btn) {
            btn.addEventListener('click', function(e) {
              e.stopPropagation();
              var isExp = w.classList.toggle('expanded');
              w.classList.toggle('truncated', !isExp);
              btn.textContent = isExp ? '收起' : '展开';
              if (isExp) w.textContent = fullText;
              scrollDeferred();
            });
          })(wrap, resultText, expandBtn);
          panel.appendChild(expandBtn);
        }
      }
      // 展开面板
      var arrow = panel.querySelector('.tgl-arrow');
      if (arrow) arrow.textContent = '▾';
      var bd = panel.querySelector('.msg-body');
      if (bd) bd.style.display = '';
    } else {
      // 没有匹配的工具面板 → 独立渲染（历史回放场景）
      makeToolResultPanel(block, timing);
    }
  }

  function makeToolResultPanel(block, timing) {
    var panel = document.createElement('div');
    panel.className = 'msg msg-tool';

    var header = document.createElement('div');
    header.className = 'msg-header';
    header.style.cursor = 'pointer';

    var arrow = document.createElement('span');
    arrow.className = 'tgl-arrow';
    arrow.textContent = '▸';
    header.appendChild(arrow);

    var hlabel = document.createElement('span');
    hlabel.className = 'msg-label';
    hlabel.textContent = 'TOOL';
    header.appendChild(hlabel);

    var hname = document.createElement('span');
    hname.className = 'tool-name';
    hname.textContent = block.name || '?';
    header.appendChild(hname);

    if (timing !== undefined && timing !== null) {
      var htime = document.createElement('span');
      htime.className = 'tool-timing' + (block.isError ? ' error' : '');
      htime.textContent = (block.isError ? '✗ ' : '✓ ') + timing + 'ms';
      header.appendChild(htime);
    }

    panel.appendChild(header);

    // Extract content
    var content = '';
    if (typeof block.content === 'string') content = block.content;
    else if (Array.isArray(block.content)) content = block.content.map(function(c) { return c.text || ''; }).join(String.fromCharCode(10));
    else if (block.content) { try { content = JSON.stringify(block.content, null, 2); } catch(e) { content = String(block.content); } }

    if (content) {
      var body = document.createElement('div');
      body.className = 'msg-body';
      body.style.display = 'none';

      var wrap = document.createElement('div');
      wrap.className = 'tool-output-wrap';
      if (content.length > 120) wrap.classList.add('truncated');
      if (block.isError) wrap.style.color = 'var(--red)';
      wrap.textContent = content;

      body.appendChild(wrap);
      panel.appendChild(body);

      // Expand button for longer content
      var expandBtn = null;
      if (content.length > 120) {
        expandBtn = document.createElement('button');
        expandBtn.className = 'tool-expand-btn';
        expandBtn.textContent = '展开';
        expandBtn.style.display = 'none';
        (function(w, fullText, btn) {
          btn.addEventListener('click', function(e) {
            e.stopPropagation();
            var isExp = w.classList.toggle('expanded');
            w.classList.toggle('truncated', !isExp);
            btn.textContent = isExp ? '收起' : '展开';
            if (isExp) w.textContent = fullText;
            scrollDeferred();
          });
        })(wrap, content, expandBtn);
        panel.appendChild(expandBtn);
      }

      // Header click toggles body + arrow
      header.addEventListener('click', function() {
        var hidden = body.style.display === 'none';
        body.style.display = hidden ? '' : 'none';
        if (expandBtn) expandBtn.style.display = hidden ? '' : 'none';
        arrow.textContent = hidden ? '▾' : '▸';
        scrollDeferred();
      });
    }

    messagesEl.appendChild(panel);
    scrollDeferred();
    return panel;
  }

  // ===== Add Message =====
  var lastAgentBody = null;

  function addMessage(isAssistant, content, agentType) {
    if (typeof content === 'string') {
      if (isAssistant && !agentType && lastAgentBody) {
        // Append to existing agent panel (streaming)
        lastAgentBody.textContent += '\\n' + content;
      } else if (isAssistant) {
        var p = makeAgentPanel(content, agentType);
        lastAgentBody = p.querySelector('.msg-body');
      } else {
        makeUserPanel(content);
        lastAgentBody = null;
      }
    } else if (Array.isArray(content)) {
      var saTarget = agentType ? getSubAgentGroup(agentType).body : null;
      for (var i = 0; i < content.length; i++) {
        var block = content[i];
        if (block.type === 'text') {
          if (hasStreamedText) continue;
          var lastMsg = messagesEl.lastElementChild;
          if (lastMsg && lastMsg.classList.contains('msg-agent') && !lastMsg.classList.contains('sub-agent') && lastAgentBody) {
            lastAgentBody.textContent += '\\n' + (block.text || '');
          } else {
            var tp = makeAgentPanel(block.text || '', agentType || null);
            lastAgentBody = tp.querySelector('.msg-body');
          }
        } else if (block.type === 'thinking') {
          if (streamedIndices[i]) continue;
          var thText = block.thinking || '';
          if (thText) {
            var thPanel = createThinkingPanel(thText, i);
            if (saTarget) { thPanel.remove(); saTarget.appendChild(thPanel); }
            finalizeThinkingPanel(thPanel);
            lastAgentBody = null;
          }
        } else if (block.type === 'tool_use') {
          trackSdkTask(block.name, block.input);
          toolStartTimes[block.id] = Date.now();
          var tup2 = makeToolUsePanel(block.name, block.input, block.id);
          if (saTarget) { tup2.remove(); saTarget.appendChild(tup2); }
          lastAgentBody = null;
        } else if (block.type === 'tool_result') {
          var elapsed = null;
          if (block.id && toolStartTimes[block.id] !== undefined) {
            elapsed = Date.now() - toolStartTimes[block.id];
            delete toolStartTimes[block.id];
          }
          applyToolResult(block, elapsed);
          lastAgentBody = null;
        }
      }
    }
    scrollDeferred();
  }

  // ===== Result divider =====
  function addResult(cls, label) {
    var div = document.createElement('div');
    div.className = 'msg-result';
    div.innerHTML = '<span class="result-line"></span>'
      + '<span class="result-label ' + cls + '">' + label + '</span>'
      + '<span class="result-line"></span>';
    messagesEl.appendChild(div);
    scrollDeferred();
  }

  // ===== Typing indicator =====
  function showReading() {
    if (readingEl) return;
    readingEl = document.createElement('div');
    readingEl.className = 'typing-indicator';
    readingEl.innerHTML = '<span class="typing-dot"></span><span class="typing-dot"></span><span class="typing-dot"></span>';
    messagesEl.appendChild(readingEl);
    scrollDeferred();
  }

  function hideReading() {
    if (readingEl) { readingEl.remove(); readingEl = null; }
  }

  // ===== Auto-scroll =====
  var userScrolledUp = false;
  var scrollRaf = 0;
  var autoScrolling = false;
  var scrollEventTimer = 0;


  function scrollNow() {
    cancelAnimationFrame(scrollRaf);
    autoScrolling = true;
    messagesEl.scrollTop = messagesEl.scrollHeight;
    scrollRaf = 0;
    requestAnimationFrame(function() { autoScrolling = false; });
  }

  function scrollDeferred() {
    if (userScrolledUp) return;
    if (scrollRaf) return;
    scrollRaf = requestAnimationFrame(function() {
      scrollRaf = 0;
      if (!userScrolledUp) scrollNow();
    });
  }

  function checkScroll() {
    if (userScrolledUp) {
      var diff2 = messagesEl.scrollHeight - messagesEl.clientHeight - messagesEl.scrollTop;
      if (diff2 < 6) {
        userScrolledUp = false;
        newMsgPill.style.display = 'none';
      }
      return;
    }
  }

  messagesEl.addEventListener('scroll', function() {
    if (userScrolledUp || autoScrolling) return;
    clearTimeout(scrollEventTimer);
    scrollEventTimer = setTimeout(function() {
      if (autoScrolling) return;
      var diff = messagesEl.scrollHeight - messagesEl.clientHeight - messagesEl.scrollTop;
      if (diff > 50) {
        userScrolledUp = true;
        newMsgPill.style.display = 'block';
      }
    }, 150);
  }, { passive: true });
  function scrollToBottom() {
    userScrolledUp = false;
    newMsgPill.style.display = 'none';
    scrollNow();
  }
  window.scrollToBottom = scrollToBottom;

  // ===== History =====
  var loadingHistory = false;
  var pendingWs = [];
  var seenUuids = {};

  function loadHistory() {
    if (loadingHistory) return;
    loadingHistory = true;
    fetch('/history?n=30').then(function(r) { return r.json(); }).then(function(msgs) {
      messagesEl.innerHTML = '';
      toolCount = 0; sdkTasks = []; updateToolCount(); renderSdkTasks();
      if (!msgs || !msgs.length) return;
      for (var i = 0; i < msgs.length; i++) {
        var msg = msgs[i];
        if (msg.type === 'assistant' || msg.type === 'user') {
          if (msg.uuid) seenUuids[msg.uuid] = true;
          addMsgSimple(msg);
        } else if (msg.type === 'result') {
          var cls = msg.subtype === 'success' ? 'ok' : msg.subtype === 'aborted' ? 'warn' : 'error';
          var label = msg.subtype === 'success' ? '✓ Done' : msg.subtype === 'aborted' ? '⏹ Aborted' : '✗ Failed';
          addResult(cls, label);
        }
      }
      scrollDeferred();
    }).catch(function(e) { console.error('[history] fetch 失败', e); }).then(function() {
      loadingHistory = false;
      if (pendingWs.length) {
        var queued = pendingWs.splice(0);
        for (var k = 0; k < queued.length; k++) handleMessage(queued[k]);
      }
    });
  }

  function addMsgSimple(msg) {
    var isAssistant = msg.type === 'assistant';
    var content = msg.message?.content;
    if (!content) return;
    if (typeof content === 'string') {
      if (isAssistant) makeAgentPanel(content, msg.agent_type);
      else makeUserPanel(content);
    } else if (Array.isArray(content)) {
      for (var j = 0; j < content.length; j++) {
        var block = content[j];
        if (block.type === 'text') {
          if (isAssistant) makeAgentPanel(block.text || '', null);
          else makeUserPanel(block.text || '');
        } else if (block.type === 'thinking') {
          var thPanel = createThinkingPanel(block.thinking || '', j);
          finalizeThinkingPanel(thPanel);
        } else if (block.type === 'tool_use') {
          makeToolUsePanel(block.name, block.input, block.id);
        } else if (block.type === 'tool_result') {
          applyToolResult(block, null);
        }
      }
    }
    scrollDeferred();
  }

  // ===== Send =====
  function sendMessage() {
    const text = inputEl.value.trim();
    if (!text || !connected) return;
    ws.send(JSON.stringify({
      type: 'event', event: 'chat',
      payload: { text: text },
      thinking: { mode: currentMode, effort: currentEffort, tokens: currentTokens },
    }));
    inputEl.value = '';
    adjustHeight();
    showReading();
  }

  function adjustHeight() {
    inputEl.style.height = 'auto';
    var newH = Math.min(Math.max(inputEl.scrollHeight, 44), 160);
    inputEl.style.height = newH + 'px';
  }

  sendBtn.addEventListener('click', sendMessage);
  abortBtn.addEventListener('click', function() {
    if (!ws || !connected || discarding) return;
    ws.send(JSON.stringify({ type: 'abort' }));
    discarding = true;
    abortBtn.disabled = true;
    abortBtn.textContent = '中断中...';
  });

  function setAbort(enabled) {
    if (discarding) return; // 中断进行中不改变状态
    if (enabled) {
      abortBtn.classList.add('enabled');
      abortBtn.disabled = false;
      abortBtn.textContent = '中断';
    } else {
      abortBtn.classList.remove('enabled');
    }
  }

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
