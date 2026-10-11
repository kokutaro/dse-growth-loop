import { test, mock } from 'node:test';
import assert from 'node:assert/strict';

let completeLogout;
let cleared = 0;
let requests = 0;
let failLogout = false;
class FakeClient {
  async initialize() {}
  getAllAccounts() { return [{ homeAccountId: 'dummy' }]; }
  async acquireTokenSilent() { requests++; return { accessToken: 'dummy' }; }
  async logoutPopup() {
    if (failLogout) throw new Error('popup blocked');
    await new Promise(resolve => { completeLogout = resolve; });
  }
  async clearCache() { cleared++; }
}
mock.module('@azure/msal-browser', { namedExports: { PublicClientApplication: FakeClient, InteractionRequiredAuthError: class extends Error {} } });
globalThis.location = { origin: 'https://dummy.example' };
const { initializeAuth, getToken, endAuth, authSessionSignal } = await import('../src/auth.ts');
const config = { mode: 'Azure', tenantId: 'dummy', clientId: 'dummy', scope: 'dummy' };

test('logout blocks background token refresh immediately and clears cache', async () => {
  await initializeAuth(config);
  assert.equal(await getToken(config), 'dummy');
  const pending = endAuth();
  await assert.rejects(getToken(config));
  assert.equal(requests, 1);
  completeLogout(); await pending;
  assert.equal(cleared, 1);
  await assert.rejects(getToken(config));
});

test('failed logout popup still clears local credentials and blocks token refresh', async () => {
  await initializeAuth(config); failLogout = true;
  await assert.rejects(endAuth());
  assert.equal(cleared, 2);
  await assert.rejects(getToken(config));
});

test('logout clears private upload resume IDs and both chat outboxes even when the popup fails', async () => {
  const values = new Map([['dse-upload-job', 'old'], ['dse-upload-job:v2:Azure:tenant:person-a', 'private-id'], ['dse-outbox:tenant:person:session', 'legacy-private-text'], ['dse-workspace-outbox:v2:Azure:tenant:room:alias', 'private-text'], ['dse-business-analysis-request:v1:owner', 'private-run'], ['unrelated', 'preserved']]);
  globalThis.sessionStorage = { get length() { return values.size; }, key: index => [...values.keys()][index] ?? null, removeItem: key => values.delete(key) };
  await initializeAuth(config); failLogout = true;
  const logout = endAuth();
  assert.deepEqual([...values], [['unrelated', 'preserved']]);
  await assert.rejects(logout);
  delete globalThis.sessionStorage;
});

test('simultaneous initial requests share one login popup', async () => {
  failLogout = false;
  let release;
  let popups = 0;
  FakeClient.prototype.getAllAccounts = () => [];
  FakeClient.prototype.loginPopup = async () => {
    popups++;
    return await new Promise(resolve => { release = resolve; });
  };
  await initializeAuth(config);
  const first = getToken(config);
  const second = getToken(config);
  assert.equal(popups, 1);
  release({ accessToken: 'dummy' });
  assert.deepEqual(await Promise.all([first, second]), ['dummy', 'dummy']);
});

test('token response completing after logout is never returned', async () => {
  let releaseToken;
  FakeClient.prototype.getAllAccounts = () => [{ homeAccountId: 'dummy' }];
  FakeClient.prototype.acquireTokenSilent = () => new Promise(resolve => { releaseToken = resolve; });
  await initializeAuth(config);
  const token = getToken(config);
  const rejected = assert.rejects(token, /接続を終了しました/);
  const logout = endAuth();
  completeLogout(); await logout;
  releaseToken({ accessToken: 'dummy' });
  await rejected;
});

test('logout aborts authenticated operations before the popup completes', async () => {
  await initializeAuth(config);
  const signal = authSessionSignal();
  const logout = endAuth();
  assert.equal(signal.aborted, true);
  completeLogout(); await logout;
});

test('reinitializing an account aborts the old session and rejects its delayed token', async () => {
  let releaseOld;
  FakeClient.prototype.acquireTokenSilent = () => new Promise(resolve => { releaseOld = resolve; });
  await initializeAuth(config);
  const signal = authSessionSignal();
  const oldToken = getToken(config);
  const rejected = assert.rejects(oldToken, /接続を終了しました/);
  await initializeAuth(config);
  assert.equal(signal.aborted, true);
  assert.equal(authSessionSignal().aborted, false);
  let releaseNew, newRequests = 0;
  FakeClient.prototype.acquireTokenSilent = () => {
    newRequests++;
    return new Promise(resolve => { releaseNew = resolve; });
  };
  const newToken = getToken(config);
  releaseOld({ accessToken: 'old-session-token' });
  await rejected;
  const sharedNewToken = getToken(config);
  assert.equal(newRequests, 1);
  releaseNew({ accessToken: 'new-session-token' });
  assert.deepEqual(await Promise.all([newToken, sharedNewToken]), ['new-session-token', 'new-session-token']);
});
