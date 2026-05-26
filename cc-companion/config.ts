/**
 * 配置解析 — CLI 参数 + 环境变量
 */

export interface CompanionConfig {
  host: string;
  port: number;
  token: string;
  mcpConfig: string;
  projectPath: string;
  permissionMode: string;
  maxTurns: number;
  idleTimeout: number;
  apiKey: string;
  apiBaseUrl: string;
  modelName: string;
  settingSources: string[];
}

export const CONFIG: CompanionConfig = {
  host: process.env.CC_HOST || '127.0.0.1',
  port: parseInt(process.env.CC_PORT || '19999'),
  token: process.env.CC_TOKEN || '',
  mcpConfig: process.env.CC_MCP_CONFIG || '{"rimworld":{"type":"http","url":"http://localhost:9877/mcp"}}',
  projectPath: process.env.RIMWORLD_PROJECT_PATH || process.cwd(),
  permissionMode: process.env.CC_PERMISSION_MODE || 'bypassPermissions',
  maxTurns: parseInt(process.env.CC_MAX_TURNS || '500'),
  idleTimeout: process.env.CC_IDLE_TIMEOUT !== undefined
    ? parseInt(process.env.CC_IDLE_TIMEOUT) : 300000,
  apiKey: process.env.CC_API_KEY || '',
  apiBaseUrl: process.env.CC_API_BASE_URL || 'http://localhost:3000',
  modelName: process.env.CC_MODEL_NAME || 'deepseek-v4-flash',
  settingSources: process.env.CC_SETTING_SOURCES
    ? process.env.CC_SETTING_SOURCES.split(',').map(s => s.trim())
    : ['user', 'project', 'local'],
};

export function parseArgs(argv: string[]): void {
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--host' && argv[i + 1]) CONFIG.host = argv[++i];
    else if (arg === '--port' && argv[i + 1]) CONFIG.port = parseInt(argv[++i]);
    else if (arg === '--token' && argv[i + 1]) CONFIG.token = argv[++i];
    else if (arg === '--permission-mode' && argv[i + 1]) CONFIG.permissionMode = argv[++i];
    else if (arg === '--max-turns' && argv[i + 1]) CONFIG.maxTurns = parseInt(argv[++i]);
    else if (arg === '--idle-timeout' && argv[i + 1]) CONFIG.idleTimeout = parseInt(argv[++i]);
    else if (arg === '--no-idle-timeout') CONFIG.idleTimeout = 0;
    else if (arg === '--project-path' && argv[i + 1]) CONFIG.projectPath = argv[++i];
    else if (arg === '--api-key' && argv[i + 1]) CONFIG.apiKey = argv[++i];
    else if (arg === '--api-base-url' && argv[i + 1]) CONFIG.apiBaseUrl = argv[++i];
    else if (arg === '--model-name' && argv[i + 1]) CONFIG.modelName = argv[++i];
    else if (arg === '--mcp-config' && argv[i + 1]) CONFIG.mcpConfig = argv[++i];
    else if (arg === '--setting-sources' && argv[i + 1]) CONFIG.settingSources = argv[++i].split(',').map(s => s.trim());
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
  --permission-mode <mode> 权限模式 (默认 bypassPermissions, 环境变量 CC_PERMISSION_MODE)
  --max-turns <n>          最大对话轮次 (默认 500, 环境变量 CC_MAX_TURNS)
  --idle-timeout <ms>      空闲超时自动退出 (默认 300000, 环境变量 CC_IDLE_TIMEOUT)
  --no-idle-timeout        禁用空闲超时，永远等待
  --project-path <dir>     会话存储目录，SDK 据此隔离 checkpoint (默认 cwd)
  --api-key <key>          API Key (环境变量 CC_API_KEY)
  --api-base-url <url>     API 代理地址 (默认 http://localhost:3000, 环境变量 CC_API_BASE_URL)
  --model-name <name>      模型名称 (默认 deepseek-v4-pro[1m], 环境变量 CC_MODEL_NAME)
  --mcp-config <json>      MCP 服务器完整配置 JSON，含 url/type/headers 等 (环境变量 CC_MCP_CONFIG)
                           例: '{"rimworld":{"type":"http","url":"http://localhost:9877/mcp"}}'
  --setting-sources <list> 设置加载源，逗号分隔 (默认 user,project,local, 环境变量 CC_SETTING_SOURCES)
  --help                   显示帮助

环境变量:
  CC_HOST                  监听地址覆盖 (默认 127.0.0.1)
  CC_PORT                  WebSocket 端口覆盖 (默认 19999)
  CC_TOKEN                 认证 token
  CC_PERMISSION_MODE       权限模式 (默认 bypassPermissions)
  CC_MAX_TURNS             最大对话轮次 (默认 500)
  CC_IDLE_TIMEOUT          空闲超时毫秒数 (默认 300000)
  CC_MODEL_NAME            模型名称 (默认 deepseek-v4-pro[1m])
  CC_MCP_CONFIG            MCP 服务 JSON 配置
  CC_SETTING_SOURCES       设置加载源，逗号分隔 (默认 user,project,local)
  CC_API_KEY               API Key
  CC_API_BASE_URL          API 代理地址 (默认 http://localhost:3000)
  RIMWORLD_PROJECT_PATH    会话存储目录 (默认 cwd)
  ANTHROPIC_API_KEY        API Key 认证（备用）
  ANTHROPIC_AUTH_TOKEN     Bearer Token 认证（备用）
`);
}
