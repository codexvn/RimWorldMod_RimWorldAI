import {Options, query, SDKMessage} from '@anthropic-ai/claude-agent-sdk';
import { writeFileSync } from 'fs';

interface TestCase {
  label: string;
  options: Options;
}

const tests: TestCase[] = [
  {
    label: 'disabled',
    options: { thinking: { type: 'disabled' } }
  },
  {
    label: 'adaptive+low',
    options: { thinking: { type: 'adaptive' }, effort: 'low' }
  },
  {
    label: 'adaptive+high',
    options: { thinking: { type: 'adaptive' }, effort: 'high' }
  },
  {
    label: 'adaptive+xhigh',
    options: { thinking: { type: 'adaptive' }, effort: 'xhigh' }
  },
  {
    label: 'adaptive+max',
    options: { thinking: { type: 'adaptive' }, effort: 'max' }
  },
];

async function runTest(tc: TestCase) {
  const label = tc.label;
  console.log(`\n=== ${label} ===`);

  const options = {
    ...tc.options,
    model:"GLM-5.1",
    permissionMode: 'bypassPermissions' as const,
    allowDangerouslySkipPermissions: true,
    maxTurns: 1,
    includePartialMessages: true,
    settingSources: ['user', 'project', 'local'] as string[],
  }as Options;

  const start = Date.now();

  try {
    const q = query({
      prompt: '如何能够与人相处的愉快 给我5个成语',
      options,
    });

    let text = '';
    let thinkingText = '';
    let usage: any = null;

    for await (const msg  of q) {
      // 打印每条消息的 type + 关键字段摘要
      const summary: any = { type: msg.type };
      if ((msg as any).subtype) summary.subtype = (msg as any).subtype;
      if (msg.type === 'stream_event') {
        const evt = msg.event;
        summary.event_type = evt?.type;
        if (evt?.type === 'content_block_start') summary.block_type = evt.content_block?.type;
        if (evt?.type === 'content_block_delta') {
          summary.delta_type = evt.delta?.type;
          if (evt.delta?.type === 'text_delta') {
            text += evt.delta.text || '';
            summary.preview = (evt.delta.text || '').slice(0, 40);
          }
          if (evt.delta?.type === 'thinking_delta') {
            thinkingText += evt.delta.thinking || '';
            summary.preview = (evt.delta.thinking || '').slice(0, 40);
          }
        }
      }
      if ((msg as any).message) {
        const m = (msg as any).message;
        summary.model = m.model;
        summary.stop_reason = m.stop_reason;
        summary.content_types = (m.content || []).map((c: any) => c.type);
        if (m.usage) usage = m.usage;
      }
      if ((msg as any).usage) {
        usage = (msg as any).usage;
        summary.usage = { in: usage.input_tokens, out: usage.output_tokens };
      }
      if ((msg as any).result) summary.result_preview = (msg as any).result?.slice(0, 60);
      console.log(`  [MSG] ${JSON.stringify(msg)}`);
    }

    const elapsed = ((Date.now() - start) / 1000).toFixed(1);
    console.log(`  耗时: ${elapsed}s`);
    console.log(`  思考: ${thinkingText.length > 0 ? `✅ (${thinkingText.length}字符): ${thinkingText.slice(0, 120)}...` : '❌ 无'}`);
    console.log(`  回复: ${text.trim()}`);
    if (usage) {
      console.log(`  Token: 入=${usage.input_tokens ?? '?'} 出=${usage.output_tokens ?? '?'}  缓存读=${usage.cache_read_input_tokens ?? 0} 缓存写=${usage.cache_creation_input_tokens ?? 0}`);
      // Cache read > 0 说明用了 thinking
      if ((usage.cacheReadInputTokens ?? 0) > 0) {
        console.log('  ⚠ 有缓存命中（非首轮）');
      }
    }
    return { label, elapsed, thinkingLen: thinkingText.length, text: text.trim(), usage };
  } catch (err: any) {
    console.log(`  失败: ${err.message}`);
    return { label, error: err.message };
  }
}

async function main() {
  console.log('=== Claude Agent SDK Thinking 参数测试 ===');
  console.log(`模型: Opus 4.7 (由 SDK 默认选择)`);
  console.log(`提示: 每个请求 maxTurns=1, 不做工具调用\n`);

  const results: any[] = [];
  for (const tc of tests) {
    const r = await runTest(tc);
    results.push(r);
    // 间隔 200ms 避免 rate limit
    await new Promise(resolve => setTimeout(resolve, 200));
  }

  console.log('\n=== 汇总 ===');
  console.log('配置             | 耗时   | 思考   | Token入/出');
  console.log('-----------------|--------|--------|------------');
  for (const r of results) {
    const label = r.label.padEnd(16);
    const elapsed = (r.elapsed || '?').toString().padEnd(6);
    const thinking = r.thinkingLen > 0 ? `✅ ${r.thinkingLen}字符` : '❌ 无';
    const tokens = r.usage
      ? `${r.usage.input_tokens ?? '?'}/${r.usage.output_tokens ?? '?'}`.padEnd(10)
      : '?'.padEnd(10);
    console.log(`  ${label} | ${elapsed} | ${thinking.padEnd(10)} | ${tokens}`);
  }

  writeFileSync('thinking-test-result.json', JSON.stringify(results, null, 2));
  console.log('\n结果已写入 thinking-test-result.json');
}

main();
