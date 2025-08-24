# WWWROOT asset refactor — link-fix checklist

Goal: All .js now in `wwwroot/lib/`, all .mp3 in `wwwroot/music/`, all .m4a in `wwwroot/sfx/`. Update every reference across app code, pages, JS, and deploy scripts (including precompressed assets) so nothing breaks at dev or publish time.

Use this as a worksheet. Check items as you complete them.

## 1) JavaScript references moved to /lib

- [x] Update all HTML/Razor script tags that referenced root-level files:
  - [x] `index.html` (if present under `wwwroot/`) and `flatpublish/index.html` — change:
    - `src="nesInterop.js"` → `src="lib/nesInterop.js"`
    - `src="homePixelBg.js"` → `src="lib/homePixelBg.js"`
    - `src="nesInput.js"` → `src="lib/nesInput.js"`
    - `src="shaders.js"` → `src="lib/shaders.js"`
    - `src="soundfont.js"` → `src="lib/soundfont.js"`
    - `src="mnesSf2.js"` → `src="lib/mnesSf2.js"`
    - Any other `*.js` now under `lib/`
- [x] Update module imports from C# (Blazor JS isolation):
  - [x] Anywhere we do `IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./nesInterop.js")` → `./lib/nesInterop.js` (common in `Pages/Nes.razor` or its code-behind).
  - [x] Any other module imports pointing to root `*.js` → `./lib/*.js`.
- [x] Update dynamic/runtime JS loads inside our own JS:
  - [x] In `wwwroot/lib/nesInterop.js`, change worklet path:
    - `ctx.audioWorklet.addModule('audio-worklet.js')` → `ctx.audioWorklet.addModule('lib/audio-worklet.js')` (path is relative to page origin, not the JS file).
  - [x] Any `fetch('shaders.js')` / `importScripts('shaders.js')` style loads → `lib/shaders.js`.
- [x] Update docs/examples that embed our JS:
  - [x] `docs/**`, `docs/projects/**`, `docs/workpad/**`, `flatpublish/test-sf2.html`: ensure all `<script src>` point to `lib/*.js`.
- [x] Service Worker special-case (see section 4). Do NOT blindly move SW to `lib` without handling scope.

## 2) Music (.mp3) moved to /music

- [x] Update hardcoded audio file paths in JS:
  - [x] In `wwwroot/lib/nesInterop.js` Title music default:
    - `created.src = 'TitleScreen.mp3'` → `created.src = 'music/TitleScreen.mp3'`.
- [x] Update Razor/HTML `<audio>` elements and any component state pointing to mp3s:
  - [x] `DeckBuilder.mp3` → `music/DeckBuilder.mp3`
  - [x] `Options.mp3` → `music/Options.mp3`
  - [x] Any other `.mp3` references in `Pages/**`, `Layout/**`, `Shared/**`, and `wwwroot/**`.
- [x] Update any preload or `link rel="preload"` entries in HTML that reference `.mp3`.
- [x] If any docs or sample pages link `*.mp3`, update to `music/*.mp3`.

## 3) Sound effects (.m4a) moved to /sfx

- [x] Update `<audio>` elements used for SFX to use the new path:
  - [x] `plates.m4a` → `sfx/plates.m4a`
  - [x] Any other `.m4a` referenced in `Pages/**`, `Layout/**`, `Shared/**`, and docs.
- [x] Ensure `nesInterop.connectElementToSfx(elementId)` still finds elements by id; only the element `src` path changes.
- [x] Update any preload entries that reference `.m4a`.

## 4) Service Worker and PWA manifest

Service Worker must reside at or above the scope it controls. Moving `sw.js` into `lib/` changes scope and likely breaks site-wide control.

Choose ONE approach:

- [x] Preferred: keep a tiny `wwwroot/sw.js` in the root that delegates to the new implementation in `lib/`:
  - [x] Root `sw.js` content uses `importScripts('/lib/sw.js');` (and nothing else) so scope remains `/`.
  - [x] Update `navigator.serviceWorker.register('/sw.js', { scope: '/' })` calls to still point to root `/sw.js`.
  - [x] Ensure `flatpublish/sw.js` is the delegating stub and `flatpublish/lib/sw.js` contains the real logic (and both have updated `.br`/`.gz` as needed).

OR

