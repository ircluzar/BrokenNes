// DisplayName: PX
precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
void main(){
  vec2 uv=vec2(vTex.x,1.0-vTex.y);
  gl_FragColor=texture2D(uTex,uv);
}
