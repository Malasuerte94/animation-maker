import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { test, expect } from '@playwright/test';

function fixture() {
    const width = 132, height = 50;
    const rgb = Buffer.alloc(width * height * 3);
    for (let y = 0; y < 49; y++) {
        for (let x = 0; x < 65; x++) {
            const i = (y * width + x) * 3;
            const alpha = x < 20 ? 0 : x < 44 ? 128 : 255;
            rgb[i] = 240;
            rgb.fill(alpha, i + 66 * 3, i + 67 * 3);
        }
    }
    const ffmpeg = resolve(process.env.ANIMATION_MAKER_FFMPEG ?? '../../.tools/ffmpeg', 'ffmpeg.exe');
    return execFileSync(ffmpeg, ['-v', 'error', '-f', 'rawvideo', '-pixel_format', 'rgb24',
        '-video_size', `${width}x${height}`, '-framerate', '12', '-i', 'pipe:0',
        '-frames:v', '1', '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
        '-movflags', '+empty_moov+frag_keyframe', '-f', 'mp4', 'pipe:1'], { input: rgb });
}

async function sample(page, preview) {
    const screenshot = (await preview.screenshot()).toString('base64');
    return page.evaluate(async data => {
        const img = new Image();
        img.src = 'data:image/png;base64,' + data;
        await img.decode();
        const canvas = document.createElement('canvas');
        canvas.width = img.width;
        canvas.height = img.height;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(img, 0, 0);
        const spriteWidth = Math.min(img.width, (img.height - 2) * 65 / 49);
        return [-.36, 0, .36].map(fraction => [...ctx.getImageData(
            Math.round(img.width / 2 + spriteWidth * fraction), Math.round(img.height / 2), 1, 1).data]);
    }, screenshot);
}

test('packed MP4 renders transparent, partial and opaque regions with cropped padding', async ({ page }) => {
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await page.goto('/runtime/pixi-delta/dist/packed-mp4.html');
    await page.locator('#file').setInputFiles({ name: 'fixture.mp4', mimeType: 'video/mp4', buffer: fixture() });
    await expect(page.locator('#status')).toContainText('66 × 50 px');
    await page.locator('#width').fill('65');
    await page.locator('#height').fill('49');
    await page.locator('#height').press('Tab');
    await expect(page.locator('#status')).toContainText('65 × 49 px');
    await page.locator('#play').click();
    await expect(page.locator('#play')).toHaveText('Pause');
    const preview = page.locator('#preview');
    await page.locator('#background').selectOption('white');
    const white = await sample(page, preview);
    await page.locator('#background').selectOption('black');
    const black = await sample(page, preview);
    // Limited-range H.264 and browser conversion can move the black level a few values.
    expect(Math.min(...white[0].slice(0, 3))).toBeGreaterThanOrEqual(250);
    expect(Math.max(...black[0].slice(0, 3))).toBeLessThanOrEqual(5);
    expect(black[1][0]).toBeGreaterThan(111);
    expect(black[1][0]).toBeLessThan(131);
    expect(white[1][1]).toBeGreaterThan(114);
    expect(white[1][1]).toBeLessThan(140);
    expect(black[2][0]).toBeGreaterThan(227);
    expect(white[2][1]).toBeLessThan(10);
    expect(errors).toEqual([]);
});
