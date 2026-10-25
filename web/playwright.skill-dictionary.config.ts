import {defineConfig} from '@playwright/test';
export default defineConfig({testDir:'./tests',testMatch:'skill-dictionary.spec.ts',workers:1,
 use:{baseURL:'http://127.0.0.1:5175',viewport:{width:1360,height:1000},launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH}},
 webServer:[{command:'dotnet run --project ../tests/Dse.SkillDictionaryBrowserHost --no-launch-profile',url:'http://127.0.0.1:5182/health/live',timeout:120000},
 {command:'npm run dev -- --host 127.0.0.1 --port 5175',url:'http://127.0.0.1:5175',env:{API_PROXY_TARGET:'http://127.0.0.1:5182'},timeout:30000}]
});
