using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

string wsUrl = args.Length > 0 ? args[0] : "ws://127.0.0.1:18789";
string token = args.Length > 1 ? args[1] : "";

var tester = new GatewayTester(token);
await tester.Run(wsUrl);

class GatewayTester
{
    private readonly string _token;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool> _helloOk = new();
    private List<string> _received = new();

    private Ed25519PrivateKeyParameters? _key;
    private string _deviceId = "";
    private string _pubKeyB64Url = "";

    public GatewayTester(string token) { _token = token; }

    public async Task Run(string wsUrl)
    {
        Console.WriteLine($"→ 连接 {wsUrl}");
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        Console.WriteLine("WebSocket 已连接");

        _ = Task.Run(ReceiveLoop);

        await Task.Delay(500);

        if (!_helloOk.Task.IsCompleted)
        {
            Console.WriteLine("→ 发送 connect (无 challenge)");
            await Send(new { type = "connect", role = "client", client = "csharp" });
            if (!string.IsNullOrEmpty(_token))
                await Send(new { type = "auth", token = _token });
            await Task.Delay(1000);
        }

        var winner = await Task.WhenAny(_helloOk.Task, Task.Delay(15000));
        if (_helloOk.Task.IsCompleted && _helloOk.Task.Result)
            Console.WriteLine("✅ 握手成功!");
        else
            Console.WriteLine("❌ 握手超时");

        Console.WriteLine("\n--- 收到的消息 ---");
        foreach (var msg in _received)
            Console.WriteLine(msg.Length > 300 ? msg[..297] + "..." : msg);

        if (_helloOk.Task.IsCompleted && _helloOk.Task.Result)
        {
            await Send(new { type = "req", id = "test1", method = "agent.send", @params = new { text = "RimWorldMCP 测试消息" } });
            await Task.Delay(2000);
            Console.WriteLine("\n--- 最后 5 条 ---");
            foreach (var msg in _received.Skip(Math.Max(0, _received.Count - 5)))
                Console.WriteLine(msg.Length > 300 ? msg[..297] + "..." : msg);
        }

        _cts.Cancel();
        _ws.Dispose();
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[8192];
        try
        {
            while (_ws!.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                _received.Add(text);
                Console.WriteLine($"← {Trunc(text)}");

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;

                if (t.GetString() == "event" && root.TryGetProperty("event", out var ev)
                    && ev.GetString() == "connect.challenge"
                    && root.TryGetProperty("payload", out var pl)
                    && pl.TryGetProperty("nonce", out var nonce))
                {
                    var n = nonce.GetString() ?? "";
                    Console.WriteLine($"收到 challenge: {n}");
                    await SendChallengeResponse(n, _token);
                }
                else if (t.GetString() == "res" && root.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                    && root.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("type", out var pt)
                    && pt.GetString() == "hello-ok")
                {
                    Console.WriteLine("收到 hello-ok");
                    _helloOk.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"接收错误: {ex.Message}"); }
    }

    private async Task Send(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Console.WriteLine($"→ {Trunc(json)}");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static string Trunc(string s) => s.Length <= 200 ? s : s[..197] + "...";

    // ====== ED25519 设备签名 (V3) ======

    private void EnsureDeviceId()
    {
        if (_key != null) return;
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        _key = new Ed25519PrivateKeyParameters(seed, 0);
        var pubKey = _key.GeneratePublicKey();
        var raw = pubKey.GetEncoded(); // BouncyCastle 直接返回 32 字节 raw key
        _pubKeyB64Url = B64Url(raw);
        _deviceId = BitConverter.ToString(SHA256.HashData(raw)).Replace("-", "").ToLowerInvariant();
    }

    private static string B64Url(byte[] d) => Convert.ToBase64String(d).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private async Task SendChallengeResponse(string nonce, string token)
    {
        EnsureDeviceId();
        var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scopes = "operator.read,operator.write";
        var payload = $"v3|{_deviceId}|gateway-client|backend|operator|{scopes}|{signedAt}|{token}|{nonce}|windows|";
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(Encoding.UTF8.GetBytes(payload));
        var sig = B64Url(signer.GenerateSignature());

        await Send(new
        {
            type = "req",
            id = "conn1",
            method = "connect",
            @params = new
            {
                minProtocol = 3,
                maxProtocol = 4,
                client = new { id = "gateway-client", displayName = "RimWorldMCP", version = "1.0", platform = "windows", mode = "backend" },
                role = "operator",
                scopes = new[] { "operator.read", "operator.write" },
                caps = new[] { "tool-events" },
                locale = "zh-CN",
                userAgent = "RimWorldMCP/1.0",
                auth = new { token = string.IsNullOrEmpty(_token) ? null : _token, password = (string?)null, deviceToken = (string?)null },
                device = new { id = _deviceId, publicKey = _pubKeyB64Url, signature = sig, signedAt, nonce }
            }
        });
    }
}
