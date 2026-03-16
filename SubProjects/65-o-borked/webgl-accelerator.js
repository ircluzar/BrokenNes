/**
 * WebGL Acceleration Module for NES ROM Hole Reconstruction
 * Provides GPU-accelerated matrix operations for Transformer inference
 */

class WebGLTransformerAccelerator {
    constructor() {
        this.gl = null;
        this.programs = {};
        this.buffers = new Map();
        this.isInitialized = false;
    }

    /**
     * Initialize WebGL context and compile shaders
     */
    async initialize() {
        try {
            // Get WebGL2 context (required for compute-like operations)
            const canvas = document.createElement('canvas');
            this.gl = canvas.getContext('webgl2');
            
            if (!this.gl) {
                console.warn('WebGL2 not available, falling back to CPU');
                return false;
            }

            // Check for required extensions
            const ext = this.gl.getExtension('EXT_color_buffer_float');
            if (!ext) {
                console.warn('Float textures not supported');
                return false;
            }

            // Compile shader programs
            await this.compileShaders();
            
            this.isInitialized = true;
            console.log('? WebGL acceleration initialized');
            return true;
        } catch (error) {
            console.error('? WebGL initialization failed:', error);
            return false;
        }
    }

    /**
     * Compile WebGL shaders for various operations
     */
    async compileShaders() {
        // Matrix multiplication shader
        this.programs.matmul = this.createProgram(
            this.vertexShaderSource,
            this.matmulFragmentShader
        );

        // Softmax shader
        this.programs.softmax = this.createProgram(
            this.vertexShaderSource,
            this.softmaxFragmentShader
        );

        // Embedding lookup shader
        this.programs.embedding = this.createProgram(
            this.vertexShaderSource,
            this.embeddingFragmentShader
        );

        // Attention computation shader (simplified)
        this.programs.attention = this.createProgram(
            this.vertexShaderSource,
            this.attentionFragmentShader
        );
    }

    /**
     * Matrix multiplication: C = A * B
     */
    async matrixMultiply(aFlat, bFlat, m, n, p) {
        if (!this.isInitialized) {
            throw new Error('WebGL not initialized');
        }

        const gl = this.gl;
        const program = this.programs.matmul;

        // Create textures for input matrices
        const texA = this.createFloatTexture(aFlat, n, m);
        const texB = this.createFloatTexture(bFlat, p, n);
        
        // Create framebuffer for output
        const outputTexture = this.createEmptyTexture(p, m);
        const framebuffer = gl.createFramebuffer();
        
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, outputTexture, 0);

        // Set up shader program
        gl.useProgram(program);
        gl.viewport(0, 0, p, m);

