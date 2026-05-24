using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace RimWorldMCP
{
    public enum ClientState { Disconnected, Connecting, Handshake, Ready }

    public static class GatewayClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static string _password = "";
        private static int _rpcSeq;

        // 设备身份（ED25519），首次使用时生成
        private static Ed25519PrivateKeyParameters? _deviceKey;
        private static string? _deviceId;
        private static string? _devicePublicKeyBase64Url;
        private static byte[]? _devicePublicKeyRaw;
        private static ClientState _state = ClientState.Disconnected;
        private static TaskCompletionSource<bool>? _helloOk;
        private static int _tickIntervalMs = 30000; // 默认 30s，hello-ok 可能覆盖
        private static DateTime _lastTick = DateTime.MinValue;

        public static ClientState State => _state;
        public static bool IsConnected => _state >= ClientState.Handshake;
        public static bool IsReady => _state == ClientState.Ready;

        public static readonly ConcurrentQueue<string> Incoming = new();

        /// <summary>连接 Gateway</summary>
        public static async Task Connect(string wsUrl, string token, string password)
        {
            _url = wsUrl;
            _token = token ?? "";
            _password = password ?? "";
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _helloOk = new TaskCompletionSource<bool>();
                _state = ClientState.Connecting;

                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                _state = ClientState.Handshake;
                McpLog.Info($"[ws] 已连接: {wsUrl}");

                // 启动接收循环
                _ = ReceiveLoop(_cts.Token);

                // 等待 hello-ok (最多 15 秒)
                var timeout = Task.Delay(15000);
                var completed = await Task.WhenAny(_helloOk.Task, timeout);
                if (completed == _helloOk.Task && _helloOk.Task.Result)
                {
                    _state = ClientState.Ready;
                    McpLog.Info("[ws] 握手完成");
                }
                else
                {
                    McpLog.Warn("[ws] 握手超时");
                }
            }
            catch (Exception ex)
            {
                _state = ClientState.Disconnected;
                McpLog.Warn($"[ws] 连接失败: {ex.Message}");
            }
        }

        public static async Task SendMessage(string text)
        {
            if (!IsReady) return;
            await SendRpc("agent.send", new { text });
        }

        public static async Task SendRpc(string method, object? payload = null)
        {
            if (!IsReady) return;
            var id = (++_rpcSeq).ToString();
            await SendJson(new { type = "req", id, method, @params = payload });
        }

        public static async Task Ping()
        {
            if (_ws?.State == WebSocketState.Open)
                await SendJson(new { type = "ping" });
        }

        public static void Disconnect()
        {
            _cts?.Cancel();
            _state = ClientState.Disconnected;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        private static async Task SendJson(object obj)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            McpLog.Info($"[ws] → {Truncate(json)}");
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }

        private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 197) + "...";

        // Gateway 完整握手：收到 challenge → 发 connect RPC → 等 hello-ok
        private static void EnsureDeviceIdentity()
        {
            if (_deviceKey != null) return;

            var rng = RandomNumberGenerator.Create();
            var seed = new byte[32];
            rng.GetBytes(seed);
            _deviceKey = new Ed25519PrivateKeyParameters(seed, 0);
            var pubKey = _deviceKey.GeneratePublicKey();
            var pubKeyDer = pubKey.GetEncoded(); // SPKI DER: 12-byte prefix + 32-byte raw key
            _devicePublicKeyRaw = new byte[32];
            Array.Copy(pubKeyDer, 12, _devicePublicKeyRaw, 0, 32);

            // base64url: raw 32 bytes
            _devicePublicKeyBase64Url = Base64UrlEncode(_devicePublicKeyRaw);

            // deviceId = SHA256(raw 32 bytes) hex (full 64 chars)
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(_devicePublicKeyRaw);
            _deviceId = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // V3 签名载荷 (与 OpenClaw Gateway 参考客户端一致)
        // "v3|<deviceId>|<clientId>|<clientMode>|<role>|<scopes>|<signedAtMs>|<token>|<nonce>|<platform>|<deviceFamily>"
        private static string SignDevicePayload(string nonce, long signedAtMs, string platform)
        {
            var scopes = "operator.read,operator.write";
            var payload = $"v3|{_deviceId}|gateway-client|backend|operator|{scopes}|{signedAtMs}|{_token}|{nonce}|{platform}|";
            var data = Encoding.UTF8.GetBytes(payload);
            var signer = new Ed25519Signer();
            signer.Init(true, _deviceKey);
            signer.BlockUpdate(data, 0, data.Length);
            return Base64UrlEncode(signer.GenerateSignature());
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static async Task SendChallengeResponse(string nonce)
        {
            EnsureDeviceIdentity();

            var platform = Environment.OSVersion.Platform.ToString().Contains("Win") ? "windows"
                : Environment.OSVersion.Platform.ToString().Contains("Mac") ? "macos" : "linux";
            var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var connectParams = new
            {
                minProtocol = 3,
                maxProtocol = 4,
                client = new
                {
                    id = "gateway-client",
                    displayName = "RimWorldMCP",
                    version = "1.0",
                    platform,
                    mode = "backend"
                },
                role = "operator",
                scopes = new[] { "operator.read", "operator.write" },
                caps = new[] { "tool-events" },
                locale = "zh-CN",
                userAgent = "RimWorldMCP/1.0",
                auth = new
                {
                    token = !string.IsNullOrEmpty(_token) ? _token : !string.IsNullOrEmpty(_password) ? _password : null,
                    password = (string?)null,
                    deviceToken = (string?)null
                },
                device = new
                {
                    id = _deviceId,
                    publicKey = _devicePublicKeyBase64Url,
                    signature = SignDevicePayload(nonce, signedAt, platform),
                    signedAt,
                    nonce
                }
            };

            await SendJson(new { type = "req", id = Guid.NewGuid().ToString("N").Substring(0, 8), method = "connect", @params = connectParams });
        }

        private static async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                    while (!result.EndOfMessage && !ct.IsCancellationRequested)
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, result.Count, buf.Length - result.Count), ct);
                        text += Encoding.UTF8.GetString(buf, 0, result.Count);
                    }

                    Incoming.Enqueue(text);
                    McpLog.Info($"[ws] ← {Truncate(text)}");

                    // 按官方协议解析帧类型: req(res)/res/event/ping
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;
                        var type = t.GetString();

                        switch (type)
                        {
                            case "event":
                                if (root.TryGetProperty("event", out var ev))
                                {
                                    var evt = ev.GetString();
                                    if (evt == "connect.challenge"
                                        && root.TryGetProperty("payload", out var pl)
                                        && pl.TryGetProperty("nonce", out var nonce))
                                    {
                                        await SendChallengeResponse(nonce.GetString() ?? "");
                                    }
                                    else if (evt == "tick")
                                    {
                                        _lastTick = DateTime.UtcNow;
                                    }
                                }
                                break;

                            case "res":
                                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                                    && root.TryGetProperty("payload", out var payload)
                                    && payload.TryGetProperty("type", out var pt) && pt.GetString() == "hello-ok")
                                {
                                    if (payload.TryGetProperty("policy", out var policy)
                                        && policy.TryGetProperty("tickIntervalMs", out var tiv) && tiv.TryGetInt32(out var iv))
                                        _tickIntervalMs = iv;

                                    _lastTick = DateTime.UtcNow;
                                    _helloOk?.TrySetResult(true);
                                }
                                break;

                            case "ping":
                                await SendJson(new { type = "pong" });
                                break;
                        }

                        // tick 超时检查 (2x tickIntervalMs, 官方协议要求)
                        if (_state == ClientState.Ready && (DateTime.UtcNow - _lastTick).TotalMilliseconds > _tickIntervalMs * 2)
                        {
                            McpLog.Warn("[ws] tick 超时，连接可能已断开");
                            _state = ClientState.Disconnected;
                            try { _ws?.CloseAsync(WebSocketCloseStatus.ProtocolError, "tick timeout", CancellationToken.None); } catch { }
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { McpLog.Warn($"[ws] 接收异常: {ex.Message}"); }

            _state = ClientState.Disconnected;
        }
    }
}
