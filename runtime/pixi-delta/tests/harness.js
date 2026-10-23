// Pixi's WebGPU canvas extraction can copy RGBA into a BGRA canvas and return zeros.
// Read RGBA render textures directly so alpha and reconstruction checks cannot pass on empty readback.
globalThis.readDeltaPixels = async function(renderer, texture) {
    if(renderer.type!==2)return renderer.extract.pixels({target:texture}).pixels;
    const device=renderer.gpu.device,w=texture.source.pixelWidth,h=texture.source.pixelHeight,row=Math.ceil(w*4/256)*256;
    const buffer=device.createBuffer({size:row*h,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ});
    try {
        const command=device.createCommandEncoder();
        command.copyTextureToBuffer({texture:renderer.texture.getGpuSource(texture.source)},{buffer,bytesPerRow:row,rowsPerImage:h},{width:w,height:h});
        device.queue.submit([command.finish()]);await buffer.mapAsync(GPUMapMode.READ);
        const mapped=new Uint8Array(buffer.getMappedRange()),result=new Uint8Array(w*h*4);
        for(let y=0;y<h;y++)result.set(mapped.subarray(y*row,y*row+w*4),y*w*4);
        buffer.unmap();return result;
    } finally {buffer.destroy();}
};
globalThis.runDeltaValidation = async function(backend) {
    const { Application, ImageSource, Texture, Sprite, RenderTexture } = PIXI;
    const { DeltaAtlasAsset } = AnimationMakerDelta;
    const app = new Application(); await app.init({ preference: backend, preferWebGLVersion: 2, width: 120, height: 90, autoStart: false, resolution: 1, antialias: false, backgroundAlpha: 0 });
    if ((backend === 'webgpu' ? app.renderer.type !== 2 : app.renderer.type !== 1)) throw Error('Requested renderer unavailable: ' + app.renderer.type);
    const errors = []; if (backend === 'webgpu') app.renderer.gpu.device.addEventListener('uncapturederror', e => errors.push(e.error.message));
    const asset = await DeltaAtlasAsset.load('/artifacts/delta-validation/sparse/delta.animation.json');
    const p = asset.createPlayer(app.renderer), p2 = asset.createPlayer(app.renderer);
    const m = asset.manifest, refs = [], bitmaps = [];
    if(!m.frames.some(f=>f.operations.length>1)||!m.frames.some(f=>!f.keyframe&&!f.operations.length)||!m.frames.some(f=>!f.keyframe&&f.operations.some(o=>o.kind==='clear')))throw Error('Fixture must exercise multi-patch, hold and partial clear frames');
    const referenceTarget = RenderTexture.create({ width: m.width, height: m.height, resolution: 1, format: 'rgba8unorm', alphaMode: 'premultiplied-alpha', antialias: false });
    for (let i = 0; i < m.frames.length; i++) {
        const bitmap = await createImageBitmap(await (await fetch(`/artifacts/delta-validation/sparse/references/${String(i).padStart(3,'0')}.png`)).blob(), { premultiplyAlpha: 'premultiply', colorSpaceConversion: 'none' });
        bitmaps.push(bitmap); refs.push(new Texture({ source: new ImageSource({ resource: bitmap, alphaMode: 'premultiplied-alpha', scaleMode: 'nearest' }) }));
    }
    const expected = new Sprite();
    async function check(player, index) {
        expected.texture = refs[index]; app.renderer.render({ container: expected, target: referenceTarget, clear: true, clearColor: [0,0,0,0] });
        const a = await readDeltaPixels(app.renderer,player.texture), b = await readDeltaPixels(app.renderer,referenceTarget);
        let count = 0, first = -1; for (let i=0;i<a.length;i++) if(a[i]!==b[i]) {count++;if(first<0)first=i;}
        if(count)throw Error(`${backend} frame ${index}: ${count} differing bytes; first ${first}: got ${a[first]}, expected ${b[first]}`);
    }
    let checks = 0;
    for(let i=0;i<m.frames.length;i++) { p.gotoAndStop(i);await check(p,i);checks++; }
    for(const i of [69,2,35,0,60,29,30,38,39,1]) {p.gotoAndStop(i);p2.gotoAndStop((i+11)%70);await check(p,i);await check(p2,(i+11)%70);checks+=2;}
    p.gotoAndPlay(0);p.update(9.01*1000/m.fps);await check(p,9);checks++;
    p.update((70*2+5.01)*1000/m.fps);await check(p,14);checks++;
    p.loop=false;p.gotoAndPlay(68);p.update(1000);if(p.playing||p.currentFrame!==69)throw Error('Non-looping playback did not stop');await check(p,69);checks++;
    p.gotoAndStop(17);p.invalidate();p.update(0);await check(p,17);checks++;
    if(backend==='webgl') {
        const gl=app.renderer.gl, ext=gl.getExtension('WEBGL_lose_context');
        if(!ext)throw Error('Missing WEBGL_lose_context test extension');
        await new Promise(resolve=>{app.canvas.addEventListener('webglcontextlost',()=>resolve(),{once:true});ext.loseContext();});
        await new Promise(resolve=>setTimeout(resolve,100));
        await new Promise(resolve=>{app.canvas.addEventListener('webglcontextrestored',()=>resolve(),{once:true});ext.restoreContext();});
        p.update(0);await check(p,17);p2.update(0);await check(p2,p2.currentFrame);checks+=2;
    } else {
        const old=app.renderer.gpu.device;
        const restored=new Promise(resolve=>{const listener={contextChange:()=>{app.renderer.runners.contextChange.remove(listener);resolve();}};app.renderer.runners.contextChange.add(listener);});
        old.destroy();await restored;p.update(0);await check(p,17);p2.update(0);await check(p2,p2.currentFrame);checks+=2;
    }
    const emptyAsset=await DeltaAtlasAsset.load('/artifacts/delta-validation/empty/delta.animation.json');const empty=emptyAsset.createPlayer(app.renderer);
    empty.gotoAndStop(34);if((await readDeltaPixels(app.renderer,empty.texture)).some(v=>v!==0))throw Error('Empty animation is not transparent');emptyAsset.destroy();
    const invalid=structuredClone(m);invalid.frames[0].operations=[{kind:'replace',x:0,y:0,width:1,height:1,patch:'unknown'}];
    let rejected=false;try{await DeltaAtlasAsset.fromManifest(invalid,()=>Promise.reject(Error('Unexpected page read')));}catch{rejected=true;}if(!rejected)throw Error('Malformed manifest accepted');
    const adapter=backend==='webgpu'?app.renderer.gpu.adapter.info.description:app.renderer.gl.getParameter(app.renderer.gl.getExtension('WEBGL_debug_renderer_info').UNMASKED_RENDERER_WEBGL);
    asset.destroy();for(const r of refs)r.destroy(true);for(const b of bitmaps)b.close();expected.destroy();referenceTarget.destroy(true);app.destroy(true);
    if(errors.length)throw Error(errors.join('\n'));
    return {backend,adapter,exactRgbaComparisons:checks,checkpoints:true,contextRestoration:true,independentPlayers:true};
};
globalThis.runDeltaBenchmark=async function(backend) {
    const app=new PIXI.Application();await app.init({preference:backend,preferWebGLVersion:2,width:1920,height:1080,autoStart:false,resolution:1,antialias:false});
    const asset=await AnimationMakerDelta.DeltaAtlasAsset.load('/artifacts/delta-validation/benchmark/delta.animation.json');const p=asset.createPlayer(app.renderer);app.stage.addChild(p.sprite);p.play();
    const times=[],probe=new Uint8Array(4);
    for(let i=0;i<150;i++) {
        const start=performance.now();p.update(1000/30);app.renderer.render(app.stage);
        if(backend==='webgpu')await app.renderer.gpu.device.queue.onSubmittedWorkDone();else {const gl=app.renderer.gl;gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,probe);if(gl.isContextLost()||gl.getError()!==gl.NO_ERROR)throw Error('WebGL benchmark context error');}
        if(i>=30)times.push(performance.now()-start);
    }
    const adapter=backend==='webgpu'?JSON.stringify(app.renderer.gpu.adapter.info.toJSON?.()||{vendor:app.renderer.gpu.adapter.info.vendor,architecture:app.renderer.gpu.adapter.info.architecture,device:app.renderer.gpu.adapter.info.device,description:app.renderer.gpu.adapter.info.description}):app.renderer.gl.getParameter(app.renderer.gl.getExtension('WEBGL_debug_renderer_info').UNMASKED_RENDERER_WEBGL);
    times.sort((a,b)=>a-b);const result={backend,adapter,medianMs:times[Math.floor(times.length/2)],meanMs:times.reduce((a,b)=>a+b,0)/times.length,p95Ms:times[Math.floor(times.length*.95)],samples:times.length,includes:'delta patches + display + GPU completion, no codecs or loading'};
    asset.destroy();app.destroy(true);return result;
};
