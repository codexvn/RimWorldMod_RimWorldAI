/**
 * 配置解析 — CLI 参数 + 环境变量
 */

export interface CompanionConfig {
  host: string;
  port: number;
  token: string;
  projectPath: string;
  idleTimeout: number;
  modelName: string;
  settingSources: string[];
  projectSettingSources: string;
  localSettingSources: string;
  chatPageEnabled: boolean;
}

/** 运行时状态 — 由各模块写入，非启动配置 */
export const RuntimeState = {
  tokenBudgetLimit: 0,
  tokenBudgetUsed: 0,
  tokenBudgetAction: 'Block' as string,
  resolvedModel: '' as string,
  sessionFilePath: '' as string,
  lastInitData: null as any,
  lastColonyStats: null as any,
  lastTodoItems: null as any,
  sdkTasks: [] as Array<{id: string, subject: string, status: string}>,
  thinkingMode: 'default' as string,
  thinkingEffort: 'medium' as string,
  maxThinkingTokens: 0 as number,
};

export const CONFIG: CompanionConfig = {
  host: process.env.CCB_HOST || '0.0.0.0',
  port: parseInt(process.env.CCB_PORT || '19999'),
  token: process.env.CCB_AUTH_TOKEN || '',
  projectPath: process.env.RIMWORLD_PROJECT_PATH || process.cwd(),
  idleTimeout: process.env.CCB_IDLE_TIMEOUT !== undefined
    ? parseInt(process.env.CCB_IDLE_TIMEOUT) : 0,
  modelName: process.env.CCB_MODEL_NAME || '',
  settingSources: process.env.CCB_SETTING_SOURCES
    ? process.env.CCB_SETTING_SOURCES.split(',').map(s => s.trim())
    : ['user', 'project', 'local'],
  projectSettingSources: '',
  localSettingSources: '',
  chatPageEnabled: process.env.CCB_NO_CHAT_PAGE ? false : true,
};

export function parseArgs(argv: string[]): void {
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--host' && argv[i + 1]) CONFIG.host = argv[++i];
    else if (arg === '--port' && argv[i + 1]) CONFIG.port = parseInt(argv[++i]);
    else if (arg === '--token' && argv[i + 1]) CONFIG.token = argv[++i];
    else if (arg === '--model-name' && argv[i + 1]) CONFIG.modelName = argv[++i];
    else if (arg === '--idle-timeout' && argv[i + 1]) CONFIG.idleTimeout = parseInt(argv[++i]);
    else if (arg === '--no-chat-page') CONFIG.chatPageEnabled = false;
    else if (arg === '--project-path' && argv[i + 1]) CONFIG.projectPath = argv[++i];
    else if (arg === '--setting-sources' && argv[i + 1]) CONFIG.settingSources = argv[++i].split(',').map(s => s.trim());
    else if (arg === '--project-setting-sources' && argv[i + 1]) CONFIG.projectSettingSources = argv[++i];
    else if (arg === '--local-setting-sources' && argv[i + 1]) CONFIG.localSettingSources = argv[++i];
    else if (arg === '--help') { printHelp(); process.exit(0); }
  }
  console.info(JSON.stringify(CONFIG, null, 2));
}

export function printHelp(): void {
  console.log(`
Claude Code — RimWorldMCP 游戏 AI 助手

用法: tsx companion/companion.ts [选项]

选项:
  --host <host>            监听地址 (默认 127.0.0.1, 环境变量 CCB_HOST)
  --port <port>            WebSocket 端口 (默认 19999, 环境变量 CCB_PORT)
  --token <token>          认证 token (可选, 环境变量 CCB_AUTH_TOKEN)
  --model-name <name>      模型名称 (环境变量 CCB_MODEL_NAME)
  --idle-timeout <ms>      空闲超时自动退出 (不传则永不退出, 环境变量 CCB_IDLE_TIMEOUT)
  --project-path <dir>     会话存储目录，SDK 据此隔离 checkpoint (默认 cwd)
  --setting-sources <list>       设置加载源，逗号分隔 (默认 user,project,local)
  --project-setting-sources <json> 写入 {projectPath}/.claude/settings.json 的 JSON（直接覆盖）
  --local-setting-sources <json>  写入 {projectPath}/.claude/settings.local.json 的 JSON（直接覆盖）
  --no-chat-page                 禁用聊天网页（默认启用）
  --help                         显示帮助

环境变量:
  CCB_HOST                  监听地址覆盖 (默认 127.0.0.1)
  CCB_PORT                  WebSocket 端口覆盖 (默认 19999)
  CCB_AUTH_TOKEN            WS 握手认证 token
  CCB_MODEL_NAME            模型名称
  CCB_IDLE_TIMEOUT          空闲超时毫秒数 (不传则永不退出)
  CCB_SETTING_SOURCES       settings 加载源，逗号分隔 (默认 user,project,local)
  CCB_NO_CHAT_PAGE          设为任意值以禁用聊天网页
  RIMWORLD_PROJECT_PATH    会话存储目录 (默认 cwd)

其他配置（API Key、Base URL、权限模式、MCP 服务等）通过 .claude/settings.json 管理，SDK 自动读取。
`);
}
