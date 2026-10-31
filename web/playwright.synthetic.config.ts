import {defineConfig} from '@playwright/test';
export default defineConfig({testDir:'./tests',testMatch:'synthetic-profiles.spec.ts',workers:1,
 use:{baseURL:'http://127.0.0.1:5176',viewport:{width:1280,height:1000},launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH}},
 webServer:[{command:'dotnet run --project ../tests/Dse.SyntheticBrowserHost --no-launch-profile',url:'http://127.0.0.1:5183/health/live',timeout:120000},
 {command:'npm run dev -- --host 127.0.0.1 --port 5176',url:'http://127.0.0.1:5176',env:{API_PROXY_TARGET:'http://127.0.0.1:5183'},timeout:30000}]});