        // Bind input textures
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, texA);
        gl.uniform1i(gl.getUniformLocation(program, 'uMatrixA'), 0);

        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, texB);
        gl.uniform1i(gl.getUniformLocation(program, 'uMatrixB'), 1);

        // Set dimensions
        gl.uniform1i(gl.getUniformLocation(program, 'uM'), m);
        gl.uniform1i(gl.getUniformLocation(program, 'uN'), n);
        gl.uniform1i(gl.getUniformLocation(program, 'uP'), p);

        // Draw quad to execute shader
        this.drawQuad();

        // Read result
        const result = this.readFloatTexture(outputTexture, p, m);

        // Cleanup
        gl.deleteTexture(texA);
        gl.deleteTexture(texB);
        gl.deleteTexture(outputTexture);
        gl.deleteFramebuffer(framebuffer);

        return result;
    }

    /**
     * Embedding lookup: select rows from embedding matrix
     */
    async embeddingLookup(embeddingsFlat, indices, embedDim) {
        if (!this.isInitialized) {
            throw new Error('WebGL not initialized');
        }

        const gl = this.gl;
        const program = this.programs.embedding;
        const seqLen = indices.length;

        // Create texture for embeddings (vocabSize x embedDim)
        const vocabSize = embeddingsFlat.length / embedDim;
        const embeddingTexture = this.createFloatTexture(embeddingsFlat, embedDim, vocabSize);

        // Create output texture (seqLen x embedDim)
        const outputTexture = this.createEmptyTexture(embedDim, seqLen);
        const framebuffer = gl.createFramebuffer();
        
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, outputTexture, 0);

        gl.useProgram(program);
        gl.viewport(0, 0, embedDim, seqLen);

        // Bind embedding texture
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, embeddingTexture);
        gl.uniform1i(gl.getUniformLocation(program, 'uEmbeddings'), 0);

        // Pass indices as uniform (for small sequences) or texture (for large)
        if (indices.length <= 256) {
            gl.uniform1iv(gl.getUniformLocation(program, 'uIndices'), indices);
            gl.uniform1i(gl.getUniformLocation(program, 'uSeqLen'), seqLen);
        } else {
            // For larger sequences, use texture
            const indicesTexture = this.createIntTexture(indices);
            gl.activeTexture(gl.TEXTURE1);
            gl.bindTexture(gl.TEXTURE_2D, indicesTexture);
            gl.uniform1i(gl.getUniformLocation(program, 'uIndicesTexture'), 1);
        }

        gl.uniform1i(gl.getUniformLocation(program, 'uEmbedDim'), embedDim);

        this.drawQuad();

        const result = this.readFloatTexture(outputTexture, embedDim, seqLen);

        // Cleanup
        gl.deleteTexture(embeddingTexture);
        gl.deleteTexture(outputTexture);
        gl.deleteFramebuffer(framebuffer);

        return result;
    }

    /**
     * Softmax with temperature and top-k filtering
     */
    async softmax(logits, temperature = 1.0, topK = null) {
        if (!this.isInitialized) {
            throw new Error('WebGL not initialized');
        }

        const gl = this.gl;
        const program = this.programs.softmax;
        const vocabSize = logits.length;

        // For top-k, we'd need to implement a sorting/selection algorithm
        // For now, implement basic softmax
        const logitTexture = this.createFloatTexture(logits, vocabSize, 1);
        const outputTexture = this.createEmptyTexture(vocabSize, 1);
        const framebuffer = gl.createFramebuffer();
        
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, outputTexture, 0);

        gl.useProgram(program);
        gl.viewport(0, 0, vocabSize, 1);

        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, logitTexture);
        gl.uniform1i(gl.getUniformLocation(program, 'uLogits'), 0);
        gl.uniform1f(gl.getUniformLocation(program, 'uTemperature'), temperature);
        gl.uniform1i(gl.getUniformLocation(program, 'uVocabSize'), vocabSize);

        this.drawQuad();

        const result = this.readFloatTexture(outputTexture, vocabSize, 1);

        // Cleanup
        gl.deleteTexture(logitTexture);
        gl.deleteTexture(outputTexture);
        gl.deleteFramebuffer(framebuffer);

        return result;
    }

    /**
     * Create a float texture from array data
     */
    createFloatTexture(data, width, height) {
        const gl = this.gl;
        const texture = gl.createTexture();
        
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, width, height, 0, gl.RED, gl.FLOAT, new Float32Array(data));
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        
        return texture;
    }

    /**
     * Create empty texture for output
     */
    createEmptyTexture(width, height) {
        const gl = this.gl;
        const texture = gl.createTexture();
        
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, width, height, 0, gl.RED, gl.FLOAT, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        
        return texture;
    }

    /**
     * Read float data from texture
     */
    readFloatTexture(texture, width, height) {
        const gl = this.gl;
        const framebuffer = gl.createFramebuffer();
        
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
        
        const result = new Float32Array(width * height);
        gl.readPixels(0, 0, width, height, gl.RED, gl.FLOAT, result);
        
        gl.deleteFramebuffer(framebuffer);
        return Array.from(result);
    }

    /**
     * Create shader program from vertex and fragment shader source
     */
    createProgram(vertexSource, fragmentSource) {
        const gl = this.gl;
        
        const vertexShader = this.compileShader(gl.VERTEX_SHADER, vertexSource);
        const fragmentShader = this.compileShader(gl.FRAGMENT_SHADER, fragmentSource);
        
        const program = gl.createProgram();
        gl.attachShader(program, vertexShader);
        gl.attachShader(program, fragmentShader);
        gl.linkProgram(program);
        
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            throw new Error('Program link error: ' + gl.getProgramInfoLog(program));
        }
        
        return program;
    }

    /**
     * Compile individual shader
     */
    compileShader(type, source) {
        const gl = this.gl;
        const shader = gl.createShader(type);
        
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
            throw new Error('Shader compile error: ' + gl.getShaderInfoLog(shader));
        }
        
        return shader;
    }

    /**
     * Draw a full-screen quad
     */
    drawQuad() {
        const gl = this.gl;
        
        if (!this.quadBuffer) {
            const vertices = new Float32Array([
                -1, -1,  1, -1,  -1, 1,
                -1,  1,  1, -1,   1, 1
            ]);
            
            this.quadBuffer = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, this.quadBuffer);
            gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
        }
        
        gl.bindBuffer(gl.ARRAY_BUFFER, this.quadBuffer);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    // Shader sources
    get vertexShaderSource() {
        return `#version 300 es
        in vec2 position;
        out vec2 vTexCoord;
        
        void main() {
            gl_Position = vec4(position, 0.0, 1.0);
            vTexCoord = (position + 1.0) * 0.5;
        }`;
    }

    get matmulFragmentShader() {
        return `#version 300 es
        precision highp float;
        
        uniform sampler2D uMatrixA;
        uniform sampler2D uMatrixB;
        uniform int uM, uN, uP;
        
        in vec2 vTexCoord;
        out float fragColor;
        
        void main() {
            ivec2 coord = ivec2(gl_FragCoord.xy);
            int row = coord.y;
            int col = coord.x;
            
            float sum = 0.0;
            for (int k = 0; k < uN; k++) {
                float a = texelFetch(uMatrixA, ivec2(k, row), 0).r;
                float b = texelFetch(uMatrixB, ivec2(col, k), 0).r;
                sum += a * b;
            }
            
            fragColor = sum;
        }`;
    }

    get softmaxFragmentShader() {
        return `#version 300 es
        precision highp float;
        
        uniform sampler2D uLogits;
        uniform float uTemperature;
        uniform int uVocabSize;
        
        in vec2 vTexCoord;
        out float fragColor;
        
        void main() {
            int idx = int(gl_FragCoord.x);
            
            // Find maximum for numerical stability
            float maxVal = -1e30;
            for (int i = 0; i < uVocabSize; i++) {
                float val = texelFetch(uLogits, ivec2(i, 0), 0).r / uTemperature;
                maxVal = max(maxVal, val);
            }
            
            // Compute current exponential
            float currentLogit = texelFetch(uLogits, ivec2(idx, 0), 0).r / uTemperature;
            float currentExp = exp(currentLogit - maxVal);
            
            // Compute sum of exponentials
            float expSum = 0.0;
            for (int i = 0; i < uVocabSize; i++) {
                float val = texelFetch(uLogits, ivec2(i, 0), 0).r / uTemperature;
                expSum += exp(val - maxVal);
            }
            
            fragColor = currentExp / expSum;
        }`;
    }

    get embeddingFragmentShader() {
        return `#version 300 es
        precision highp float;
        
        uniform sampler2D uEmbeddings;
        uniform int uIndices[256];
        uniform int uSeqLen;
        uniform int uEmbedDim;
        
        in vec2 vTexCoord;
        out float fragColor;
        
        void main() {
            ivec2 coord = ivec2(gl_FragCoord.xy);
            int seqPos = coord.y;
            int embedPos = coord.x;
            
            if (seqPos >= uSeqLen) {
                fragColor = 0.0;
                return;
            }
            
            int tokenId = uIndices[seqPos];
            fragColor = texelFetch(uEmbeddings, ivec2(embedPos, tokenId), 0).r;
        }`;
    }

    get attentionFragmentShader() {
        return `#version 300 es
        precision highp float;
        
        // Simplified attention computation
        // Real implementation would be much more complex
        uniform sampler2D uQuery;
        uniform sampler2D uKey;
        uniform sampler2D uValue;
        uniform int uSeqLen;
        uniform int uHeadDim;
        
        in vec2 vTexCoord;
        out float fragColor;
        
        void main() {
            // Placeholder for attention computation
            fragColor = 0.0;
        }`;
    }

    dispose() {
        if (this.gl) {
            // Clean up WebGL resources
            for (const program of Object.values(this.programs)) {
                this.gl.deleteProgram(program);
            }
            
            if (this.quadBuffer) {
                this.gl.deleteBuffer(this.quadBuffer);
            }
        }
    }
}

