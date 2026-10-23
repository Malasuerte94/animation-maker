import { build } from 'esbuild';
import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
await mkdir('dist/vendor', { recursive: true });
await build({ entryPoints: ['src/index.ts'], outfile: 'dist/delta-runtime.js', format: 'esm', bundle: true, external: ['pixi.js'], minify: true, target: 'es2022' });
await build({ entryPoints: ['src/index.ts'], outfile: 'dist/delta-runtime.iife.js', format: 'iife', globalName: 'AnimationMakerDelta', bundle: true, plugins: [{ name: 'pixi-global', setup(b) { b.onResolve({ filter: /^pixi\.js$/ }, () => ({ path: 'pixi.js', namespace: 'global' })); b.onLoad({ filter: /.*/, namespace: 'global' }, () => ({ contents: 'module.exports = globalThis.PIXI;', loader: 'js' })); } }], minify: true, target: 'es2022' });
execFileSync(process.execPath, ['node_modules/typescript/bin/tsc', '--emitDeclarationOnly'], { stdio: 'inherit' });
await copyFile('dist/index.d.ts', 'dist/delta-runtime.d.ts');
await copyFile('node_modules/pixi.js/dist/pixi.min.js', 'dist/vendor/pixi.min.js');
await copyFile('node_modules/pixi.js/LICENSE', 'dist/vendor/PIXI-LICENSE.txt');
await copyFile('demo.html', 'dist/demo.html');
await copyFile('README.md', 'dist/README.md');
await copyFile('../../LICENSE', 'dist/LICENSE');
const manifest = JSON.parse(await readFile('package.json', 'utf8'));
await writeFile('dist/package.json', JSON.stringify({ name: manifest.name, version: manifest.version, type: 'module',
    main: './delta-runtime.js', types: './delta-runtime.d.ts',
    exports: { '.': { types: './delta-runtime.d.ts', import: './delta-runtime.js' }, './global': './delta-runtime.iife.js' },
    peerDependencies: manifest.peerDependencies, license: 'MIT' }, null, 2) + '\n');
