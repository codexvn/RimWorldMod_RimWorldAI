/** 配置 — CLI 参数 + 环境变量 */
import {EffortLevel} from "@anthropic-ai/claude-agent-sdk";

export interface CompanionConfig {
  host: string;
  port: number;
  token: string;
  projectPath: string;
  mcpServersPath: string;
  modelName: string;
  settingSources: string[];
  logSdk: boolean;
  resumeSessionId: string;
}

export const Thinking = {
  mode: 'adaptive' as string,
  effort: 'high' as EffortLevel,
};

export const CONFIG: CompanionConfig = {
  host: process.env.CCB_HOST || '0.0.0.0',
  port: parseInt(process.env.CCB_PORT || '19998'),
  token: process.env.CCB_AUTH_TOKEN || '',
  projectPath: process.env.RIMWORLD_PROJECT_PATH || process.cwd(),
  mcpServersPath: '',
  modelName: process.env.CCB_MODEL_NAME || '',
  settingSources: process.env.CCB_SETTING_SOURCES
    ? process.env.CCB_SETTING_SOURCES.split(',').map(s => s.trim())
    : ['project', 'local'],
  logSdk: process.env.CCB_LOG_SDK === '1' || process.env.CCB_LOG_SDK === 'true',
  resumeSessionId: '',
};

export function parseArgs(argv: string[]): void {
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--port' && argv[i + 1]) CONFIG.port = parseInt(argv[++i]);
    else if (a === '--host' && argv[i + 1]) CONFIG.host = argv[++i];
    else if (a === '--token' && argv[i + 1]) CONFIG.token = argv[++i];
    else if (a === '--model-name' && argv[i + 1]) CONFIG.modelName = argv[++i];
    else if (a === '--project-path' && argv[i + 1]) CONFIG.projectPath = argv[++i];
    else if (a === '--mcp-servers-path' && argv[i + 1]) CONFIG.mcpServersPath = argv[++i];
    else if (a === '--setting-sources' && argv[i + 1]) CONFIG.settingSources = argv[++i].split(',').map(s => s.trim());
    else if (a === '--log-sdk') CONFIG.logSdk = true;
    else if (a === '--resume-session-id' && argv[i + 1]) CONFIG.resumeSessionId = argv[++i];
  }
}
