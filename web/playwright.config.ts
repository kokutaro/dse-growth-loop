import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  use: {
    baseURL: "http://127.0.0.1:5173",
    viewport: { width: 1280, height: 900 },
  },
  webServer: [
    {
      command: "dotnet run --project ../src/Dse.Api --no-launch-profile",
      url: "http://127.0.0.1:5180/health/live",
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        Data__Mode: "Demo",
        ASPNETCORE_URLS: "http://127.0.0.1:5180",
      },
      timeout: 120000,
    },
    {
      command: "npm run dev -- --host 127.0.0.1",
      url: "http://127.0.0.1:5173",
      timeout: 30000,
    },
  ],
});
