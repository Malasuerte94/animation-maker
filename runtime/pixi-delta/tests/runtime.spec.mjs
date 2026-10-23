import { test, expect } from '@playwright/test';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const fixtures = process.env.DELTA_FIXTURES || '/artifacts/delta-validation';
for(const backend of ['webgl','webgpu']) {
    test(`${backend}: compact v2 tuples preserve alpha and reject malformed input`,async({page})=>{
        await page.goto('/runtime/pixi-delta/tests/harness.html');
        expect(await page.evaluate(async backend=>{
            const canvas=document.createElement('canvas');canvas.width=8;canvas.height=8;
            const ctx=canvas.getContext('2d');ctx.fillStyle='rgba(80,140,230,0.5)';ctx.fillRect(0,0,8,8);
            const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/webp',0.75));
            const manifest={format:'animation-maker-delta',version:2,width:16,height:16,fps:29.97,
                pages:[['delta-000.webp',8,8]],patches:[[0,0,0,8,8]],keyframes:[0],
                frames:[[0,0,0],[],[-1,0,0,8,8,0,8,8],[-1,8,8,8,8]]};
            const app=new PIXI.Application();await app.init({preference:backend,preferWebGLVersion:2,width:16,height:16,autoStart:false,backgroundAlpha:0});
            const asset=await AnimationMakerDelta.DeltaAtlasAsset.fromManifest(manifest,async()=>blob);
            const player=asset.createPlayer(app.renderer);
            for(const i of [0,1,2,3,2,0,3]){
                player.gotoAndStop(i);const pixels=await readDeltaPixels(app.renderer,player.texture);
                for(let y=0;y<16;y++)for(let x=0;x<16;x++){
                    const visible=i<2?x<8&&y<8:i===2?x>=8&&y>=8:false;
                    const alpha=pixels[(y*16+x)*4+3];
                    if(visible?alpha<126||alpha>130:alpha!==0)throw Error('Incorrect v2 alpha at frame '+i);
                }
            }
            const mutations=[
                m=>m.frames[0]=[9999,0,0],m=>m.frames[0]=[0,0],m=>m.frames[0]=[-2,0,0],
                m=>m.frames[0]=[-1,0,0,17,16],m=>m.frames[0]=[0,-1,0],
                m=>m.patches[0]=[0,0,0,8],m=>m.pages[0]=['../escape.webp',8,8],
                m=>m.keyframes=[0,0],m=>m.keyframes=[1],m=>m.fps=0
            ];
            for(const mutate of mutations){
                const bad=structuredClone(manifest);mutate(bad);
                let rejected=false,reads=0;
                try{const loaded=await AnimationMakerDelta.DeltaAtlasAsset.fromManifest(bad,async()=>{reads++;return blob;});loaded.destroy();}catch{rejected=true;}
                if(!rejected||reads)throw Error('Malformed compact manifest accepted before page loading');
            }
            asset.destroy();app.destroy(true);return true;
        },backend)).toBe(true);
    });
    test(`${backend}: WebP pages preserve alpha through hold, clear and seek`,async({page})=>{
        await page.goto('/runtime/pixi-delta/tests/harness.html');
        const result=await page.evaluate(async backend=>{
            const canvas=document.createElement('canvas');canvas.width=16;canvas.height=16;
            const ctx=canvas.getContext('2d');ctx.fillStyle='rgba(190,80,30,0.5)';ctx.fillRect(0,0,16,16);
            const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/webp',0.75));
            if(!blob||blob.type!=='image/webp')throw Error('WebP encoding unavailable');
            const rect={x:0,y:0,width:16,height:16},replace={...rect,kind:'replace',patch:'p'};
            const frame=(keyframe,operations)=>({durationMs:1000/24,keyframe,operations,sha256:'0'.repeat(64)});
            const manifest={format:'animation-maker-delta',version:1,width:16,height:16,fps:24,
                encoding:{mode:'lossy',tolerance:0,textureFormat:'webp',textureQuality:75},
                pages:[{image:'delta-000.webp',width:16,height:16}],patches:{p:{page:0,rect}},keyframes:[0],
                frames:[frame(true,[replace]),frame(false,[]),frame(false,[{...rect,kind:'clear'}]),frame(false,[replace])]};
            const app=new PIXI.Application();await app.init({preference:backend,preferWebGLVersion:2,width:16,height:16,autoStart:false,backgroundAlpha:0});
            const asset=await AnimationMakerDelta.DeltaAtlasAsset.fromManifest(manifest,async name=>{
                if(name!=='delta-000.webp')throw Error('Unexpected image name');return blob;
            });
            const player=asset.createPlayer(app.renderer);app.stage.addChild(player.sprite);
            const pixels=async()=>{app.renderer.render(app.stage);return Array.from(await readDeltaPixels(app.renderer,player.texture));};
            const first=await pixels();if(first[3]<126||first[3]>130)throw Error('Semitransparent alpha lost: '+JSON.stringify(first.slice(0,8)));
            for(const i of [1,3,0,2,3,1]){
                player.gotoAndStop(i);const current=await pixels();
                if(i===2?current.some(v=>v!==0):current.some((v,j)=>v!==first[j]))throw Error('Incorrect WebP reconstruction at '+i);
            }
            const invalid=structuredClone(manifest);invalid.encoding.textureQuality=101;
            let rejected=false;try{await AnimationMakerDelta.DeltaAtlasAsset.fromManifest(invalid,async()=>blob);}catch{rejected=true;}
            if(!rejected)throw Error('Invalid WebP quality accepted');
            asset.destroy();app.destroy(true);return true;
        },backend);
        expect(result).toBe(true);
    });
    test(`${backend}: exact alpha, seek, loop, independent instances and GPU restoration`,async({page},info)=>{
        await page.goto('/runtime/pixi-delta/tests/harness.html');
        const result=await page.evaluate(({backend,fixtures})=>runDeltaValidation(backend,fixtures),{backend,fixtures});
        expect(result.exactRgbaComparisons).toBeGreaterThan(90);
        await info.attach('validation.json',{body:JSON.stringify(result,null,2),contentType:'application/json'});
        console.log(JSON.stringify({project:info.project.name,...result}));
    });
    test(`${backend}: 1080p reconstruction and presentation benchmark`,async({page},info)=>{
        await page.goto('/runtime/pixi-delta/tests/harness.html');
        const result=await page.evaluate(({backend,fixtures})=>runDeltaBenchmark(backend,fixtures),{backend,fixtures});
        expect(result.meanMs).toBeLessThan(1000/30);
        await info.attach('benchmark.json',{body:JSON.stringify(result,null,2),contentType:'application/json'});
        console.log(JSON.stringify({project:info.project.name,...result}));
    });
}
test('offline demo loads folder and supports scrub without network',async({page})=>{
    await page.goto(pathToFileURL(resolve('dist/demo.html')).href);
    await page.locator('#folder').setInputFiles(resolve('../..' + fixtures + '/sparse'));
    await expect(page.locator('#status')).toContainText('70 frames verified');
    await page.locator('#scrub').fill('38');await expect(page.locator('#time')).toHaveText('39 / 70');
    await page.locator('#regions').check();await page.locator('#next').click();await expect(page.locator('#time')).toHaveText('40 / 70');
});
