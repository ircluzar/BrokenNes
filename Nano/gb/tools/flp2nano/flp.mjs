// flp.mjs: the .flp parser moved to the music pipeline's shared reader (BrokenDev/tools/music/flp/flpsong.mjs, docs/projects/flp-music-pipeline.md).
// This file re-exports it unchanged so flp2nano (convert.mjs, flp2nano.mjs, test/flp2nano.test.mjs) keeps reading projects exactly as before.
export { SLIDE_FLAG, PARAMS, parseFlp, parseBn2State } from '../../../../BrokenDev/tools/music/flp/flpsong.mjs';
