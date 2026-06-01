using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldAgent
{
    /// <summary>基于 System.Net.WebSockets 的 UIMessageBus WS 客户端，和 WebUI(index.html) 完全一致。</summary>
    public class BridgeClient : IDisposable
    {
        private const int RECONNECT_INTERVAL = 1000;

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _reconnectCts;
        private readonly string _url;
        private bool _isConnected;
        private bool _disposed;

        public bool IsConnected => _isConnected && _ws?.State == WebSocketState.Open;
        public event Action<string>? OnMessage;
        public event Action? OnConnectedChanged;

        public BridgeClient(string url = "ws://127.0.0.1:19999")
        {
            _url = url;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_disposed || IsConnected) return;
            try
            {
                CancelCurrentWs();
                _ws = new ClientWebSocket();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await _ws.ConnectAsync(new Uri(_url), _cts.Token);
                _isConnected = true;
                SafeLog.Info($"[BridgeClient] 已连接: {_url}");
                OnConnectedChanged?.Invoke();
                _ = ReceiveLoop(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isConnected = false;
                SafeLog.Warning($"[BridgeClient] 连接失败 ({_url}): {ex.Message}");
                if (!_disposed) ScheduleReconnect();
            }
        }

        public async Task SendChat(string text)
        {
            await SendJson(new { type = "chat", text });
        }

        public async Task SendAbort()
        {
            await SendJson(new { type = "abort" });
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    OnMessage?.Invoke(sb.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex) { SafeLog.Info($"[BridgeClient] 接收异常: {ex.Message}"); }
            finally
            {
                _isConnected = false;
                OnConnectedChanged?.Invoke();
                if (!_disposed) ScheduleReconnect();
            }
        }

        private async Task SendJson(object obj)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    _cts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { _isConnected = false; }
            catch (Exception ex) { SafeLog.Info($"[BridgeClient] 发送失败: {ex.Message}"); }
        }

        // ===== 自动重连（固定间隔） =====

        private void ScheduleReconnect()
        {
            if (_disposed) return;
            CancelReconnect();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RECONNECT_INTERVAL, token);
                    if (!_disposed) await ConnectAsync(token);
                }
                catch (OperationCanceledException) { }
            });
        }

        private void CancelReconnect()
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        private void CancelCurrentWs()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        public void Dispose()
        {
            _disposed = true;
            _isConnected = false;
            CancelReconnect();
            CancelCurrentWs();
        }
    }
}
