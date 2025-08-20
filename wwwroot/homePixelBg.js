// homePixelBg.js - lightweight pixel tile background for Home page
(function(){
  const API = {
    _interval: null,
    _container: null,
    _active: false,
    _tiles: new Set(),
  // Tunables (adjusted for x20 density request)
  _spawnMs: 90,
  _spawnPerTick: 2,
  _fadeMs: 3200,
  _tileSize: 10,
  _minClusterCells: 3,
  _maxClusterCells: 9,
  _maxActive: 300,
  // Depth (distance) range: 0 = near, 1 = far
  _depthMin: 0.0,
  _depthMax: 1.0,
  _speedNear: 4,    // much slower near layer
  _speedFar: 14,    // slower far layer
  _minSpeed: 1,     // absolute minimum so every tile moves a bit
  _rafId: null,
    _resizeHandler: null,
    start(containerId){
      try {
        if(this._active) return;
        const el = document.getElementById(containerId);
        if(!el){
          // Retry a few times in case called before render; capped attempts
          let attempts = 0;
          const retry = ()=>{
            if(this._active) return;
            const e2 = document.getElementById(containerId);
            if(e2){ this.start(containerId); return; }
            if(++attempts<15) setTimeout(retry, 120);
          };
          retry();
          return;
        }
        this._container = el;
        this._active = true;
        el.innerHTML='';
        this._tiles.clear();
        el.style.setProperty('--hp-tile-size', this._tileSize + 'px');
        const spawnOne = ()=>{
          if(!this._active || !this._container) return;
          const w = this._container.clientWidth;
            const h = this._container.clientHeight;
            if(w===0||h===0) return;
            const minC = this._minClusterCells;
            const maxC = this._maxClusterCells;
            const cellsW = Math.floor(Math.random()*(maxC-minC+1))+minC;
            const cellsH = Math.floor(Math.random()*(maxC-minC+1))+minC;
            const clusterW = cellsW * this._tileSize;
            const clusterH = cellsH * this._tileSize;
            if(clusterW > w || clusterH > h) return; // skip if container very small
            // Depth (z) -> influences size scale, vertical position range, speed
            const depth = (Math.random()*(this._depthMax - this._depthMin)) + this._depthMin; // 0..1 (0 near, 1 far)
            // Scale: far (1) should be smaller; near (0) larger.
            const scale = 0.55 + (1 - depth) * 0.45; // near ~1.0, far ~0.55
            const adjW = Math.max(1, Math.round(clusterW * scale));
            const adjH = Math.max(1, Math.round(clusterH * scale));
            const maxX = w - adjW;
            const maxY = h - adjH;
            const px = Math.floor(Math.random()* (Math.floor(maxX/this._tileSize)+1)) * this._tileSize;
            const py = Math.floor(Math.random()* (Math.floor(maxY/this._tileSize)+1)) * this._tileSize;
            const cluster = document.createElement('div');
            cluster.className = 'hp-tile hp-cluster';
            cluster.style.left = px + 'px';
            cluster.style.top = py + 'px';
            cluster.style.width = adjW + 'px';
            cluster.style.height = adjH + 'px';
            cluster.style.position = 'absolute';
            cluster.style.display = 'grid';
            const cellSize = Math.round(this._tileSize * scale);
            cluster.style.gridTemplateColumns = `repeat(${cellsW}, ${cellSize}px)`;
            cluster.style.gridTemplateRows = `repeat(${cellsH}, ${cellSize}px)`;
            cluster.style.opacity = '1';
            cluster.dataset.depth = depth.toFixed(3);
            // Speed interpolated between near & far
            const rawSpeed = this._speedNear + depth * (this._speedFar - this._speedNear);
            const speed = Math.max(this._minSpeed, rawSpeed);
            cluster.dataset.speed = speed.toFixed(4);
            const palette = ['#2b0000','#3a0000','#480000','#550000'];
            const colorOff = '#000';
            const totalCells = cellsW * cellsH;
            const liveChance = 0.5; // proportion of lit (red) cells
            let litCount = 0;
            for(let i=0;i<totalCells;i++){
              const cell = document.createElement('div');
              // Random decide lit or off; ensure at least one lit
              let lit = (Math.random() < liveChance);
              if(!lit && litCount===0 && i===totalCells-1) lit = true; // ensure >=1
              if(lit){
                litCount++;
                cell.style.background = palette[Math.floor(Math.random()*palette.length)];
              } else {
                cell.style.background = colorOff;
              }
              cell.style.width = cellSize+'px';
              cell.style.height = cellSize+'px';
              cluster.appendChild(cell);
            }
            this._container.appendChild(cluster);
            this._tiles.add(cluster);
            // Enforce cap
            if(this._tiles.size > this._maxActive){
              // Remove oldest (iterate first element of Set)
              const first = this._tiles.values().next().value;
              if(first){ this._tiles.delete(first); first.remove(); }
            }
            // Fade out on timer (opacity), movement handled in RAF
            requestAnimationFrame(()=>{ cluster.style.transition = `opacity ${this._fadeMs}ms linear`; cluster.style.opacity='0'; });
            setTimeout(()=>{ this._tiles.delete(cluster); cluster.remove(); }, this._fadeMs + 120);
        };
        const spawnBatch = ()=>{
          for(let i=0;i<this._spawnPerTick;i++) spawnOne();
        };
        this._interval = setInterval(spawnBatch, this._spawnMs);
        // Initial burst
        for(let i=0;i<20;i++) setTimeout(spawnBatch, i*30);
        // Start animation loop
        const step = (ts)=>{
          if(!this._active){ this._rafId=null; return; }
          if(!this._lastTs) this._lastTs = ts;
          const dt = (ts - this._lastTs)/1000; // seconds
          this._lastTs = ts;
          // Move each cluster left by speed*dt (farther = faster)
          const cullX = -100; // buffer
          this._tiles.forEach(el=>{
            const speed = parseFloat(el.dataset.speed)||0;
            // Maintain fractional position in data-fx (fall back to style if missing)
            let fx = el.dataset.fx ? parseFloat(el.dataset.fx) : parseFloat(el.style.left)||0;
            fx -= speed * dt; // px movement this frame
            el.dataset.fx = fx.toString();
            // Apply without rounding so very slow speeds are perceptible
            el.style.left = fx + 'px';
            if(fx + el.offsetWidth < cullX){ this._tiles.delete(el); el.remove(); }
          });
          this._rafId = requestAnimationFrame(step);
        };
        this._lastTs = null;
        this._rafId = requestAnimationFrame(step);
        // Handle resize (clears to avoid misalignment artifacts)
        this._resizeHandler = ()=>{ if(!this._active) return; this.clearTiles(); };
        window.addEventListener('resize', this._resizeHandler);
      } catch(e){ console.warn('homePixelBg start failed', e); }
    },
    clearTiles(){
      this._tiles.forEach(t=>t.remove());
      this._tiles.clear();
    },
    stop(){
      this._active = false;
      if(this._interval) clearInterval(this._interval);
      this._interval = null;
  if(this._rafId){ cancelAnimationFrame(this._rafId); this._rafId=null; }
      if(this._resizeHandler){ window.removeEventListener('resize', this._resizeHandler); this._resizeHandler=null; }
      this.clearTiles();
      this._container = null;
    }
  };
  window.homePixelBg = API;
  // Convenience for Blazor: window.homePixelBgEnsure = ()=> API.start('pixelBgHost');
  window.homePixelBgEnsure = ()=> API.start('pixelBgHost');
})();
