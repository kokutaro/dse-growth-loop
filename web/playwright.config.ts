import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  testIgnore: 'skill-dictionary.spec.ts', // Dedicated loopback-only fixture host; see test:e2e:dictionary.
  // Tests update the same seeded identities and use one in-memory API instance.
  workers: 1,
  use: {
    // Optional local browser when the pinned download CDN is unavailable.
    // CI leaves this unset and continues using Playwright's pinned Chromium.
    launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH },
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
        Ingestion__RunWorker: "true",
        Ai__Mode: "Mock",
        BusinessAnalysis__DemoFixtures: process.env.DSE_ANALYSIS_FIXTURES ?? "false",
        Profile__SecondaryWorkFragments: "true",
        AiUsage__Stopped: "false",
        AiUsage__MaxOutputTokens: "3000",
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
