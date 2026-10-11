import {
  PublicClientApplication,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";
export type AppConfig = {
  mode: "Demo" | "Azure";
  tenantId: string;
  clientId: string;
  scope: string;
};
let instance: PublicClientApplication | undefined;
let ended = false;
let pendingToken: Promise<string> | undefined;
let sessionController = new AbortController();
export function authSessionSignal() { return sessionController.signal; }
export async function initializeAuth(config: AppConfig) {
  sessionController.abort();
  sessionController = new AbortController();
  ended = false;
  pendingToken = undefined;
  if (config.mode === "Demo") return;
  instance = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: `${location.origin}/redirect.html`,
    },
    cache: { cacheLocation: "memoryStorage" },
  });
  await instance.initialize();
}
export async function getToken(config: AppConfig) {
  const signal = authSessionSignal();
  if (ended) throw new Error("接続を終了しました。再ログインしてください。");
  if (!instance) throw new Error("認証を初期化できませんでした。");
  // Initial panels can request credentials together; MSAL permits one interaction.
  if (!pendingToken) {
    const pending = acquireToken(config, signal).finally(() => { if (pendingToken === pending) pendingToken = undefined; });
    pendingToken = pending;
  }
  const token = await pendingToken;
  if (ended || signal.aborted) throw new Error("接続を終了しました。再ログインしてください。");
  return token;
}
async function acquireToken(config: AppConfig, signal: AbortSignal) {
  const client = instance!;
  const account = client.getAllAccounts()[0];
  const request = { scopes: [config.scope], account };
  let token: string;
  if (!account) token = (await client.loginPopup(request)).accessToken;
  else {
    try {
      token = (await client.acquireTokenSilent(request)).accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError)
        token = (await client.acquireTokenPopup(request)).accessToken;
      else throw error;
    }
  }
  if (ended || signal.aborted) throw new Error("接続を終了しました。再ログインしてください。");
  return token;
}
export async function endAuth() {
  ended = true;
  sessionController.abort();
  // Drop this application's private resume IDs and outboxes before logout waits.
  if (typeof sessionStorage !== 'undefined') {
    for (let index = sessionStorage.length - 1; index >= 0; index--) {
      const key = sessionStorage.key(index);
      if (key === 'dse-upload-job' || key?.startsWith('dse-upload-job:') || key?.startsWith('dse-outbox:') || key?.startsWith('dse-workspace-outbox:') || key?.startsWith('dse-business-analysis-request:')) sessionStorage.removeItem(key);
    }
  }
  if (!instance) return;
  const client = instance;
  const account = client.getAllAccounts()[0];
  try {
    await client.logoutPopup({ account, postLogoutRedirectUri: `${location.origin}/redirect.html` });
  } finally {
    await client.clearCache();
  }
}
