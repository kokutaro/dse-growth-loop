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
export async function initializeAuth(config: AppConfig) {
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
  if (!instance) throw new Error("認証を初期化できませんでした。");
  const account = instance.getAllAccounts()[0];
  const request = { scopes: [config.scope], account };
  if (!account) return (await instance.loginPopup(request)).accessToken;
  try {
    return (await instance.acquireTokenSilent(request)).accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError)
      return (await instance.acquireTokenPopup(request)).accessToken;
    throw error;
  }
}
