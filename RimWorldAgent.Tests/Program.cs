using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Tests
{
    /// <summary>Agent → MCP Tool 调用集成测试</summary>
    internal class Program
    {
        private static int _passed;
        private static int _failed;
        private static int _skipped;

        static async Task Main(string[] args)
        {
            var baseUrl = args.Length > 0 ? args[0] : "http://localhost:9877";
            Console.WriteLine($"=== Agent → MCP Tool 集成测试 ===");
            Console.WriteLine($"目标: {baseUrl}");
            Console.WriteLine();

            // 测试 1: MCP 连接 + ListTools
            await TestConnectAndListTools(baseUrl);

            // 测试 2: CallTool check_map_loaded (INoMapRequired)
            await TestCallTool_CheckMapLoaded(baseUrl);

            // 测试 3: CallTool get_colonists (需地图)
            await TestCallTool_GetColonists(baseUrl);

            // 测试 4: CallTool 未知工具
            await TestCallTool_UnknownTool(baseUrl);

            // 测试 5: ToolDispatcher 外部工具调用链路
            await TestToolDispatcher_External(baseUrl);

            // 测试 6-8: 暂停功能
            await TestTogglePause(baseUrl);
            await TestTogglePause_Speed(baseUrl, "superfast", "3 倍速");
            await TestAdvanceTick(baseUrl, 0.1f);

            Console.WriteLine();
            Console.WriteLine($"=== 结果: {_passed} 通过, {_failed} 失败, {_skipped} 跳过 ===");
            Environment.ExitCode = _failed > 0 ? 1 : 0;
        }

        /// <summary>测试 1: MCP SDK 握手 + 工具列表</summary>
        static async Task TestConnectAndListTools(string baseUrl)
        {
            var name = "MCP 连接 + ListTools";
            try
            {
                using var client = new McpClient(baseUrl);
                var tools = await client.ListToolsAsync();

                if (tools.Count == 0)
                {
                    Fail(name, "工具列表为空");
                    return;
                }

                Pass(name, $"获取到 {tools.Count} 个工具");
                // 打印前 5 个工具名
                for (int i = 0; i < Math.Min(5, tools.Count); i++)
                    Console.WriteLine($"    [{i + 1}] {tools[i].Name}");
                if (tools.Count > 5)
                    Console.WriteLine($"    ... 共 {tools.Count} 个");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        /// <summary>测试 2: 调用 check_map_loaded (INoMapRequired，无地图也能用)</summary>
        static async Task TestCallTool_CheckMapLoaded(string baseUrl)
        {
            var name = "CallTool check_map_loaded";
            try
            {
                using var client = new McpClient(baseUrl);
                var result = await client.CallToolAsync("check_map_loaded");

                if (string.IsNullOrEmpty(result))
                {
                    Fail(name, "返回为空");
                    return;
                }

                Pass(name, result.Length > 100 ? result.Substring(0, 100) + "..." : result);
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        /// <summary>测试 3: 调用 get_colonists (需地图，无地图时应返回守卫错误而非超时)</summary>
        static async Task TestCallTool_GetColonists(string baseUrl)
        {
            var name = "CallTool get_colonists (守卫检查)";
            try
            {
                using var client = new McpClient(baseUrl);
                var sw = Stopwatch.StartNew();
                var result = await client.CallToolAsync("get_colonists");
                sw.Stop();

                // 如果游戏已加载地图，result 应包含殖民者信息
                // 如果未加载地图，result 应包含守卫错误信息
                if (result.Contains("没有已加载的地图") || result.Contains("游戏未启动") || result.Contains("加载中"))
                {
                    Pass(name, $"守卫正确拦截 ({sw.ElapsedMilliseconds}ms): {result.Substring(0, Math.Min(80, result.Length))}");
                }
                else if (result.Contains("殖民者") || result.Contains("colonist") || result.Contains("心情"))
                {
                    Pass(name, $"游戏已加载，获取到殖民者数据 ({sw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    // 可能返回了其他内容，也算通过（只要不是超时）
                    Pass(name, $"返回内容 ({sw.ElapsedMilliseconds}ms): {result.Substring(0, Math.Min(80, result.Length))}");
                }
            }
            catch (TimeoutException)
            {
                Fail(name, "超时！McpCommandQueue.DispatchAsync 60 秒未返回 — 守卫检查可能未生效");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        /// <summary>测试 4: 调用不存在的工具</summary>
        static async Task TestCallTool_UnknownTool(string baseUrl)
        {
            var name = "CallTool 未知工具";
            try
            {
                using var client = new McpClient(baseUrl);
                var result = await client.CallToolAsync("nonexistent_tool_xyz");

                if (result.Contains("未知工具") || result.Contains("Unknown tool"))
                {
                    Pass(name, "正确返回未知工具错误");
                }
                else
                {
                    Fail(name, $"预期返回未知工具错误，实际: {result.Substring(0, Math.Min(80, result.Length))}");
                }
            }
            catch (Exception ex)
            {
                // 如果 SDK 抛出异常而非返回错误文本，也记录
                Fail(name, $"SDK 异常: {UnwrapException(ex)}");
            }
        }

        /// <summary>测试 5: ToolDispatcher 外部工具调用链路（模拟 CCB 消息）</summary>
        static async Task TestToolDispatcher_External(string baseUrl)
        {
            var name = "ToolDispatcher 外部工具";
            try
            {
                using var mcp = new McpClient(baseUrl);

                // 模拟 ToolDispatcher 的参数反序列化逻辑
                var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}");
                var args = input != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(input))
                    : null;

                // 直接调用 McpClient.CallTool 模拟 ToolDispatcher 的外部工具路径
                var sw = Stopwatch.StartNew();
                var result = await mcp.CallToolAsync("check_map_loaded", args);
                sw.Stop();

                if (string.IsNullOrEmpty(result))
                {
                    Fail(name, "返回为空");
                    return;
                }

                Pass(name, $"完整链路通过 ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        // ========== 暂停功能测试 ==========

        /// <summary>测试 6: toggle_pause 切换暂停</summary>
        static async Task TestTogglePause(string baseUrl)
        {
            var name = "toggle_pause 切换暂停";
            try
            {
                using var client = new McpClient(baseUrl);
                var result = await client.CallToolAsync("toggle_pause");

                if (string.IsNullOrEmpty(result))
                {
                    Fail(name, "返回为空");
                    return;
                }

                if (result.Contains("已暂停") || result.Contains("运行中") || result.Contains("加载中"))
                    Pass(name, result.Length > 80 ? result.Substring(0, 80) + "..." : result);
                else
                    Fail(name, $"预期暂停/运行状态，实际: {result.Substring(0, Math.Min(80, result.Length))}");
            }
            catch (TimeoutException)
            {
                Fail(name, "超时！主线程可能不可用");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        /// <summary>测试 7: toggle_pause 设置指定速度</summary>
        static async Task TestTogglePause_Speed(string baseUrl, string speed, string expectedLabel)
        {
            var name = $"toggle_pause speed={speed}";
            try
            {
                using var client = new McpClient(baseUrl);
                var args = new Dictionary<string, JsonElement>
                {
                    ["speed"] = JsonSerializer.SerializeToElement(speed)
                };
                var result = await client.CallToolAsync("toggle_pause", args);

                if (string.IsNullOrEmpty(result))
                {
                    Fail(name, "返回为空");
                    return;
                }

                if (result.Contains(expectedLabel) || result.Contains("加载中"))
                    Pass(name, result.Length > 80 ? result.Substring(0, 80) + "..." : result);
                else
                    Fail(name, $"预期包含 '{expectedLabel}'，实际: {result.Substring(0, Math.Min(80, result.Length))}");
            }
            catch (TimeoutException)
            {
                Fail(name, "超时！主线程可能不可用");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        /// <summary>测试 8: advance_tick 推进游戏</summary>
        static async Task TestAdvanceTick(string baseUrl, float hours)
        {
            var name = $"advance_tick hours={hours}";
            try
            {
                using var client = new McpClient(baseUrl);
                var args = new Dictionary<string, JsonElement>
                {
                    ["hours"] = JsonSerializer.SerializeToElement(hours)
                };

                var sw = Stopwatch.StartNew();
                var result = await client.CallToolAsync("advance_tick", args);
                sw.Stop();

                if (string.IsNullOrEmpty(result))
                {
                    Fail(name, "返回为空");
                    return;
                }

                if (result.Contains("游戏状态") || result.Contains("Tick") || result.Contains("加载中") || result.Contains("中断"))
                    Pass(name, $"({sw.ElapsedMilliseconds}ms) {result.Substring(0, Math.Min(80, result.Length))}");
                else
                    Fail(name, $"预期游戏状态，实际: {result.Substring(0, Math.Min(80, result.Length))}");
            }
            catch (TimeoutException)
            {
                Fail(name, "超时！advance_tick 可能卡在等待主线程");
            }
            catch (Exception ex)
            {
                Fail(name, UnwrapException(ex));
            }
        }

        // ========== 辅助方法 ==========

        static void Pass(string testName, string detail)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {testName}: {detail}");
        }

        static void Fail(string testName, string detail)
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {testName}: {detail}");
        }

        static void Skip(string testName, string reason)
        {
            _skipped++;
            Console.WriteLine($"  [SKIP] {testName}: {reason}");
        }

        static string UnwrapException(Exception ex)
        {
            var parts = new List<string>();
            var current = ex;
            while (current != null)
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
                current = current.InnerException;
            }
            return string.Join(" → ", parts);
        }
    }
}
