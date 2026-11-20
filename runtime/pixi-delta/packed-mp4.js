// Pass the PixiJS 8 module (or global PIXI) and a video with loaded metadata.
// This mesh uses the WebGL renderer. The caller controls play(), pause() and audio.
export async function createPackedMp4Mesh(PIXI, video, size) {
    const fullWidth = video.videoWidth;
    const fullHeight = video.videoHeight;
    if (!fullWidth || fullWidth % 2 || !fullHeight) {
        throw new Error('Expected a video with two equal-width halves.');
    }

    const halfWidth = fullWidth / 2;
    const source = new PIXI.VideoSource({ resource: video, autoPlay: false, autoLoad: false });
    await source.load();
    // The video texture is opaque. The custom shader supplies premultiplied RGBA.
    source.alphaMode = 'premultiply-alpha-on-upload';
    const texture = new PIXI.Texture({ source });
    const geometry = new PIXI.MeshGeometry();
    const shader = PIXI.Shader.from({
        gl: {
            vertex: `
                in vec2 aPosition;
                in vec2 aUV;
                out vec2 vUV;
                uniform mat3 uProjectionMatrix;
                uniform mat3 uWorldTransformMatrix;
                uniform mat3 uTransformMatrix;
                void main() {
                    vec3 position = uProjectionMatrix * uWorldTransformMatrix * uTransformMatrix * vec3(aPosition, 1.0);
                    gl_Position = vec4(position.xy, 0.0, 1.0);
                    vUV = aUV;
                }
            `,
            fragment: `
                precision highp float;
                in vec2 vUV;
                out vec4 finalColor;
                uniform sampler2D uTexture;
                uniform vec2 uLogicalSize;
                uniform vec2 uEncodedSize;
                uniform vec4 uColor;
                uniform vec4 uWorldColorAlpha;
                void main() {
                    vec2 pixel = clamp(vUV * uLogicalSize, vec2(0.5), uLogicalSize - 0.5);
                    vec2 uv = pixel / uEncodedSize;
                    vec3 rgb = texture(uTexture, uv).rgb;
                    float alpha = texture(uTexture, uv + vec2(0.5, 0.0)).r;
                    finalColor = vec4(rgb * alpha, alpha) * uColor * uWorldColorAlpha;
                }
            `
        },
        resources: {
            uTexture: source,
            videoUniforms: {
                uLogicalSize: { value: new Float32Array([halfWidth, fullHeight]), type: 'vec2<f32>' },
                uEncodedSize: { value: new Float32Array([fullWidth, fullHeight]), type: 'vec2<f32>' }
            }
        }
    });
    const mesh = new PIXI.Mesh({ geometry, shader, texture });

    function setLogicalSize({ width = halfWidth, height = fullHeight } = {}) {
        if (!Number.isInteger(width) || width < 1 || width > halfWidth ||
            !Number.isInteger(height) || height < 1 || height > fullHeight) {
            throw new Error(`Dimensions must fit within ${halfWidth} × ${fullHeight} px.`);
        }
        shader.resources.videoUniforms.uniforms.uLogicalSize.set([width, height]);
        geometry.positions = new Float32Array([0, 0, width, 0, width, height, 0, height]);
    }

    function destroy() {
        mesh.destroy({ texture: true, textureSource: true });
        geometry.destroy(true);
        shader.destroy();
    }

    try {
        setLogicalSize(size);
        return { mesh, setLogicalSize, destroy };
    } catch (error) {
        destroy();
        throw error;
    }
}
