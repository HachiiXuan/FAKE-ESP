/* ============================================================================
 * FAKE ESP —— 渲染器
 *
 * 这是一个"假的外挂特效"：所有透视框和任何游戏都没有任何关系。
 *
 * 设计要点
 * --------
 * 1) 这里画的仍然是**真 3D**：世界坐标 + 相机 + 透视投影，转视角时视差完全正确。
 *    3D 感来自这套数学，而不是图形 API。投影之后用 Canvas2D 画线段，原因有两个：
 *      · 外挂画面本质就是一堆 2D 线段 + 方框，Canvas2D 足够而且更好控样式（发光/描边文字）；
 *      · 同一套投影数学能原样搬到 WPF 覆盖窗，两个宿主画面完全一致。
 * 2) 唯一"作弊"的地方：**不做任何遮挡剔除**。框会穿墙显示 —— 这正是透视外挂该有的样子。
 * 3) 相机姿态由 C# 桥接用真实鼠标原始增量驱动（和游戏同一套手感），所以只要
 *    FOV 和灵敏度两个滑块调对，运动耦合就足以骗过眼睛。
 * ========================================================================== */
'use strict';

(() => {
  // ---------------------------------------------------------------- 启动参数
  const params = new URLSearchParams(location.search);
  const MODE = (params.get('mode') || 'standalone').toLowerCase();
  const FORCE_BG = params.get('bg');
  // 左下角的调试信息默认不显示（会盖在画面/录像上），加 ?debug=1 才打开
  const WANT_STATUS = params.get('debug') === '1';

  // ---------------------------------------------------------------- 画布
  const sceneCv = document.getElementById('scene');
  const hudCv = document.getElementById('hud');
  const statusEl = document.getElementById('status');
  const sctx = sceneCv.getContext('2d');
  const hctx = hudCv.getContext('2d');

  let W = 1, H = 1, DPR = 1, FOCAL = 1;

  function resize() {
    DPR = Math.min(window.devicePixelRatio || 1, 2);
    W = Math.max(1, window.innerWidth);
    H = Math.max(1, window.innerHeight);
    for (const cv of [sceneCv, hudCv]) {
      cv.width = Math.max(1, Math.round(W * DPR));
      cv.height = Math.max(1, Math.round(H * DPR));
    }
    sctx.setTransform(DPR, 0, 0, DPR, 0, 0);
    hctx.setTransform(DPR, 0, 0, DPR, 0, 0);
    FOCAL = (W / 2) / Math.tan((90 * Math.PI / 180) / 2); // 先给个默认，每帧按 fov 重算
  }
  window.addEventListener('resize', resize);
  resize();

  // ---------------------------------------------------------------- 状态拉取
  let state = null;
  let connected = false;
  let fetchHz = 0;
  let frameFps = 0;
  let renderMs = 0;

  async function pump() {
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let count = 0, countAt = performance.now();
    for (;;) {
      const t0 = performance.now();
      try {
        // 同源请求，keep-alive，没有任何 CORS 问题
        const res = await fetch('state', { cache: 'no-store' });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        state = await res.json();
        connected = true;
      } catch (err) {
        connected = false;
        state = null;
      }

      const now = performance.now();
      if (now - countAt >= 1000) {
        fetchHz = count * 1000 / (now - countAt);
        count = 0;
        countAt = now;
      }
      count++;

      // 最多 ~200Hz，且永远不堆积请求
      await sleep(Math.max(0, 5 - (performance.now() - t0)));
    }
  }
  pump();

  // ---------------------------------------------------------------- 小工具
  const DEG = Math.PI / 180;
  const NEAR = 0.05;

  function round1(v) { return Math.round(v * 10) / 10; }

  function hexToRgb(hex) {
    let h = (hex || '#ffffff').replace('#', '');
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    const n = parseInt(h, 16);
    if (!isFinite(n)) return { r: 255, g: 255, b: 255 };
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
  }

  function rgba(c, a) { return 'rgba(' + c.r + ',' + c.g + ',' + c.b + ',' + a + ')'; }

  /** 屏幕像素对齐，1px 线才够锐利 */
  function snap(v) { return Math.round(v) + 0.5; }

  // ---------------------------------------------------------------- 相机
  function buildCamera(cam) {
    const yaw = cam.yaw * DEG;
    const pitch = cam.pitch * DEG;
    const cp = Math.cos(pitch), sp = Math.sin(pitch);
    const cy = Math.cos(yaw), sy = Math.sin(yaw);

    // forward：yaw=0 时朝向 +Z；yaw 增大 = 向右转
    const fx = sy * cp, fy = sp, fz = cy * cp;

    // right = normalize(cross(worldUp, forward))，worldUp=(0,1,0)
    let rx = fz, ry = 0, rz = -fx;
    const rl = Math.hypot(rx, ry, rz) || 1;
    rx /= rl; ry /= rl; rz /= rl;

    // up = cross(forward, right)
    const ux = fy * rz - fz * ry;
    const uy = fz * rx - fx * rz;
    const uz = fx * ry - fy * rx;

    return { px: cam.x, py: cam.y, pz: cam.z, fx, fy, fz, rx, ry, rz, ux, uy, uz };
  }

  /** 相机空间深度（正数表示在相机前方） */
  function depthOf(K, x, y, z) {
    return (x - K.px) * K.fx + (y - K.py) * K.fy + (z - K.pz) * K.fz;
  }

  /** 投影到屏幕；返回 {x,y,z}，z 为相机空间深度 */
  function project(K, x, y, z, focal, cx, cy) {
    const dx = x - K.px, dy = y - K.py, dz = z - K.pz;
    const zc = dx * K.fx + dy * K.fy + dz * K.fz;
    const inv = 1 / Math.max(zc, 1e-4);
    const xc = dx * K.rx + dy * K.ry + dz * K.rz;
    const yc = dx * K.ux + dy * K.uy + dz * K.uz;
    return { x: cx + xc * focal * inv, y: cy - yc * focal * inv, z: zc };
  }

  /** 近平面裁剪后的投影，返回 [x1,y1,x2,y2] 或 null（整段在相机后面） */
  function projectSegment(K, ax, ay, az, bx, by, bz, focal, cx, cy, out) {
    let za = depthOf(K, ax, ay, az);
    let zb = depthOf(K, bx, by, bz);
    if (za < NEAR && zb < NEAR) return false;

    if (za < NEAR) {
      const t = (NEAR - za) / (zb - za);
      ax += (bx - ax) * t; ay += (by - ay) * t; az += (bz - az) * t;
      za = NEAR;
    } else if (zb < NEAR) {
      const t = (NEAR - zb) / (za - zb);
      bx += (ax - bx) * t; by += (ay - by) * t; bz += (az - bz) * t;
      zb = NEAR;
    }

    const pa = project(K, ax, ay, az, focal, cx, cy);
    const pb = project(K, bx, by, bz, focal, cx, cy);
    out[0] = pa.x; out[1] = pa.y; out[2] = pb.x; out[3] = pb.y;
    return true;
  }

  const segTmp = new Float64Array(4);

  /** 把一段 3D 线塞进当前路径；返回是否画了 */
  function addSegment(ctx, K, ax, ay, az, bx, by, bz, focal, cx, cy) {
    if (!projectSegment(K, ax, ay, az, bx, by, bz, focal, cx, cy, segTmp)) return false;
    ctx.moveTo(segTmp[0], segTmp[1]);
    ctx.lineTo(segTmp[2], segTmp[3]);
    return true;
  }

  // ---------------------------------------------------------------- 主渲染
  function render() {
    const now = performance.now();

    if (!state || !state.boxes) {
      sctx.clearRect(0, 0, W, H);
      hctx.clearRect(0, 0, W, H);
      updateStatus(now, 0, 0);
      requestAnimationFrame(render);
      return;
    }

    const t0 = performance.now();
    const o = state.opts || {};
    const cam = state.cam || { x: 0, y: 1.7, z: 0, yaw: 0, pitch: 0, fov: 90 };

    // 背景模式（由设置面板切换）
    const bg = FORCE_BG || o.bg || 'transparent';
    applyBackground(bg);

    sctx.clearRect(0, 0, W, H);
    hctx.clearRect(0, 0, W, H);

    const color = hexToRgb(o.color || '#ffffff');
    const focal = (W / 2) / Math.tan(((o.fov || 90) * DEG) / 2);
    FOCAL = focal;
    const cx = W / 2, cy = H / 2;
    const K = buildCamera(cam);

    // 总开关（Ins 一键隐身）
    if (o.enabled === false) {
      updateStatus(now, 0, 0);
      requestAnimationFrame(render);
      return;
    }

    // ---- 世界几何 ----
    // 每个目标只投影一次：先把中心点和屏幕包围盒都算出来，HUD、连线、屏外箭头共用同一份结果
    const boxes = [];
    const minDist = o.minDist || 0;
    const maxDist = o.maxDist || 90;

    for (const b of state.boxes) {
      const dx = b.x - cam.x, dy = b.y - cam.y, dz = b.z - cam.z;
      const dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
      // 太近的直接剔除：避免一个大框糊在脸上（距离由设置界面调，0~100m）
      if (dist < minDist) continue;
      if (dist > maxDist * 1.25) continue;
      b.d = round1(dist);

      // 相机空间坐标（就算在背后也算，屏外箭头要用）
      const zc = dx * K.fx + dy * K.fy + dz * K.fz;
      const xc = dx * K.rx + dy * K.ry + dz * K.rz;
      const yc = dx * K.ux + dy * K.uy + dz * K.uz;

      // 目标中心点的屏幕坐标（在背后时无意义，置为 NaN）
      let sx = NaN, sy = NaN;
      if (zc > NEAR) {
        const inv = 1 / zc;
        sx = cx + xc * focal * inv;
        sy = cy - yc * focal * inv;
      }

      boxes.push({ b, dist, zc, xc, yc, sx, sy, sb: computeScreenBox(K, b, focal, cx, cy) });
    }
    boxes.sort((p, q) => q.dist - p.dist); // 远的先画

    if (o.grid) drawGrid(sctx, K, cam, focal, cx, cy, color, o);
    if (o.worldBoxes !== false) drawWorldBoxes(sctx, K, boxes, focal, cx, cy, color, o);

    // ---- HUD ----
    let onScreen = 0;
    for (const item of boxes) {
      if (!item.sb || !item.sb.visible) continue;
      drawHudBox(hctx, item, color, o);
      onScreen++;
    }

    if (o.lines) drawTracers(hctx, boxes, color);
    if (o.radar) drawRadar(hctx, K, cam, state.boxes, color, o);
    if (o.weapons) drawCenterReticle(hctx, color, o);
    if (o.offscreen) drawOffscreenArrows(hctx, boxes, cx, cy, color);

    // ---- 收尾 ----
    renderMs = renderMs * 0.85 + (performance.now() - t0) * 0.15;
    updateStatus(now, onScreen, boxes.length);

    requestAnimationFrame(render);
  }

  function applyBackground(bg) {
    let css = 'transparent';
    if (bg === 'green') css = '#00b140';
    else if (bg === 'magenta') css = '#ff00ff';
    else if (bg === 'dark') css = '#0d0f13';
    if (document.body.style.background !== css) document.body.style.background = css;
    document.documentElement.style.background = css === 'transparent' ? 'transparent' : css;
  }

  // ---------------------------------------------------------------- 地面网格
  function drawGrid(ctx, K, cam, focal, cx, cy, color, o) {
    const SP = 4;           // 网格间距（米）
    const R = 64;           // 绘制半径
    const CHUNK = 16;       // 每段长度，用来做距离分层渐隐
    const baseX = Math.round(cam.x / SP) * SP;
    const baseZ = Math.round(cam.z / SP) * SP;

    // 三层透明度，避免出现生硬的边界
    const bands = [
      { limit: 26, alpha: 0.20 },
      { limit: 46, alpha: 0.11 },
      { limit: 1e9, alpha: 0.05 }
    ];

    const paths = [[], [], []];

    const push = (ax, ay, az, bx, by, bz) => {
      if (!projectSegment(K, ax, ay, az, bx, by, bz, focal, cx, cy, segTmp)) return;
      const mx = (ax + bx) / 2 - cam.x;
      const mz = (az + bz) / 2 - cam.z;
      const d = Math.hypot(mx, mz);
      let band = 2;
      if (d < bands[0].limit) band = 0;
      else if (d < bands[1].limit) band = 1;
      paths[band].push(segTmp[0], segTmp[1], segTmp[2], segTmp[3]);
    };

    for (let x = baseX - R; x <= baseX + R; x += SP) {
      for (let z = baseZ - R; z < baseZ + R; z += CHUNK) push(x, 0, z, x, 0, z + CHUNK);
    }
    for (let z = baseZ - R; z <= baseZ + R; z += SP) {
      for (let x = baseX - R; x < baseX + R; x += CHUNK) push(x, 0, z, x + CHUNK, 0, z);
    }

    ctx.lineWidth = 1;
    for (let i = 0; i < bands.length; i++) {
      const arr = paths[i];
      if (!arr.length) continue;
      ctx.strokeStyle = rgba(color, bands[i].alpha);
      ctx.beginPath();
      for (let k = 0; k < arr.length; k += 4) {
        ctx.moveTo(arr[k], arr[k + 1]);
        ctx.lineTo(arr[k + 2], arr[k + 3]);
      }
      ctx.stroke();
    }
  }

  // ---------------------------------------------------------------- 3D 线框盒子
  function drawWorldBoxes(ctx, K, boxes, focal, cx, cy, color, o) {
    const e = o.glow || 0;

    for (const item of boxes) {
      const b = item.b;
      const hx = b.sx / 2, hy = b.sy / 2, hz = b.sz / 2;
      const x0 = b.x - hx, x1 = b.x + hx;
      const y0 = b.y - hy, y1 = b.y + hy;
      const z0 = b.z - hz, z1 = b.z + hz;
      const alpha = 0.55 * (b.a ?? 1);

      const corners = [
        [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0], // 近面（-Z 侧）
        [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]  // 远面
      ];
      const edges = [
        [0, 1], [1, 2], [2, 3], [3, 0],   // 近面
        [4, 5], [5, 6], [6, 7], [7, 4],   // 远面
        [0, 4], [1, 5], [2, 6], [3, 7]    // 连接
      ];

      ctx.strokeStyle = rgba(color, alpha);
      ctx.lineWidth = 1;
      if (e > 0) {
        ctx.shadowColor = rgba(color, 0.85);
        ctx.shadowBlur = 3 + e * 9;
      }

      ctx.beginPath();
      for (const [ia, ib] of edges) {
        const a = corners[ia], c = corners[ib];
        addSegment(ctx, K, a[0], a[1], a[2], c[0], c[1], c[2], focal, cx, cy);
      }
      ctx.stroke();

      ctx.shadowBlur = 0;

      // 落地参考线（默认关）：竖直投影 + 地面十字，让目标"站在地上"
      if (o.groundCross && y0 > 0.12) {
        ctx.strokeStyle = rgba(color, 0.22 * (b.a ?? 1));
        ctx.lineWidth = 1;
        ctx.beginPath();
        addSegment(ctx, K, b.x, y0, b.z, b.x, 0, b.z, focal, cx, cy);
        const r = Math.max(hx, hz) * 1.6;
        addSegment(ctx, K, b.x - r, 0.01, b.z, b.x + r, 0.01, b.z, focal, cx, cy);
        addSegment(ctx, K, b.x, 0.01, b.z - r, b.x, 0.01, b.z + r, focal, cx, cy);
        ctx.stroke();
      }
    }
  }

  // ---------------------------------------------------------------- HUD 透视框
  /**
   * 把一个 3D 盒子的 8 个角投到屏幕，得到屏幕包围盒。
   * 只用"在相机前方"的角：背后的角强行投影会除以接近 0 的深度，炸成全屏白框。
   * 返回 null 表示这个目标这次不该画（几乎贴着镜头，或者在相机正后方）。
   */
  function computeScreenBox(K, b, focal, cx, cy) {
    const hx = b.sx / 2, hy = b.sy / 2, hz = b.sz / 2;
    const corners = [
      [b.x - hx, b.y - hy, b.z - hz], [b.x + hx, b.y - hy, b.z - hz],
      [b.x + hx, b.y + hy, b.z - hz], [b.x - hx, b.y + hy, b.z - hz],
      [b.x - hx, b.y - hy, b.z + hz], [b.x + hx, b.y - hy, b.z + hz],
      [b.x + hx, b.y + hy, b.z + hz], [b.x - hx, b.y + hy, b.z + hz]
    ];

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    let front = 0;
    for (const c of corners) {
      const dx = c[0] - K.px, dy = c[1] - K.py, dz = c[2] - K.pz;
      const zc = dx * K.fx + dy * K.fy + dz * K.fz;
      if (zc <= NEAR) continue;
      front++;
      const inv = 1 / zc;
      const px = cx + (dx * K.rx + dy * K.ry + dz * K.rz) * focal * inv;
      const py = cy - (dx * K.ux + dy * K.uy + dz * K.uz) * focal * inv;
      if (px < minX) minX = px;
      if (py < minY) minY = py;
      if (px > maxX) maxX = px;
      if (py > maxY) maxY = py;
    }

    if (front < 2) return null;
    if (depthOf(K, b.x, b.y, b.z) < 0.4) return null;

    const pad = 10;
    const visible = maxX > -pad && minX < W + pad && maxY > -pad && minY < H + pad;
    return { left: minX, top: minY, right: maxX, bottom: maxY, visible };
  }

  function drawHudBox(ctx, item, color, o) {
    const b = item.b;
    const a = b.a ?? 1;
    const sb = item.sb;

    const left = snap(sb.left), top = snap(sb.top);
    const right = snap(sb.right), bottom = snap(sb.bottom);
    const bw = Math.max(2, right - left), bh = Math.max(2, bottom - top);
    const lw = Math.max(1, o.thickness || 2);

    // 1) 细底框
    ctx.strokeStyle = rgba(color, 0.5 * a);
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.rect(left, top, bw, bh);
    ctx.stroke();

    // 2) 四角括号（粗一点，这是"透视框"的灵魂）
    const cl = Math.max(6, Math.min(bw, bh) * 0.28);   // 括号臂长
    if (o.glow) {
      ctx.shadowColor = rgba(color, 0.9);
      ctx.shadowBlur = 4 + (o.glow || 0) * 10;
    }
    ctx.strokeStyle = rgba(color, 0.95 * a);
    ctx.lineWidth = lw;
    ctx.lineCap = 'butt';
    ctx.beginPath();
    // 左上
    ctx.moveTo(left, top + cl); ctx.lineTo(left, top); ctx.lineTo(left + cl, top);
    // 右上
    ctx.moveTo(right - cl, top); ctx.lineTo(right, top); ctx.lineTo(right, top + cl);
    // 右下
    ctx.moveTo(right, bottom - cl); ctx.lineTo(right, bottom); ctx.lineTo(right - cl, bottom);
    // 左下
    ctx.moveTo(left + cl, bottom); ctx.lineTo(left, bottom); ctx.lineTo(left, bottom - cl);
    ctx.stroke();
    ctx.shadowBlur = 0;

    // 3) 头顶小标记（默认关）：物资是菱形，其它是三角
    const headX = (left + right) / 2;
    const markY = top - 7;
    if (o.headMarker) {
      ctx.fillStyle = rgba(color, 0.9 * a);
      ctx.beginPath();
      if (b.k === 2) {
        ctx.moveTo(headX, markY - 3.2); ctx.lineTo(headX + 3.2, markY);
        ctx.lineTo(headX, markY + 3.2); ctx.lineTo(headX - 3.2, markY);
        ctx.closePath();
      } else {
        ctx.moveTo(headX - 3, markY + 2.6);
        ctx.lineTo(headX + 3, markY + 2.6);
        ctx.lineTo(headX, markY - 2.6);
        ctx.closePath();
      }
      ctx.fill();
    }

    // 4) 名字 / 距离：两个独立开关，都排在框上方
    if (o.names || o.dist) {
      ctx.textAlign = 'center';
      ctx.textBaseline = 'alphabetic';
      ctx.lineWidth = 3;
      ctx.strokeStyle = 'rgba(0,0,0,' + (0.72 * a) + ')';
      ctx.fillStyle = rgba(color, 0.97 * a);

      let ty = markY - 7;
      if (o.names) {
        ctx.font = '600 12px "Segoe UI", system-ui, -apple-system, sans-serif';
        const label = b.n || '';
        ctx.strokeText(label, headX, ty);
        ctx.fillText(label, headX, ty);
        ty -= 14;
      }

      if (o.dist) {
        ctx.font = '600 11px "Segoe UI", system-ui, -apple-system, sans-serif';
        const sub = item.dist.toFixed(0) + 'm';
        ctx.strokeText(sub, headX, ty);
        ctx.fillText(sub, headX, ty);
      }
    }

    // 5) 假血条：贴在框下沿
    if (o.health) {
      const trackY = bottom + 5;
      ctx.fillStyle = 'rgba(0,0,0,' + (0.45 * a) + ')';
      ctx.fillRect(left, trackY, bw, 3);
      ctx.fillStyle = rgba(color, 0.92 * a);
      ctx.fillRect(left, trackY, bw * Math.max(0, Math.min(1, b.hp ?? 1)), 3);
    }
  }

  // ---------------------------------------------------------------- 准星连线
  function drawTracers(ctx, boxes, color) {
    const cx = W / 2, cy = H / 2;
    const limit = Math.min(W, H) * 0.45;

    // 只连"离准星最近"的最多 2 个。
    // 如果连给所有目标，当地面上所有目标都差不多高时，这些线会在视觉上
    // 连成一条横贯全屏的长线，看起来像渲染 bug。
    const candidates = [];
    for (const item of boxes) {
      if (isNaN(item.sx)) continue;
      if (item.dist > 75) continue;
      if ((item.b.a ?? 1) < 0.35) continue;
      const off = Math.hypot(item.sx - cx, item.sy - cy);
      if (off < 18 || off > limit) continue;
      candidates.push({ item, off });
    }
    candidates.sort((p, q) => p.off - q.off);

    ctx.strokeStyle = rgba(color, 0.22);
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let i = 0; i < Math.min(2, candidates.length); i++) {
      const item = candidates[i].item;
      ctx.moveTo(cx, cy);
      ctx.lineTo(item.sx, item.sy);
    }
    ctx.stroke();
  }

  // ---------------------------------------------------------------- 屏外指示
  function drawOffscreenArrows(ctx, boxes, cx, cy, color) {
    const radius = Math.min(W, H) * 0.40;

    // 只给"屏幕上看不到"的目标画箭头，最多 6 个，按距离由近到远
    const off = boxes
      .filter((it) => !it.sb || !it.sb.visible)
      .sort((p, q) => p.dist - q.dist)
      .slice(0, 6);

    let labelled = 0;
    for (const item of off) {
      // 屏幕方向：相机空间里 x 向右、y 向上，屏幕 y 是反的
      let sx = item.xc / Math.max(item.dist, 1e-3);
      let sy = -item.yc / Math.max(item.dist, 1e-3);
      // 目标在背后：方向反过来，提示"你需要转身"
      if (item.zc <= NEAR) { sx = -sx; sy = -sy; }
      if (Math.abs(sx) < 1e-4 && Math.abs(sy) < 1e-4) sy = -1;

      const ang = Math.atan2(sy, sx);
      const ax = cx + Math.cos(ang) * radius;
      const ay = cy + Math.sin(ang) * radius;
      const a = item.b.a ?? 1;

      ctx.save();
      ctx.translate(ax, ay);
      ctx.rotate(ang);
      ctx.fillStyle = rgba(color, 0.42 * a);
      ctx.beginPath();
      ctx.moveTo(6, 0);
      ctx.lineTo(-4.5, -4);
      ctx.lineTo(-4.5, 4);
      ctx.closePath();
      ctx.fill();
      ctx.restore();

      // 只给最近的 3 个标距离，避免数字叠成一团
      if (labelled < 3) {
        labelled++;
        ctx.fillStyle = rgba(color, 0.45 * a);
        ctx.font = '600 10px "Segoe UI", system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(item.dist.toFixed(0), ax + Math.cos(ang) * 14, ay + Math.sin(ang) * 14);
      }
    }
  }

  // ---------------------------------------------------------------- 准星 / FOV 圈
  function drawCenterReticle(ctx, color, o) {
    const cx = W / 2, cy = H / 2;

    // 假装是"瞄准辅助"的那个圈
    const r = Math.min(W, H) * 0.18;
    ctx.strokeStyle = rgba(color, 0.10);
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();

    ctx.strokeStyle = rgba(color, 0.55);
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(snap(cx - 7), snap(cy)); ctx.lineTo(snap(cx - 2), snap(cy));
    ctx.moveTo(snap(cx + 2), snap(cy)); ctx.lineTo(snap(cx + 7), snap(cy));
    ctx.moveTo(snap(cx), snap(cy - 7)); ctx.lineTo(snap(cx), snap(cy - 2));
    ctx.moveTo(snap(cx), snap(cy + 2)); ctx.lineTo(snap(cx), snap(cy + 7));
    ctx.stroke();
  }

  // ---------------------------------------------------------------- 小雷达
  function drawRadar(ctx, K, cam, allBoxes, color, o) {
    const R = 62;
    const cx = W - R - 22;
    const cy = H - R - 22;
    const range = Math.max(20, Math.min(o.maxDist || 130, 130));
    const scale = R / range;
    const yaw = cam.yaw * DEG;

    ctx.save();
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(0,0,0,0.20)';
    ctx.fill();
    ctx.strokeStyle = rgba(color, 0.30);
    ctx.lineWidth = 1;
    ctx.stroke();

    // 十字 + 视野扇形
    ctx.beginPath();
    ctx.moveTo(cx - R, cy); ctx.lineTo(cx + R, cy);
    ctx.moveTo(cx, cy - R); ctx.lineTo(cx, cy + R);
    ctx.strokeStyle = rgba(color, 0.16);
    ctx.stroke();

    const halfFov = (o.fov || 90) * DEG / 2;
    ctx.beginPath();
    ctx.moveTo(cx, cy);
    ctx.arc(cx, cy, R, -Math.PI / 2 - halfFov, -Math.PI / 2 + halfFov);
    ctx.closePath();
    ctx.fillStyle = rgba(color, 0.07);
    ctx.fill();

    for (const b of allBoxes) {
      const dx = b.x - cam.x;
      const dz = b.z - cam.z;

      // 把世界方向投到"相机前向 / 相机右向"两个轴上，雷达上方就永远是镜头朝向
      const s = Math.sin(yaw), c = Math.cos(yaw);
      const u = dx * c - dz * s;   // 右向分量
      const v = dx * s + dz * c;   // 前向分量

      const px = cx + u * scale;
      const py = cy - v * scale;

      if (Math.hypot(px - cx, py - cy) > R - 3) continue;
      ctx.fillStyle = rgba(color, 0.8 * (b.a ?? 1));
      ctx.beginPath();
      ctx.arc(px, py, 2.2, 0, Math.PI * 2);
      ctx.fill();
    }

    ctx.restore();
  }

  // ---------------------------------------------------------------- 击杀播报
  // ---------------------------------------------------------------- 状态行
  // 默认完全不显示：屏幕左下角那几行蓝字只在 ?debug=1 时才画出来。
  // 留着它是因为排查问题时很有用，但平时它会盖在画面/录像上。
  function updateStatus(now, onScreen, total) {
    if (!WANT_STATUS) {
      if (statusEl.style.display !== 'none') statusEl.style.display = 'none';
      return;
    }

    statusEl.style.display = 'block';
    document.body.classList.add('show-status');

    const lines = [];
    if (MODE === 'standalone') {
      lines.push('这就是效果本身：移动鼠标 / 按 WASD，框会跟着你的视角走');
    }
    lines.push(connected ? 'FAKE ESP 渲染器 已连接' : 'FAKE ESP：连接不到本地桥接 (http://127.0.0.1:17777)');
    const st = state || {};
    lines.push('模式 ' + MODE + '   画布 ' + W + 'x' + H + '   DPR ' + DPR);
    lines.push('拉取 ' + fetchHz.toFixed(0) + 'Hz   模拟 ' + (st.simHz || 0).toFixed(0) + 'Hz   渲染 ' + frameFps.toFixed(0) + 'fps   ' + renderMs.toFixed(2) + 'ms');
    lines.push('相机 yaw ' + (st.cam ? st.cam.yaw.toFixed(1) : '-') + '  pitch ' + (st.cam ? st.cam.pitch.toFixed(1) : '-') + '  FOV ' + (st.opts ? st.opts.fov : '-'));
    lines.push('目标 ' + onScreen + ' 在屏 / ' + total + ' 总计');
    lines.push('热键  Ins 一键隐身 · Home 视角回中     其余开关在设置面板 /tune');
    statusEl.textContent = lines.join('\n');
  }

  // ---------------------------------------------------------------- 帧率统计
  let fpsCount = 0, fpsAt = performance.now();
  function countFrame() {
    fpsCount++;
    const now = performance.now();
    if (now - fpsAt >= 1000) {
      frameFps = fpsCount * 1000 / (now - fpsAt);
      fpsCount = 0;
      fpsAt = now;
    }
    requestAnimationFrame(countFrame);
  }
  requestAnimationFrame(countFrame);

  // ---------------------------------------------------------------- 启动
  requestAnimationFrame(render);

  // 出错时把状态行强制显示出来（平时它是隐藏的）
  window.addEventListener('error', (e) => {
    statusEl.style.display = 'block';
    statusEl.textContent = '渲染器异常: ' + (e.message || e.error) + '\n' + (e.filename || '') + ':' + (e.lineno || '');
  });
})();
