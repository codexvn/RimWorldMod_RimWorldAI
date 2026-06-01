using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    /// <summary>
    /// 聊天频道常量。C# 与 TS companion protocol.ts ChatMessage.session 对齐。
    /// </summary>
    public static class ChatChannel
    {
        /// <summary>BridgeBus 转发的用户消息</summary>
        public const string Bus = "bus";
        /// <summary>AgentLoop 系统 prompt（RunSessionAsync）</summary>
        public const string System = "system";
    }

/// <summary>CC Companion WebSocket 客户端 — 心跳/重连/Token提取/hello/abort</summary>
public class CcbWebSocket : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _url = "";
    private string _token = "";
    private CcbClientState _state = CcbClientState.Disconnected;

    private TaskCompletionSource<bool>? _helloOk;
    private DateTime _lastPong = DateTime.MinValue;
    private const int PingIntervalMs = 30000;
    private const int PongTimeoutMs = 45000;
    private DateTime _lastPing = DateTime.MinValue;
    private System.Threading.Timer? _heartbeatTimer;

    private int _reconnectDelayMs = 5000;
    private int _reconnectAttempts;
    private bool _reconnecting;
    private volatile bool _shuttingDown;
    private const int MaxReconnectDelayMs = 60000;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _eventLock = new(1, 1);

    /// <summary>思考模式：default / disabled / adaptive / fixed</summary>
    public string ThinkingMode { get; set; } = "default";
    /// <summary>思考力度：low / medium / high</summary>
    public string ThinkingEffort { get; set; } = "medium";
    /// <summary>最大思考 Token 数（0 = 默认）</summary>
    public int MaxThinkingTokens { get; set; }

    public CcbClientState State => _state;
    public bool IsConnected => _state >= CcbClientState.Connected;
    public bool IsReady => _state == CcbClientState.Ready;
    public bool IsSendingMessage { get; private set; }

    /// <summary>收到 Claude 文本回复时触发</summary>
    public event Action<string>? OnAssistantText;
    /// <summary>收到 Claude 思考内容时触发</summary>
    public event Action<string>? OnAssistantThinking;
    /// <summary>收到 tool_use 请求时触发</summary>
    public event Action<string, string, JsonElement?>? OnToolUse;
    /// <summary>回合结束</summary>
    public event Action<string, string?>? OnResult;
    /// <summary>收到中断确认</summary>
    public event Action? OnAborted;
    /// <summary>收到系统通知（中断摘要）</summary>
    public event Action<string>? OnSystemNotification;
    /// <summary>SDK 消息（已解析），供 AgentCore 消费</summary>
    public event Action<SdkMessage>? OnSdkMessage;

    public CcbWebSocket(string url = "ws://localhost:19998", string token = "")
    {
        _url = url;
        _token = token;
    }

    // ========== 连接管理 ==========

    public async Task<bool> ConnectAsync(int timeoutMs = 10000)
    {
        Disconnect();
        _shuttingDown = false;

        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _cts = new CancellationTokenSource();
            _helloOk = new TaskCompletionSource<bool>();
            _state = CcbClientState.Connecting;

            await _ws.ConnectAsync(new Uri(_url), _cts.Token);
            _state = CcbClientState.Connected;
            CoreLog.Info($"[CCGUI_DEBUG] WS 已连接: {_url}");

            await SendHello();
            CoreLog.Info($"[CCGUI_DEBUG] hello 已发送, 等待 hello-ok...");
            _ = ReceiveLoop(_cts.Token);

            var timeout = Task.Delay(timeoutMs);
            if (await Task.WhenAny(_helloOk.Task, timeout) == _helloOk.Task)
            {
                _state = CcbClientState.Ready;
                CoreLog.Info("[CCGUI_DEBUG] hello-ok 收到, 状态=Ready");
                _reconnectAttempts = 0;
                _reconnectDelayMs = 5000;
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = new System.Threading.Timer(_ => Heartbeat(), null, 5000, 5000);
                return true;
            }
            CoreLog.Error("[CcbWS] hello-ok 超时");
            return false;
        }
        catch (Exception ex)
        {
            _state = CcbClientState.Disconnected;
            CoreLog.Error($"[CcbWS] 连接失败: {ex.Message}");
            _ = ScheduleReconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _shuttingDown = true;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _cts?.Cancel();
        _state = CcbClientState.Disconnected;
        try { _ws?.Dispose(); } catch (Exception ex) { CoreLog.Info($"[CcbWS] Dispose WebSocket 异常: {ex.Message}"); }
        _ws = null;
        _lastPing = DateTime.MinValue;
        _lastPong = DateTime.MinValue;
    }

    // ========== 消息发送 ==========

    /// <summary>发送聊天消息（用户 prompt），附带 session 标识和思考配置。
    /// thinking 参数提供每消息覆盖，null 时使用全局设置。</summary>
    public async Task SendChat(string session, string text, BridgeBus.ChatThinking? thinking = null)
    {
        var mode = thinking?.Mode ?? ThinkingMode;
        var effort = thinking?.Effort ?? ThinkingEffort;
        var tokens = thinking?.Tokens ?? MaxThinkingTokens;
        if (string.IsNullOrEmpty(mode)) mode = "default";
        if (string.IsNullOrEmpty(effort)) effort = "medium";
        // tokens=0 表示不限制

        CoreLog.Info($"[CCGUI_DEBUG] CcbWS.SendChat session={session} mode={mode} effort={effort} tokens={tokens}");
        await SendEvent("chat", new
        {
            text,
            session,
            thinking = new
            {
                mode,
                effort,
                tokens
            }
        });
        CoreLog.Info($"[CCGUI_DEBUG] CcbWS.SendChat SendEvent done");
    }

    /// <summary>发送工具执行结果回 Claude</summary>
    public async Task SendToolResult(string toolUseId, string content, bool isError = false)
    {
        await SendEvent("tool_result", new
        {
            tool_use_id = toolUseId,
            content = new[] { new { type = "text", text = content } },
            is_error = isError
        });
    }

    /// <summary>发送游戏事件到 CC Companion</summary>
    public async Task SendEvent(string eventName, object payload)
    {
        if (!IsReady) { CoreLog.Info($"[CCGUI_DEBUG] CcbWS.SendEvent 跳过(event={eventName}) IsReady={IsReady}"); return; }
        CoreLog.Info($"[CCGUI_DEBUG] CcbWS.SendEvent event={eventName}");
        await _eventLock.WaitAsync();
        try
        {
            IsSendingMessage = true;
            await SendJson(new { type = "event", @event = eventName, payload });
        }
        finally
        {
            IsSendingMessage = false;
            _eventLock.Release();
        }
    }

    /// <summary>发送中断请求，中止当前 AI 回复</summary>
    public async Task SendAbort()
    {
        await _eventLock.WaitAsync();
        try
        {
            await SendJson(new { type = "abort" });
            CoreLog.Info("[CcbWS] 已发送中断请求");
        }
        finally { _eventLock.Release(); }
    }

    // ========== 内部 ==========

    private async Task SendHello()
    {
        await SendJson(new
        {
            type = "hello",
            client = new { name = "RimWorldAgent", version = "1.0" },
            auth = new { token = _token },
            thinking = new
            {
                mode = ThinkingMode,
                effort = ThinkingEffort,
                tokens = MaxThinkingTokens
            }
        });
    }

    private async Task SendJson(object obj)
    {
        await _sendLock.WaitAsync();
        try
        {
            var ws = _ws;
            if (ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                ProcessMessage(text);
            }
        }
        catch (OperationCanceledException) { CoreLog.Info("[CcbWS] 接收循环已取消"); }
        catch (WebSocketException ex) { CoreLog.Info($"[CcbWS] WS 断开: {ex.Message}"); }
        catch (Exception ex) { CoreLog.Error($"[CcbWS] 接收异常: {ex.Message}"); }

        _state = CcbClientState.Disconnected;
        if (!ct.IsCancellationRequested) _ = ScheduleReconnect();
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var msg = SdkMessage.FromJson(json);
            CoreLog.Info($"[CCGUI_DEBUG] ProcessMessage type={msg.Type}");
            OnSdkMessage?.Invoke(msg);

            // 内部事件分发（用类型化 SdkMessage）
            switch (msg)
            {
                case SdkHelloOkMessage _:
                    CoreLog.Info("[CCGUI_DEBUG] 收到 hello-ok, 设置 _helloOk");
                    _helloOk?.TrySetResult(true);
                    break;

                case SdkAssistantMessage am:
                    foreach (var b in am.Content)
                        if (b is SdkToolUseBlock tu)
                            OnToolUse?.Invoke(tu.Id, tu.Name, default);
                    break;

                case SdkUserMessage _:
                    break;

                case SdkStreamEventMessage sem:
                    OnAssistantTextOrThinking(sem);
                    break;

                case SdkResultMessage rm:
                    OnResult?.Invoke(rm.Subtype, rm.StopReason);
                    break;

                case SdkAbortedMessage _:
                    OnAborted?.Invoke();
                    break;

                case SdkSystemMessage sm:
                    if (sm.Subtype == "init")
                        TokenUsageTracker.CurrentModel = sm.Model ?? "";
                    break;

                case SdkUnknownMessage unk:
                    if (unk.Type == "system-notification")
                        OnSystemNotification?.Invoke(unk.Root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
                    break;
            }
        }
        catch (Exception ex) { CoreLog.Warn($"[CcbWS] 消息解析失败: {json.Substring(0, Math.Min(200, json.Length))} — {ex.Message}"); }
    }

    private void OnAssistantTextOrThinking(SdkStreamEventMessage msg)
    {
        var evt = msg.Event;
        if (evt == null) return;
        if (evt.EventType == "content_block_start")
        {
            if (evt.BlockType == "thinking")
                OnAssistantThinking?.Invoke("");
            else if (evt.BlockType == "text")
                OnAssistantText?.Invoke("");
        }
        else if (evt.EventType == "content_block_delta")
        {
            if (evt.Text != null)
                OnAssistantText?.Invoke(evt.Text);
            else if (evt.Thinking != null)
                OnAssistantThinking?.Invoke(evt.Thinking);
        }
    }

    // ========== 心跳 ==========

    private void Heartbeat()
    {
        if (!IsReady) return;
        var now = DateTime.UtcNow;
        var sinceLastPing = (now - _lastPing).TotalMilliseconds;
        var sinceLastPong = _lastPong == DateTime.MinValue ? -1 : (now - _lastPong).TotalMilliseconds;
        if (sinceLastPing > PingIntervalMs)
            _ = SendPing();

        if (_lastPong != DateTime.MinValue && sinceLastPong > PongTimeoutMs)
        {
            CoreLog.Error("[CcbWS] pong 超时，断开连接（将自动重连）");
            _state = CcbClientState.Disconnected;
            try { _ws?.CloseAsync(WebSocketCloseStatus.ProtocolError, "pong timeout", CancellationToken.None); } catch (Exception ex) { CoreLog.Info($"[CcbWS] 关闭 WS 异常: {ex.Message}"); }
        }
    }

    private async Task SendPing()
    {
        try { await SendJson(new { type = "keepalive" }); _lastPing = DateTime.UtcNow; _lastPong = DateTime.UtcNow; }
        catch (Exception ex) { CoreLog.Error($"[CcbWS] keepalive 失败: {ex.Message}"); }
    }

    // ========== 自动重连 ==========

    private async Task ScheduleReconnect()
    {
        if (_reconnecting || _shuttingDown) return;
        _reconnecting = true;
        try
        {
            while (true)
            {
                _reconnectAttempts++;
                var delay = Math.Min(_reconnectDelayMs * _reconnectAttempts, MaxReconnectDelayMs);
                CoreLog.Info($"[CcbWS] {delay / 1000}s 后重连 (第 {_reconnectAttempts} 次)...");
                await Task.Delay(delay);
                if (_shuttingDown) break;
                if (string.IsNullOrEmpty(_url)) break;
                try { await ConnectAsync(); } catch (Exception ex) { CoreLog.Info($"[CcbWS] 重连尝试异常: {ex.Message}"); }
                if (_state != CcbClientState.Disconnected) break;
            }
        }
        finally { _reconnecting = false; }
    }

    public void Dispose() => Disconnect();
}

public enum CcbClientState { Disconnected, Connecting, Connected, Ready }

}