- [ ] Alternate: keep the full service worker at root (don't move it). Document the exception to the "move all .js to lib" rule.

Also:

- [x] If the SW pre-caches or references specific asset paths, update them to include `lib/`, `music/`, and `sfx/`.
- [x] If `manifest.webmanifest` preloads or references audio (rare), update paths there as well.

## 5) Build and deploy scripts

- [x] `build.sh`: update any copy/minify/compress steps that reference root-level `*.js`, `*.mp3`, `*.m4a` to the new locations. Example adjustments:
  - [x] Update rsync/cp include/exclude globs to include `wwwroot/lib/**`, `wwwroot/music/**`, `wwwroot/sfx/**`.
  - [x] Update any minification or bundling commands to process the new `lib` folder.
- [x] `staging.sh`: update packaging into `flatpublish/` to place files under `flatpublish/lib/`, `flatpublish/music/`, `flatpublish/sfx/` (and not duplicate at root).
- [x] Any precompression (brotli/gzip) steps:
  - [x] Generate `.br`/`.gz` for files in `flatpublish/lib/*.js`, `flatpublish/music/*.mp3`, `flatpublish/sfx/*.m4a`.
  - [x] Remove old compressed files at previous locations to avoid stale references (e.g., `flatpublish/*.js.br`/`*.js.gz`).
- [x] `env.sh`: update any path variables or export lines that still mention old locations.
- [x] If we publish assets via GitHub Pages or a CDN, update rewrite or cache rules to include the new subfolders.

## 6) C# and Blazor code references

- [x] Search the C# codebase (`.cs`, `.razor`, `.razor.cs`) for string literals referencing file names and adjust:
  - [x] `nesInterop.js` → `lib/nesInterop.js`
  - [x] `audio-worklet.js` → `lib/audio-worklet.js`
  - [x] `TitleScreen.mp3` → `music/TitleScreen.mp3`
  - [x] `DeckBuilder.mp3` → `music/DeckBuilder.mp3`
  - [x] `Options.mp3` → `music/Options.mp3`
  - [x] `*.m4a` → `sfx/*.m4a`
- [x] Verify any `IJSRuntime.InvokeAsync("eval"/"Function"/custom loader)` that builds URLs at runtime now points to the new subpaths.
- [x] If any path defaults are computed on the server (Blazor Server), ensure static file middleware serves `lib/`, `music/`, `sfx/` and that no old path assumptions remain.

## 7) JavaScript inside `wwwroot/lib/nesInterop.js`

Specific edits to make explicit in the code (based on current file contents):

- [x] Change default title music source:
  - [x] `created.src='TitleScreen.mp3'` → `created.src='music/TitleScreen.mp3'`.
- [x] Change worklet module load path:
  - [x] `ctx.audioWorklet.addModule('audio-worklet.js')` → `ctx.audioWorklet.addModule('lib/audio-worklet.js')`.
- [x] If `test-sf2.html` or other helper HTML is used at runtime and loads `<script src="mnesSf2.js">`, update to `lib/mnesSf2.js`.

## 8) Flatpublish content reconciliation

- [x] Ensure the published tree under `flatpublish/` mirrors the new structure:
  - [x] `flatpublish/lib/*.js`, `flatpublish/music/*.mp3`, `flatpublish/sfx/*.m4a` exist.
  - [x] Remove old `flatpublish/*.js` root files (keep a root `sw.js` stub if using the delegating approach).
  - [x] Update `flatpublish/index.html` `<script>` and `<audio>` paths.
  - [x] Ensure `.br` and `.gz` variants exist for all moved files as before.

## 9) Documentation and examples

- [x] Update `README.md` and `WebUse.md` to reference new paths and folder layout.
- [x] Update any snippets in `docs/**` that show users adding `<script src="...">` or `<audio src="...">`.

## 10) Verification pass

- [ ] Dev run: launch app, open console/network tab, confirm:
  - [ ] All `lib/*.js`, `music/*.mp3`, `sfx/*.m4a` load with 200s (no 404s).
  - [ ] AudioWorklet module loads from `lib/audio-worklet.js` without CORS errors.
  - [ ] Title screen music plays (music bus volume applies) and fades as expected.
  - [ ] SFX elements still route through `connectElementToSfx` and respect SFX/master volume.
  - [ ] Shaders and any other dynamic JS fetches work from `lib/`.
  - [ ] Service worker installs/activates and serves new asset paths.
- [ ] Publish build: run the Release/publish flow and open `flatpublish/index.html` via a local static server; verify all assets are loading and compressed variants are found when enabled.

## 11) Suggested repo-wide search patterns

Run these searches and update results accordingly:

- [x] `src=\"([^\"]+\.js)\"` — adjust to `lib/…`
- [x] `import\(\s*['\"][^'\"]*\.js['\"]\s*\)` — adjust to `./lib/…`
- [x] `audio-worklet.js` — adjust to `lib/audio-worklet.js`
- [x] `TitleScreen\.mp3|DeckBuilder\.mp3|Options\.mp3` — adjust to `music/…`
- [x] `\.m4a` — adjust to `sfx/…`
- [x] `sw\.js` in code and HTML — evaluate SW scope handling as per section 4
- [x] Any references in `build.sh`, `staging.sh`, `env.sh`, and `flatpublish/**`

## 12) Commit plan

- [x] Commit code changes in small, reviewable chunks:
  1. App code path changes (C#/.razor and `nesInterop.js`).
  2. HTML/layout updates (`index.html`, `flatpublish/index.html`).
  3. Service worker scope solution (stub or exception), plus docs explaining the choice.
  4. Script updates (`build.sh`, `staging.sh`, `env.sh`) and precompression output refresh.
  5. Documentation updates.

Notes:
- The Service Worker is the only intentional exception candidate to the "move all .js to lib" rule due to scope requirements. Prefer the root stub that imports from `lib` to keep the policy consistent while preserving scope.
- AudioWorklet module URLs follow page origin scoping; ensure the path is correct from the page, not relative to the JS file location.
