/**
 * 配置解析 — CLI 参数 + 环境变量
 */

const DEF_MCP_PORT = parseInt(process.env.MCP_PORT || '9877');

export interface CompanionConfig {
  host: string;
  port: number;
  token: string;
  model: string;
  mcpUrl: string;
  mcpHeaders: string;
  projectPath: string;
  permissionMode: string;
  maxTurns: number;
  connectTimeout: number;
}

export const CONFIG: CompanionConfig = {
  host: process.env.CC_HOST || '127.0.0.1',
  port: parseInt(process.env.CC_PORT || '19999'),
  token: process.env.CC_TOKEN || '',
  model: process.env.CC_MODEL || 'sonnet',
  mcpUrl: process.env.MCP_URL || `http://localhost:${DEF_MCP_PORT}/mcp`,
  mcpHeaders: process.env.MCP_HEADERS || '',
  projectPath: process.env.RIMWORLD_PROJECT_PATH || process.cwd(),
  permissionMode: process.env.CC_PERMISSION_MODE || 'bypassPermissions',
  maxTurns: parseInt(process.env.CC_MAX_TURNS || '500'),
  connectTimeout: process.env.CC_CONNECT_TIMEOUT !== undefined
    ? parseInt(process.env.CC_CONNECT_TIMEOUT) : 300000,
};

export function parseArgs(argv: string[]): void {
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--host' && argv[i + 1]) CONFIG.host = argv[++i];
    else if (arg === '--port' && argv[i + 1]) CONFIG.port = parseInt(argv[++i]);
    else if (arg === '--token' && argv[i + 1]) CONFIG.token = argv[++i];
    else if (arg === '--model' && argv[i + 1]) CONFIG.model = argv[++i];
    else if (arg === '--mcp-url' && argv[i + 1]) CONFIG.mcpUrl = argv[++i];
    else if (arg === '--mcp-headers' && argv[i + 1]) CONFIG.mcpHeaders = argv[++i];
    else if (arg === '--connect-timeout' && argv[i + 1]) CONFIG.connectTimeout = parseInt(argv[++i]);
    else if (arg === '--no-connect-timeout') CONFIG.connectTimeout = 0;
    else if (arg === '--project-path' && argv[i + 1]) CONFIG.projectPath = argv[++i];
    else if (arg === '--help') { printHelp(); process.exit(0); }
  }
}

export function printHelp(): void {
  console.log(`
Claude Code — RimWorldMCP 游戏 AI 助手

用法: tsx companion.ts [选项]

选项:
  --host <host>            监听地址 (默认 127.0.0.1, 环境变量 CC_HOST)
  --port <port>            WebSocket 端口 (默认 19999, 环境变量 CC_PORT)
  --token <token>          认证 token (可选, 环境变量 CC_TOKEN)
  --model <model>          模型名称 (默认 sonnet, 环境变量 CC_MODEL)
  --mcp-url <url>          MCP Server 地址 (默认 http://localhost:9877/mcp, 环境变量 MCP_URL)
  --mcp-headers <json>     MCP 请求附加头 JSON (环境变量 MCP_HEADERS)
                           例: '{"Authorization":"Bearer xxx"}'
  --connect-timeout <ms>   无客户端连接超时自动退出 (默认 300000, 环境变量 CC_CONNECT_TIMEOUT)
  --no-connect-timeout     禁用连接超时，永远等待
  --project-path <dir>     会话存储目录 (默认 cwd, 环境变量 RIMWORLD_PROJECT_PATH)
  --help                   显示帮助

环境变量:
  ANTHROPIC_API_KEY        API Key 认证
  ANTHROPIC_AUTH_TOKEN     Bearer Token 认证
`);
}
