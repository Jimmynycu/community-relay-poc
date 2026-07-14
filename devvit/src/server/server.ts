import { once } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';
import {
  context,
  reddit,
  redis,
  settings,
} from '@devvit/web/server';
import type { TaskResponse } from '@devvit/web/server';
import type {
  SettingsValidationRequest,
  SettingsValidationResponse,
  UiResponse,
} from '@devvit/web/shared';
import {
  ATTENTION_INDEX_KEY,
  LAST_RESULT_KEY,
  isIntegerInRange,
  loadRelaySettings,
  normalizeSubredditName,
  runRelayOnce,
  type RelayRunResult,
} from './relay.ts';

const LAST_RESULT_TTL_SECONDS = 7 * 24 * 60 * 60;

export async function onRequest(
  request: IncomingMessage,
  response: ServerResponse,
): Promise<void> {
  try {
    await route(request, response);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error('request-error', { path: request.url, error: message });
    writeJson(response, 500, { error: message });
  }
}

async function route(
  request: IncomingMessage,
  response: ServerResponse,
): Promise<void> {
  const path = new URL(request.url ?? '/', 'http://devvit.local').pathname;
  if (request.method !== 'POST') {
    writeJson(response, 404, { error: 'not found' });
    return;
  }

  switch (path) {
    case '/internal/scheduler/poll-sources': {
      const result = await executeRelay();
      console.log('scheduled-relay-complete', summarize(result));
      writeJson<TaskResponse>(response, 200, {});
      return;
    }
    case '/internal/menu/run-now': {
      const result = await executeRelay();
      writeJson<UiResponse>(response, 200, {
        showToast: {
          appearance: result.errors.length === 0 ? 'success' : 'neutral',
          text: formatResult(result),
        },
      });
      return;
    }
    case '/internal/menu/status': {
      const [previous, attention] = await Promise.all([
        getLastResult(),
        redis.hGetAll(ATTENTION_INDEX_KEY),
      ]);
      const current = loadRelaySettings(await settings.getAll<Record<string, unknown>>());
      const state = current.enabled ? 'enabled' : 'disabled';
      const attentionIds = Object.keys(attention);
      const attentionText = attentionIds.length
        ? ` Moderator inspection required for ${attentionIds.length} attempt(s): ${attentionIds.slice(0, 3).join(', ')}.`
        : '';
      const text = previous
        ? `${state}. Last run: ${formatResult(previous)}`
        : `${state}. No relay run has been recorded yet.`;
      writeJson<UiResponse>(response, 200, {
        showToast: { appearance: 'neutral', text: `${text}${attentionText}` },
      });
      return;
    }
    case '/internal/settings/validate-destination': {
      const body = await readJson<SettingsValidationRequest<string>>(request);
      const entered = normalizeSubredditName(body.value ?? '');
      const installed = normalizeSubredditName(context.subredditName);
      const valid = entered.length === 0 || entered === installed;
      writeJson<SettingsValidationResponse>(response, 200, {
        success: valid,
        ...(valid
          ? {}
          : { error: `Enter ${context.subredditName}; this app only writes to its installed community.` }),
      });
      return;
    }
    case '/internal/settings/validate-hourly-cap': {
      await validateNumberSetting(request, response, 1, 12, 'Hourly cap');
      return;
    }
    case '/internal/settings/validate-daily-cap': {
      await validateNumberSetting(request, response, 1, 48, 'Daily cap');
      return;
    }
    default:
      writeJson(response, 404, { error: 'not found' });
  }
}

async function executeRelay(): Promise<RelayRunResult> {
  const relaySettings = loadRelaySettings(
    await settings.getAll<Record<string, unknown>>(),
  );
  const result = await runRelayOnce({
    destination: context.subredditName,
    settings: relaySettings,
    store: redis,
    fetchNewPosts: async (source, limit) =>
      reddit
        .getNewPosts({ subredditName: source, limit, pageSize: limit })
        .get(limit),
    log: (message, fields) => console.log(message, fields ?? {}),
  });
  await redis.set(LAST_RESULT_KEY, JSON.stringify(result), {
    expiration: new Date(Date.now() + LAST_RESULT_TTL_SECONDS * 1000),
  });
  return result;
}

async function getLastResult(): Promise<RelayRunResult | undefined> {
  const value = await redis.get(LAST_RESULT_KEY);
  if (!value) return undefined;
  try {
    return JSON.parse(value) as RelayRunResult;
  } catch {
    return undefined;
  }
}

async function validateNumberSetting(
  request: IncomingMessage,
  response: ServerResponse,
  minimum: number,
  maximum: number,
  label: string,
): Promise<void> {
  const body = await readJson<SettingsValidationRequest<number>>(request);
  const valid = isIntegerInRange(body.value, minimum, maximum);
  writeJson<SettingsValidationResponse>(response, 200, {
    success: valid,
    ...(valid
      ? {}
      : { error: `${label} must be a whole number from ${minimum} to ${maximum}.` }),
  });
}

function formatResult(result: RelayRunResult): string {
  const baseline = result.baselinedSources.length
    ? ` Baseline created for r/${result.baselinedSources.join(', r/')}; no old posts were forwarded.`
    : '';
  const capped = result.capped ? ' Cap reached.' : '';
  const errors = result.errors.length ? ` ${result.errors.length} error(s).` : '';
  const uncertain = result.uncertain
    ? ` ${result.uncertain} write(s) uncertain; no automatic retry.`
    : '';
  return `${result.status}: ${result.crossposted}/${result.attempted} attempts crossposted; ${result.fetched} fetched.${baseline}${capped}${uncertain}${errors}`;
}

function summarize(result: RelayRunResult): Record<string, unknown> {
  return {
    status: result.status,
    destination: result.destination,
    sources: result.sources,
    baselinedSources: result.baselinedSources,
    fetched: result.fetched,
    attempted: result.attempted,
    crossposted: result.crossposted,
    uncertain: result.uncertain,
    attentionRequired: result.attentionRequired,
    skippedUnsafe: result.skippedUnsafe,
    skippedDuplicate: result.skippedDuplicate,
    capped: result.capped,
    errorCount: result.errors.length,
  };
}

async function readJson<T>(request: IncomingMessage): Promise<T> {
  const chunks: Uint8Array[] = [];
  request.on('data', (chunk: Uint8Array) => chunks.push(chunk));
  await once(request, 'end');
  return JSON.parse(Buffer.concat(chunks).toString('utf8')) as T;
}

function writeJson<T>(
  response: ServerResponse,
  status: number,
  value: T,
): void {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(body),
  });
  response.end(body);
}
