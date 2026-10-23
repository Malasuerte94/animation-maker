import { defineConfig } from '@playwright/test';
export default defineConfig({
    testDir: './tests', testMatch: '**/*.spec.mjs', timeout: 120000, workers: 1,
    use: { baseURL: 'http://127.0.0.1:8869', headless: true, launchOptions: { args: ['--enable-unsafe-webgpu'] } },
    projects: [{ name: 'chrome', use: { channel: 'chrome' } }, { name: 'edge', use: { channel: 'msedge' } }],
    webServer: { command: 'node tests/server.mjs', url: 'http://127.0.0.1:8869/runtime/pixi-delta/tests/harness.html', reuseExistingServer: false }
});
