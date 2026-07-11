using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.AgentTransport;
using RimWorldAgent.Core;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class RimWorldAgentMod : Mod
    {
        private const float CustomBackendCardHeight = 500f;

        public static RimWorldAgentMod Instance { get; private set; } = null!;
        public AgentModSettings Settings { get; private set; }
        private Vector2 _scrollPos;
        private string? _detectedNodePath;
        private string _detectedNodeVersion = "";
        private string _nodeDetectionStatus = "尚未检测";
        private bool _nodeVersionSupported;
        private Task<AcpBackendProbeResult>? _backendTestTask;
        private string _backendTestBackendId = "";
        private string _backendTestStatus = "";

        public RimWorldAgentMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AgentModSettings>();
            var modRoot = Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            NativeResolver.Setup(modRoot);
        }

        public override string SettingsCategory() => "RimWorld Agent";

        private static void DrawSectionHeader(Listing_Standard listing, string title)
        {
            listing.Gap(4f);
            var rect = listing.GetRect(22f);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + 10f, rect.width, 1f),
                new Color(0.25f, 0.25f, 0.3f, 0.6f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.45f, 0.5f, 0.6f, 1f);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width, 18f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            listing.Gap(2f);
        }

        private static string DrawTextArea(Listing_Standard listing, string text, float height)
        {
            var rect = listing.GetRect(height);
            return Widgets.TextArea(rect, text ?? "");
        }

        private void EnsureAcpBackendCollections()
        {
            Settings.EnsureAcpBackendDefaults();
        }

        private static bool IsAcpBackendIdValid(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            foreach (var c in id.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') continue;
                return false;
            }
            return true;
        }

        private bool IsAcpBackendIdUnique(AcpBackendSetting candidate)
            => Settings.AcpBackends.Count(backend => backend != null
                && !ReferenceEquals(backend, candidate)
                && string.Equals(backend.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)) == 0;

        private string NextAcpBackendId(string prefix = "custom-agent")
        {
            for (var i = 1; i < 1000; i++)
            {
                var id = i == 1 ? prefix : $"{prefix}-{i}";
                if (!Settings.AcpBackends.Any(backend => backend != null
                    && string.Equals(backend.Id, id, StringComparison.OrdinalIgnoreCase)))
                    return id;
            }
            return "custom-agent";
        }

        private void AddBackendTemplate(string template)
        {
            var backend = CreateBackendTemplate(template);
            Settings.AcpBackends.Add(backend);
            Settings.SelectedAcpBackendId = backend.Id;
        }

        private AcpBackendSetting CreateBackendTemplate(string template)
        {
            if (template == "claude-code")
            {
                return new AcpBackendSetting
                {
                    Id = NextAcpBackendId("claude-code"),
                    DisplayName = "Claude Code",
                    Type = "custom",
                    Command = "npx",
                    ArgsText = "-y @agentclientprotocol/claude-agent-acp"
                };
            }

            if (template == "codex")
            {
                return new AcpBackendSetting
                {
                    Id = NextAcpBackendId("codex"),
                    DisplayName = "Codex",
                    Type = "custom",
                    Command = "npx",
                    ArgsText = "-y @agentclientprotocol/codex-acp"
                };
            }

            return new AcpBackendSetting
            {
                Id = NextAcpBackendId(),
                DisplayName = "Custom Agent",
                Type = "custom",
                Command = AgentRuntimePaths.NodeCommandName
            };
        }

        private void ShowBackendTemplateMenu()
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption("Claude Code 模板", () => AddBackendTemplate("claude-code")),
                new FloatMenuOption("Codex 模板", () => AddBackendTemplate("codex")),
                new FloatMenuOption("空白 Backend", () => AddBackendTemplate("custom"))
            }));
        }

        private void ShowBackendSelectionMenu()
        {
            var options = Settings.AcpBackends
                .Where(backend => backend != null && backend.Enabled
                    && IsAcpBackendIdValid(backend.Id) && IsAcpBackendIdUnique(backend))
                .Select(backend =>
                {
                    var id = backend.Id;
                    var label = string.IsNullOrWhiteSpace(backend.DisplayName) ? id : backend.DisplayName;
                    return new FloatMenuOption($"{label}  ({id})", () => Settings.SelectedAcpBackendId = id);
                })
                .ToList();
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }

        private float GetAcpBackendCardHeight(AcpBackendSetting backend)
            => CustomBackendCardHeight;

        private bool DrawAcpBackendCard(Listing_Standard listing, AcpBackendSetting backend, int index)
        {
            var cardRect = listing.GetRect(GetAcpBackendCardHeight(backend));
            Widgets.DrawBoxSolid(cardRect, backend.Enabled
                ? new Color(0.08f, 0.09f, 0.12f, 0.72f)
                : new Color(0.06f, 0.06f, 0.07f, 0.55f));
            Widgets.DrawBox(cardRect);

            var inner = new Listing_Standard();
            inner.Begin(new Rect(cardRect.x + 8f, cardRect.y + 8f, cardRect.width - 16f, cardRect.height - 16f));
            var title = $"#{index + 1}  {(string.IsNullOrWhiteSpace(backend.DisplayName) ? backend.Id : backend.DisplayName)}";
            inner.CheckboxLabeled(title, ref backend.Enabled);
            var isSelected = backend.Id == Settings.SelectedAcpBackendId;
            GUI.color = isSelected ? new Color(0.55f, 0.85f, 0.65f, 1f) : new Color(0.6f, 0.65f, 0.75f, 1f);
            inner.Label(isSelected ? $"当前使用 · {backend.Id}" : $"手动配置 · {backend.Id}");
            GUI.color = Color.white;

            inner.Label("显示名称");
            backend.DisplayName = inner.TextEntry(backend.DisplayName ?? "").Trim();
            inner.Label("Backend ID（字母、数字、点号、下划线、连字符）");
            backend.Id = inner.TextEntry(backend.Id ?? "").Trim();
            if (!IsAcpBackendIdValid(backend.Id) || !IsAcpBackendIdUnique(backend))
            {
                GUI.color = Color.yellow;
                inner.Label("Backend ID 无效或重复，当前配置不会被使用。");
                GUI.color = Color.white;
            }
            inner.Label("启动命令");
            backend.Command = inner.TextEntry(backend.Command ?? "").Trim();
            inner.Label("命令参数（空格分隔，支持引号）");
            backend.ArgsText = inner.TextEntry(backend.ArgsText ?? "").Trim();
            inner.Label("工作目录（留空使用 Agent 数据目录）");
            backend.WorkingDirectory = inner.TextEntry(backend.WorkingDirectory ?? "").Trim();
            inner.Label("环境变量（每行 KEY=VALUE；模型、API URL、API Key 等由 Backend 自行约定）");
            inner.Label("模型配置位置：填写 Backend 要求的环境变量或命令参数；ACP 不定义统一的模型字段。");
            backend.EnvText = DrawTextArea(inner, backend.EnvText ?? "", 62f);
            var testRunning = _backendTestTask != null && !_backendTestTask.IsCompleted;
            if (!testRunning && inner.ButtonText("测试 ACP 启动"))
                StartBackendTest(backend);
            if (backend.Id == _backendTestBackendId && !string.IsNullOrWhiteSpace(_backendTestStatus))
            {
                GUI.color = _backendTestStatus.StartsWith("ACP 启动成功", StringComparison.Ordinal)
                    ? new Color(0.55f, 0.85f, 0.65f, 1f)
                    : Color.yellow;
                inner.Label(_backendTestStatus);
                GUI.color = Color.white;
            }
            var deleted = inner.ButtonText("删除此 Backend");
            inner.End();
            return deleted;
        }

        private void DrawAcpBackendSection(Listing_Standard listing)
        {
            DrawSectionHeader(listing, "ACP Backend");
            var selected = Settings.AcpBackends.FirstOrDefault(backend => backend != null
                && backend.Enabled && backend.Id == Settings.SelectedAcpBackendId);
            var selectedName = selected == null
                ? "无可用 Backend"
                : string.IsNullOrWhiteSpace(selected.DisplayName) ? selected.Id : selected.DisplayName;
            if (listing.ButtonText($"当前 Backend: {selectedName}")) ShowBackendSelectionMenu();
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label("点击上方按钮选择已启用的 Backend。认证、API 地址和模型由 Backend 自身配置管理。");
            GUI.color = Color.white;

            listing.Label("Node.js 运行时（用于启动 ACP Host，留空自动检测）");
            var nodeExecutablePath = listing.TextEntry(Settings.NodeExecutablePath ?? "").Trim();
            if (!string.Equals(nodeExecutablePath, Settings.NodeExecutablePath, StringComparison.Ordinal))
            {
                Settings.NodeExecutablePath = nodeExecutablePath;
                RefreshNodeDetection();
            }
            if (listing.ButtonText("检测运行环境"))
            {
                // 手动检测必须忽略当前输入框内容，否则输入框里残留的
                // "nodejs"/无效路径会让按钮只重复验证这个无效值。
                RefreshNodeDetection(true);
                if (!string.IsNullOrWhiteSpace(_detectedNodePath))
                    Settings.NodeExecutablePath = _detectedNodePath!;
            }
            if (_detectedNodePath == null || !_nodeVersionSupported)
            {
                GUI.color = Color.yellow;
                listing.Label(_detectedNodePath == null
                    ? "Node.js: " + _nodeDetectionStatus
                    : $"Node.js: {_detectedNodePath} ({_detectedNodeVersion}) · {_nodeDetectionStatus}");
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(0.55f, 0.85f, 0.65f, 1f);
                listing.Label($"Node.js: {_detectedNodePath}{(string.IsNullOrEmpty(_detectedNodeVersion) ? "" : $" ({_detectedNodeVersion})")}");
                GUI.color = Color.white;
            }

            foreach (var pair in Settings.AcpBackends.Select((backend, index) => new { backend, index }).ToList())
            {
                var backend = pair.backend ?? new AcpBackendSetting();
                Settings.AcpBackends[pair.index] = backend;
                listing.Gap(8f);
                if (!DrawAcpBackendCard(listing, backend, pair.index)) continue;
                var deletedId = backend.Id;
                Settings.AcpBackends.RemoveAt(pair.index);
                if (Settings.SelectedAcpBackendId == deletedId) Settings.EnsureAcpBackendDefaults();
                break;
            }

            listing.Gap(6f);
            if (listing.ButtonText("添加 Backend 模板")) ShowBackendTemplateMenu();
        }

        private void StartBackendTest(AcpBackendSetting backend)
        {
            if (_backendTestTask != null && !_backendTestTask.IsCompleted) return;

            var nodePath = NodeRuntimeLocator.Resolve(Settings.NodeExecutablePath);
            if (string.IsNullOrWhiteSpace(nodePath))
            {
                _backendTestBackendId = backend.Id;
                _backendTestStatus = "ACP 测试失败：未找到 Node.js 运行时。";
                return;
            }
            if (!NodeRuntimeLocator.IsVersionSupported(nodePath!, 22, out var nodeVersion))
            {
                _backendTestBackendId = backend.Id;
                _backendTestStatus = $"ACP 测试失败：Node.js 版本不受支持 ({nodeVersion})。";
                return;
            }

            var backendDefinition = GameComponent_RimWorldAgent.BuildAcpBackendDefinition(backend, nodePath!);
            if (backendDefinition == null || string.IsNullOrWhiteSpace(backendDefinition.Command))
            {
                _backendTestBackendId = backend.Id;
                _backendTestStatus = "ACP 测试失败：Backend 启动命令为空。";
                return;
            }

            var assemblyDirectory = Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            var hostDirectory = Path.Combine(assemblyDirectory, AgentRuntimePaths.NodeHostDirectoryName);
            var hostEntryPoint = AgentRuntimePaths.GetNodeHostEntryPoint(hostDirectory);
            var projectPath = AgentRuntimePaths.GetProbeProjectDirectory(assemblyDirectory);
            Directory.CreateDirectory(projectPath);

            _backendTestBackendId = backend.Id;
            _backendTestStatus = "ACP 启动测试中...";
            _backendTestTask = Task.Run(() => AcpBackendProbe.RunAsync(
                nodePath!,
                hostEntryPoint,
                projectPath,
                backendDefinition,
                Settings.AgentMcpPort,
                TimeSpan.FromSeconds(120),
                CancellationToken.None));
        }

        private void PollBackendTest()
        {
            if (_backendTestTask == null || !_backendTestTask.IsCompleted) return;
            try
            {
                var result = _backendTestTask.GetAwaiter().GetResult();
                _backendTestStatus = result.Message;
            }
            catch (Exception ex)
            {
                _backendTestStatus = $"ACP 启动测试失败：{ex.GetType().Name}: {ex.Message}";
                CoreLog.Error($"[agent-mod] ACP 启动测试异常: {ex}");
            }
            finally
            {
                _backendTestTask = null;
            }
        }

        private void RefreshNodeDetection(bool autoDetect = false)
        {
            var configuredPath = autoDetect ? null : Settings.NodeExecutablePath;
            _detectedNodePath = NodeRuntimeLocator.Resolve(configuredPath);
            _detectedNodeVersion = "";
            _nodeVersionSupported = false;
            if (_detectedNodePath == null)
            {
                _nodeDetectionStatus = string.IsNullOrWhiteSpace(configuredPath)
                    ? "运行环境检测失败，请安装 Node.js，或填写 Node.js 可执行文件路径（Windows: node.exe；macOS/Linux: node）。"
                    : "指定的 Node.js 路径不可执行。";
                return;
            }
            _nodeVersionSupported = NodeRuntimeLocator.IsVersionSupported(_detectedNodePath, 22,
                out _detectedNodeVersion);
            _nodeDetectionStatus = _nodeVersionSupported ? "可用" : "需要 Node.js 22 或更高版本。";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            PollBackendTest();
            EnsureAcpBackendCollections();
            if (_nodeDetectionStatus == "尚未检测") RefreshNodeDetection();
            var backendHeight = 0f;
            foreach (var backend in Settings.AcpBackends)
                backendHeight += GetAcpBackendCardHeight(backend) + 8f;
            var h = 740f + backendHeight;
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, h);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (Find.CurrentMap != null)
            {
                GUI.color = Color.yellow;
                listing.Label("设置仅在主菜单生效，游戏内仅可查看。");
                GUI.color = Color.white;
                listing.Gap(8f);
            }
            GUI.enabled = Find.CurrentMap == null;

            DrawAcpBackendSection(listing);

            // ==================== MCP 服务 ====================
            DrawSectionHeader(listing, "MCP 服务");

            listing.Label("游戏 MCP 服务地址");
            Settings.GameMcpHost = listing.TextEntry(Settings.GameMcpHost);

            listing.Label("游戏 MCP 端口");
            var gamePortStr = listing.TextEntry(Settings.GameMcpPort.ToString());
            if (int.TryParse(gamePortStr, out int gamePort) && gamePort > 0 && gamePort <= 65535)
                Settings.GameMcpPort = gamePort;

            listing.Label("Agent MCP 端口 (Node Host 使用)");
            var agentPortStr = listing.TextEntry(Settings.AgentMcpPort.ToString());
            if (int.TryParse(agentPortStr, out int agentPort) && agentPort > 0 && agentPort <= 65535)
                Settings.AgentMcpPort = agentPort;

            // ==================== Token 预算 ====================
            DrawSectionHeader(listing, "Token 预算");

            listing.Label("预算上限 (K, 0=不限制)");
            var limitKStr = listing.TextEntry((Settings.TokenBudgetLimit / 1000).ToString());
            if (long.TryParse(limitKStr, out long limitK) && limitK >= 0)
                Settings.TokenBudgetLimit = limitK * 1000;

            listing.Gap(4f);
            var usage = TokenUsageTracker.GetCompactDisplay(Settings.TokenBudgetLimit);
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label($"累计: {usage}");
            GUI.color = Color.white;

            // ==================== 工具结果 Diff ====================
            DrawSectionHeader(listing, "工具结果 Diff");

            listing.CheckboxLabeled("启用工具结果增量返回", ref Settings.DiffEnabled,
                "开启后，支持 cacheKey 的工具结果会优先返回相对上次结果的 diff。");

            listing.Label("全量阈值 (0.0-1.0)");
            var diffThresholdText = listing.TextEntry(Settings.DiffThreshold.ToString("0.##"));
            if (double.TryParse(diffThresholdText, out var diffThreshold))
                Settings.DiffThreshold = Math.Max(0, Math.Min(1, diffThreshold));

            // ==================== Agent 行为 ====================
            DrawSectionHeader(listing, "Agent 行为");

            listing.CheckboxLabeled("自动运行 Agent", ref Settings.AgentAutoRun,
                "开启后加载存档时自动启动。");
            // Plan 阶段速度已移除 — Plan/Act 均强制暂停，仅 Advance 可推进

            listing.Label("Skills 目录 (留空用默认)");
            Settings.SkillsDir = listing.TextEntry(Settings.SkillsDir);
            if (listing.ButtonText($"管理 Skills / {AgentRuntimePaths.UserSkillsDirectoryName}"))
                Find.WindowStack.Add(new Dialog_SkillManager());

            // ==================== UI Bridge ====================
            DrawSectionHeader(listing, "UI 桥接 (WebSocket)");

            listing.Label("监听地址");
            Settings.BridgeHost = listing.TextEntry(Settings.BridgeHost);

            listing.Label("监听端口");
            var bpStr = listing.TextEntry(Settings.BridgePort.ToString());
            if (int.TryParse(bpStr, out int bp) && bp > 0 && bp <= 65535)
                Settings.BridgePort = bp;

            GUI.enabled = true;
            listing.End();
            Widgets.EndScrollView();
        }
    }
}
