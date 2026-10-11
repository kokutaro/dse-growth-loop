import { getToken, type AppConfig } from './auth';

export type Api = <T>(path: string, method?: string, body?: unknown, key?: string, etag?: string) => Promise<T>;
export class WorkspaceError extends Error {
  constructor(public status: number, public code: string) { super(code); }
}
export function workspaceApi(config: AppConfig, person: string, signal?: AbortSignal): Api {
  // A tenant access fence serializes the server's read-and-serialize boundary.
  // Share one local queue across the workspace panels; writes are never retried here.
  let workspaceTail: Promise<unknown> = Promise.resolve();
  return <T>(path: string, method = 'GET', body?: unknown, key?: string, etag?: string): Promise<T> => {
    const execute = async (): Promise<T> => {
    const headers = new Headers();
    if (config.mode === 'Demo') headers.set('X-Demo-Person', person);
    else headers.set('Authorization', `Bearer ${await getToken(config)}`);
    if (body !== undefined) headers.set('Content-Type', 'application/json');
    if (key) headers.set('Idempotency-Key', key);
    if (etag) headers.set('If-Match', etag);
    for (let attempt = 0; ; attempt++) {
      const response = await fetch(`/api/v1${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
      const value = await response.json().catch(() => ({}));
      if (method === 'GET' && response.status === 503 && value.code === 'ACCESS_BUSY' && attempt < 3) {
        await new Promise(resolve => setTimeout(resolve, 200 * 2 ** attempt));
        continue;
      }
      if (!response.ok) throw new WorkspaceError(response.status, value.code ?? 'CONNECTION_FAILED');
      return { ...value, ...(response.headers.get('ETag') ? { etag: response.headers.get('ETag') } : {}) } as T;
    }
    };
    if (!path.startsWith('/workspace/') && !path.startsWith('/business-analysis')) return execute();
    const result = workspaceTail.then(execute);
    workspaceTail = result.catch(() => undefined);
    return result;
  };
}
export async function readPages<T>(api: Api, path: string) {
  let token: string | null = null;
  const items: T[] = [];
  do {
    const page: { items: T[]; continuationToken: string | null } = await api(`${path}${path.includes('?') ? '&' : '?'}pageSize=100${token ? `&continuationToken=${encodeURIComponent(token)}` : ''}`);
    items.push(...page.items); token = page.continuationToken;
  } while (token);
  return items;
}
export function friendlyError(error: unknown) {
  if (error instanceof WorkspaceError) {
    if (error.code === 'IDENTIFYING_CONTENT') return '氏名・連絡先・識別情報を含むため保存していません。匿名の内容に修正してください。';
    if (error.code === 'SEARCH_CONDITIONS_REQUIRED') return '相談の分野や具体的なキーワードを追加し、候補を再確認してください。';
    if (error.code === 'ANALYSIS_RECOVERY_REQUIRED') return '中断した分析の停止確認と復旧が必要です。新しい分析は開始していません。';
    if (error.status === 412) return '別の更新がありました。最新の状態を読み込み、内容を確認して再試行してください。';
    if (error.status === 429) return '操作が続いています。少し時間をおいて再試行してください。';
    if ([401, 403, 404].includes(error.status)) return '現在の参加状態では閲覧・操作できません。最新の状態を確認してください。';
    if (error.status === 409) return 'すでに処理済み、または現在の状態では実行できません。最新の状態を確認してください。';
  }
  return '接続または保存を確認できませんでした。入力は保持しています。再試行してください。';
}

export type WorkspaceCapabilities = { contractVersion: string; available: boolean; structuredIdentifiersRemoved: boolean; roomLocalAliases: boolean; opaqueOperationIds: boolean; freeTextAnonymized: boolean; legacyApiIsolation: string; processingMode: string };
export type Challenge = { id: string; roomId: string | null; title: string; summary: string; status: string; isOwner: boolean; etag: string };
export type InvitationRow = { id: string; challengeId: string; roomId: string | null; title: string; summary: string; status: string; isOwner: boolean; aliasId: string; aliasLabel: string; etag: string };
export type RoomRow = { id: string; challenge: Challenge; status: string; aiAssistance: string; isOwner: boolean; participantCount: number; participants: { aliasId: string; aliasLabel: string; isSelf: boolean }[]; etag: string; participation?: InvitationRow };
export type MessageRow = { id: string; roomId: string; body: string; authorType: string; authorAliasId: string; authorAliasLabel: string; isSelf: boolean; sequence: number; deliveryState: string; citationState: string; clientMessageId?: string | null };
export type WorkspaceCase = { id: string; title: string; summary: string; sections: { field: string; text: string; basis: string }[]; version: number; processingMode: string };