// Global instance
let webglAccelerator = null;

// JavaScript functions called from C#
window.initializeWebGLAccelerator = async function() {
    try {
        webglAccelerator = new WebGLTransformerAccelerator();
        return await webglAccelerator.initialize();
    } catch (error) {
        console.error('Failed to initialize WebGL accelerator:', error);
        return false;
    }
};

window.webglMatrixMultiply = async function(aFlat, bFlat, m, n, p) {
    if (!webglAccelerator?.isInitialized) {
        throw new Error('WebGL accelerator not initialized');
    }
    return await webglAccelerator.matrixMultiply(aFlat, bFlat, m, n, p);
};

window.webglEmbeddingLookup = async function(embeddingsFlat, indices, embedDim) {
    if (!webglAccelerator?.isInitialized) {
        throw new Error('WebGL accelerator not initialized');
    }
    return await webglAccelerator.embeddingLookup(embeddingsFlat, indices, embedDim);
};

window.webglSoftmax = async function(logits, temperature, topK) {
    if (!webglAccelerator?.isInitialized) {
        throw new Error('WebGL accelerator not initialized');
    }
    return await webglAccelerator.softmax(logits, temperature, topK);
};

window.disposeWebGLAccelerator = function() {
    if (webglAccelerator) {
        webglAccelerator.dispose();
        webglAccelerator = null;
    }
};

// Progress reporting for UI
window.updateProgress = function(message) {
    console.log('??', message);
    // Could dispatch custom events for UI updates
    if (typeof updateProgressCallback === 'function') {
        updateProgressCallback(message);
    }
};

// Memory and performance monitoring
window.getAvailableMemory = function() {
    // Estimate available memory (not perfect but useful)
    if (performance.memory) {
        return performance.memory.jsHeapSizeLimit - performance.memory.usedJSHeapSize;
    }
    return 1024 * 1024 * 1024; // Default to 1GB estimate
};

window.getGPUInfo = function() {
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
    
    if (!gl) return { available: false };
    
    const debugInfo = gl.getExtension('WEBGL_debug_renderer_info');
    return {
        available: true,
        vendor: debugInfo ? gl.getParameter(debugInfo.UNMASKED_VENDOR_WEBGL) : 'Unknown',
        renderer: debugInfo ? gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) : 'Unknown',
        version: gl.getParameter(gl.VERSION),
        maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
        maxVertexTextures: gl.getParameter(gl.MAX_VERTEX_TEXTURE_IMAGE_UNITS),
        maxFragmentTextures: gl.getParameter(gl.MAX_TEXTURE_IMAGE_UNITS)
    };
};

console.log('?? WebGL Transformer Accelerator module loaded');
console.log('?? GPU Info:', window.getGPUInfo());