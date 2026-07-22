/* tspi web viewer — playback-only, dependency-free WebGL1.
 *
 * Mirrors the Unity viewer's contract: this page never simulates anything, it
 * renders interpolated poses straight out of the .tspi file (see tspi.js).
 *
 * Render frame: right-handed, y-up.  NED -> render: x = East, y = -Down (up),
 * z = -North (so the default camera looks north, toward -Z).  Attitude follows
 * the NedUnity.cs approach: rotate the body axes through the quat into NED, map
 * them into render space, and rebuild an orthonormal basis — one code path, no
 * hand-derived quaternion basis change to get subtly wrong.
 */
(function () {
  'use strict';

  // ---------- small vector/matrix helpers (column-major mat4) ----------
  function v3(x, y, z) { return [x, y, z]; }
  function sub(a, b) { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }
  function add(a, b) { return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]; }
  function scale(a, s) { return [a[0] * s, a[1] * s, a[2] * s]; }
  function cross(a, b) {
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  }
  function len(a) { return Math.hypot(a[0], a[1], a[2]); }
  function norm(a) { var l = len(a); return l > 1e-12 ? scale(a, 1 / l) : [0, 0, 0]; }

  function matIdentity() {
    return new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
  }
  function matMul(a, b) {
    var o = new Float32Array(16);
    for (var c = 0; c < 4; c++)
      for (var r = 0; r < 4; r++)
        o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return o;
  }
  function matPerspective(fovY, aspect, near, far) {
    var f = 1 / Math.tan(fovY / 2), nf = 1 / (near - far);
    var o = new Float32Array(16);
    o[0] = f / aspect; o[5] = f; o[10] = (far + near) * nf; o[11] = -1;
    o[14] = 2 * far * near * nf;
    return o;
  }
  function matLookAt(eye, target, up) {
    var b = norm(sub(eye, target));           // back (+z of view space)
    var r = norm(cross(up, b));
    var u = cross(b, r);
    return new Float32Array([
      r[0], u[0], b[0], 0,
      r[1], u[1], b[1], 0,
      r[2], u[2], b[2], 0,
      -(r[0] * eye[0] + r[1] * eye[1] + r[2] * eye[2]),
      -(u[0] * eye[0] + u[1] * eye[1] + u[2] * eye[2]),
      -(b[0] * eye[0] + b[1] * eye[1] + b[2] * eye[2]), 1,
    ]);
  }
  function matBasis(r, u, back, t) {
    return new Float32Array([
      r[0], r[1], r[2], 0,
      u[0], u[1], u[2], 0,
      back[0], back[1], back[2], 0,
      t[0], t[1], t[2], 1,
    ]);
  }

  function nedToRender(n) { return [n[1], -n[2], -n[0]]; }

  // Rotate v by unit quaternion q (wxyz): v + 2w(q̂×v) + 2 q̂×(q̂×v).
  function quatRotate(q, v) {
    var t = scale(cross([q[1], q[2], q[3]], v), 2);
    return add(v, add(scale(t, q[0]), cross([q[1], q[2], q[3]], t)));
  }

  var TEAM_COLORS = {
    blue: [0.30, 0.60, 1.00],
    red: [1.00, 0.35, 0.30],
    white: [0.92, 0.92, 0.95],
  };
  function teamColor(team) { return TEAM_COLORS[team] || [0.62, 0.66, 0.72]; }

  // ---------- GL setup ----------
  var canvas = document.getElementById('gl');
  var gl = canvas.getContext('webgl', { antialias: true, alpha: false });
  if (!gl) { showError('WebGL unavailable in this browser'); return; }

  var VS = 'attribute vec3 aPos; attribute float aShade;' +
    'uniform mat4 uProj, uView, uModel; varying float vShade;' +
    'void main(){ vShade = aShade; gl_Position = uProj * (uView * (uModel * vec4(aPos, 1.0))); }';
  var FS = 'precision mediump float; uniform vec4 uColor; varying float vShade;' +
    'void main(){ gl_FragColor = vec4(uColor.rgb * vShade, uColor.a); }';

  function makeShader(type, src) {
    var s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS))
      throw new Error('shader: ' + gl.getShaderInfoLog(s));
    return s;
  }
  var prog = gl.createProgram();
  gl.attachShader(prog, makeShader(gl.VERTEX_SHADER, VS));
  gl.attachShader(prog, makeShader(gl.FRAGMENT_SHADER, FS));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS))
    throw new Error('link: ' + gl.getProgramInfoLog(prog));
  gl.useProgram(prog);
  var loc = {
    aPos: gl.getAttribLocation(prog, 'aPos'),
    aShade: gl.getAttribLocation(prog, 'aShade'),
    uProj: gl.getUniformLocation(prog, 'uProj'),
    uView: gl.getUniformLocation(prog, 'uView'),
    uModel: gl.getUniformLocation(prog, 'uModel'),
    uColor: gl.getUniformLocation(prog, 'uColor'),
  };
  gl.enableVertexAttribArray(loc.aPos);
  gl.enable(gl.DEPTH_TEST);
  gl.depthFunc(gl.LEQUAL);
  gl.enable(gl.BLEND);
  gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

  function staticBuffer(data) {
    var b = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, b);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    return b;
  }
  // Positions-only draw: aShade pinned to a constant, no second buffer needed.
  function drawPositions(buf, mode, first, count, color, model, shade) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);
    gl.disableVertexAttribArray(loc.aShade);
    gl.vertexAttrib1f(loc.aShade, shade === undefined ? 1 : shade);
    gl.uniformMatrix4fv(loc.uModel, false, model || IDENT);
    gl.uniform4fv(loc.uColor, color);
    gl.drawArrays(mode, first, count);
  }
  var IDENT = matIdentity();

  // ---------- marker geometry: a dart, nose along -Z, interleaved pos+shade ----------
  var MARKER = (function () {
    var nose = [0, 0, -1.5], tl = [-0.9, 0, 1.0], tr = [0.9, 0, 1.0];
    var top = [0, 0.45, 0.7], bot = [0, -0.2, 0.7];
    var faces = [
      [nose, tl, top, 0.95], [nose, top, tr, 1.0],
      [nose, bot, tl, 0.62], [nose, tr, bot, 0.70],
      [top, tl, tr, 0.82], [bot, tr, tl, 0.52],
    ];
    var v = [];
    faces.forEach(function (f) {
      for (var i = 0; i < 3; i++) v.push(f[i][0], f[i][1], f[i][2], f[3]);
    });
    return { buf: staticBuffer(new Float32Array(v)), count: faces.length * 3 };
  })();
  function drawMarker(model, color) {
    gl.bindBuffer(gl.ARRAY_BUFFER, MARKER.buf);
    gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 16, 0);
    gl.enableVertexAttribArray(loc.aShade);
    gl.vertexAttribPointer(loc.aShade, 1, gl.FLOAT, false, 16, 12);
    gl.uniformMatrix4fv(loc.uModel, false, model);
    gl.uniform4fv(loc.uColor, color);
    gl.drawArrays(gl.TRIANGLES, 0, MARKER.count);
  }

  var poleBuf = gl.createBuffer(); // per-entity altitude pole, rewritten each draw

  // ---------- scene (rebuilt per loaded file) ----------
  var scene = null;   // { file, span, views[], grid, axes, radius, center }

  function buildScene(file, name) {
    var views = [];
    var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    file.entities.forEach(function (e) {
      if (e.samples <= 0 || e.layout !== Tspi.LAYOUT_6DOF_V1) return;
      var step = Math.max(1, Math.ceil(e.samples / 50000));
      var n = Math.floor((e.samples - 1) / step) + 1;
      var pts = new Float32Array(n * 3);
      for (var i = 0; i < n; i++) {
        var r = file.readSample(e, i * step);
        var p = nedToRender(r.pos);
        pts[i * 3] = p[0]; pts[i * 3 + 1] = p[1]; pts[i * 3 + 2] = p[2];
        for (var k = 0; k < 3; k++) {
          if (p[k] < lo[k]) lo[k] = p[k];
          if (p[k] > hi[k]) hi[k] = p[k];
        }
      }
      views.push({
        e: e, step: step, nPts: n, buf: staticBuffer(pts),
        color: teamColor(e.team),
        scaleBase: e.type === 'munition' ? 0.45 : 1.0,
        row: null, alive: undefined,
      });
    });
    if (!views.length) throw new Error('no sampleable entities in file');

    var center = scale(add(lo, hi), 0.5);
    var radius = Math.max(len(sub(hi, center)), 100);

    // Ground grid on y=0 sized to the data, 1-2-5 spacing, ~10 cells per side.
    var ext = Math.max(Math.abs(lo[0]), Math.abs(hi[0]), Math.abs(lo[2]), Math.abs(hi[2]), 1000) * 1.25;
    var spacing = Math.pow(10, Math.floor(Math.log10(ext / 5)));
    if (ext / spacing > 10) spacing *= 2;
    if (ext / spacing > 10) spacing *= 2.5;
    ext = Math.ceil(ext / spacing) * spacing;
    var g = [];
    for (var x = -ext; x <= ext + 1e-6; x += spacing) g.push(x, 0, -ext, x, 0, ext);
    for (var z = -ext; z <= ext + 1e-6; z += spacing) g.push(-ext, 0, z, ext, 0, z);
    var grid = { buf: staticBuffer(new Float32Array(g)), count: g.length / 3, spacing: spacing };
    // North axis (accent) and east axis, through the NED origin.
    var axes = {
      n: staticBuffer(new Float32Array([0, 0, 0, 0, 0, -ext])),
      e: staticBuffer(new Float32Array([0, 0, 0, ext, 0, 0])),
    };

    var span = file.timeSpan();
    return {
      file: file, name: name, views: views, grid: grid, axes: axes,
      center: center, radius: radius, span: span,
      duration: Math.max(span.max - span.min, 1e-9),
    };
  }

  function disposeScene(s) {
    if (!s) return;
    s.views.forEach(function (v) { gl.deleteBuffer(v.buf); });
    gl.deleteBuffer(s.grid.buf);
    gl.deleteBuffer(s.axes.n);
    gl.deleteBuffer(s.axes.e);
  }

  // ---------- camera (orbit) ----------
  var cam = { target: [0, 0, 0], yaw: 0.6, pitch: 0.45, dist: 20000 };
  function camEye() {
    var cp = Math.cos(cam.pitch);
    return add(cam.target, scale(
      [Math.sin(cam.yaw) * cp, Math.sin(cam.pitch), Math.cos(cam.yaw) * cp], cam.dist));
  }
  function fitView() {
    if (!scene) return;
    cam.target = scene.center.slice();
    cam.dist = scene.radius * 2.4;
    cam.yaw = 0.6; cam.pitch = 0.45;
    followId = null;
    refreshEntityRows();
  }

  canvas.addEventListener('contextmenu', function (ev) { ev.preventDefault(); });
  var drag = null;
  canvas.addEventListener('pointerdown', function (ev) {
    canvas.setPointerCapture(ev.pointerId);
    drag = { x: ev.clientX, y: ev.clientY, pan: ev.button === 2 || ev.shiftKey };
  });
  canvas.addEventListener('pointermove', function (ev) {
    if (!drag) return;
    var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
    drag.x = ev.clientX; drag.y = ev.clientY;
    if (drag.pan) {
      // Translate the target in the camera's screen plane; panning breaks follow.
      var b = norm(sub(camEye(), cam.target));
      var r = norm(cross([0, 1, 0], b));
      var u = cross(b, r);
      var k = cam.dist * 0.0016;
      cam.target = add(cam.target, add(scale(r, -dx * k), scale(u, dy * k)));
      if (followId !== null) { followId = null; refreshEntityRows(); }
    } else {
      cam.yaw -= dx * 0.006;
      cam.pitch = Math.min(1.55, Math.max(-1.55, cam.pitch + dy * 0.006));
    }
  });
  canvas.addEventListener('pointerup', function () { drag = null; });
  canvas.addEventListener('wheel', function (ev) {
    ev.preventDefault();
    cam.dist *= Math.pow(1.0013, ev.deltaY);
    cam.dist = Math.min(Math.max(cam.dist, 5), 5e7);
  }, { passive: false });

  // ---------- playback state ----------
  var timeSec = 0, playing = false, speed = 1, loop = true;
  var followId = null, scrubbing = false;

  function seek(t) {
    if (!scene) return;
    timeSec = Math.min(Math.max(t, scene.span.min), scene.span.max);
  }
  function setPlaying(p) {
    playing = p;
    playBtn.innerHTML = p ? '&#10074;&#10074;' : '&#9654;';
  }

  // ---------- UI ----------
  var drop = document.getElementById('drop');
  var fileInput = document.getElementById('fileInput');
  var playBtn = document.getElementById('playBtn');
  var scrub = document.getElementById('scrub');
  var timeLbl = document.getElementById('timeLbl');

  // Looks up #err lazily: callable from the no-WebGL bail-out above, which runs
  // before this section's vars are assigned.
  function showError(msg) {
    var box = document.getElementById('err');
    box.textContent = msg;
    box.classList.remove('hidden');
    clearTimeout(showError._t);
    showError._t = setTimeout(function () { box.classList.add('hidden'); }, 8000);
  }

  function loadBuffer(buf, name, startAtSec) {
    var file = Tspi.parse(buf, name);
    disposeScene(scene);
    scene = buildScene(file, name);
    followId = null;
    if (startAtSec !== undefined) {
      seek(startAtSec);
      setPlaying(false);
    } else {
      timeSec = scene.span.min;
      setPlaying(true);
    }
    buildStaticUi();
    fitView();
    drop.classList.add('hidden');
    ['topbar', 'entities', 'eventsPanel', 'transport'].forEach(function (id) {
      document.getElementById(id).classList.remove('hidden');
    });
  }

  function loadFile(f) {
    f.arrayBuffer()
      .then(function (buf) { loadBuffer(buf, f.name); })
      .catch(function (e) { showError(String(e.message || e)); });
  }

  drop.addEventListener('click', function () { fileInput.click(); });
  document.getElementById('openBtn2').addEventListener('click', function () { fileInput.click(); });
  fileInput.addEventListener('change', function () {
    if (fileInput.files.length) loadFile(fileInput.files[0]);
    fileInput.value = '';
  });
  window.addEventListener('dragover', function (ev) {
    ev.preventDefault();
    drop.classList.add('dragover');
  });
  window.addEventListener('dragleave', function () { drop.classList.remove('dragover'); });
  window.addEventListener('drop', function (ev) {
    ev.preventDefault();
    drop.classList.remove('dragover');
    if (ev.dataTransfer.files.length) loadFile(ev.dataTransfer.files[0]);
  });

  playBtn.addEventListener('click', function () { setPlaying(!playing); });
  document.getElementById('speedSel').addEventListener('change', function (ev) {
    speed = parseFloat(ev.target.value);
  });
  document.getElementById('loopChk').addEventListener('change', function (ev) {
    loop = ev.target.checked;
  });
  document.getElementById('fitBtn').addEventListener('click', fitView);
  scrub.addEventListener('input', function () {
    if (!scene) return;
    seek(scene.span.min + parseFloat(scrub.value) * scene.duration);
  });
  scrub.addEventListener('pointerdown', function () { scrubbing = true; });
  window.addEventListener('pointerup', function () { scrubbing = false; });

  window.addEventListener('keydown', function (ev) {
    if (!scene || ev.target.tagName === 'INPUT' || ev.target.tagName === 'SELECT'
      || ev.target.tagName === 'TEXTAREA') return;
    var stepS = ev.shiftKey ? 10 : 1;
    if (ev.code === 'Space') { setPlaying(!playing); ev.preventDefault(); }
    else if (ev.code === 'ArrowLeft') seek(timeSec - stepS);
    else if (ev.code === 'ArrowRight') seek(timeSec + stepS);
    else if (ev.code === 'Home') seek(scene.span.min);
    else if (ev.code === 'End') seek(scene.span.max);
    else if (ev.code === 'KeyF') fitView();
  });

  function fmtT(t) { return t.toFixed(2) + 's'; }

  function buildStaticUi() {
    var f = scene.file;
    document.getElementById('fileName').textContent = scene.name;
    var dyn = '';
    for (var i = 0; i < f.provenance.length; i++)
      if (f.provenance[i].dynamics) dyn = f.provenance[i].dynamics;
    document.getElementById('fileMeta').textContent =
      f.entities.length + ' entities · dt ' + (f.dtSec * 1000).toFixed(1) + ' ms · origin ' +
      f.header.originLatDeg.toFixed(4) + '°, ' + f.header.originLonDeg.toFixed(4) + '°, ' +
      f.header.originAltM.toFixed(0) + ' m · ' + (dyn || 'unknown dynamics');

    var entPanel = document.getElementById('entities');
    entPanel.innerHTML = '';
    scene.views.forEach(function (v) {
      var row = document.createElement('div');
      row.className = 'row';
      var c = v.color;
      row.innerHTML = '<span class="chip" style="background:rgb(' +
        (c[0] * 255 | 0) + ',' + (c[1] * 255 | 0) + ',' + (c[2] * 255 | 0) + ')"></span>' +
        '<span>' + v.e.id + '</span><span class="sub">' + v.e.type + ' · ' + v.e.model + '</span>';
      row.title = 'click to follow · alive ' + fmtT(scene.file.startSec(v.e)) +
        ' – ' + fmtT(scene.file.endSec(v.e));
      row.addEventListener('click', function () {
        followId = followId === v.e.id ? null : v.e.id;
        refreshEntityRows();
      });
      entPanel.appendChild(row);
      v.row = row;
      v.alive = undefined;
    });

    var ordToId = {};
    f.entities.forEach(function (e) { ordToId[e.ord] = e.id; });
    var evPanel = document.getElementById('events');
    evPanel.innerHTML = '';
    var ticks = document.getElementById('ticks');
    ticks.innerHTML = '';
    f.events.forEach(function (ev) {
      var t = ev.t_ns / 1e9;
      var who = (ev.src != null ? (ordToId[ev.src] || ev.src) : '') +
        (ev.dst != null ? ' → ' + (ordToId[ev.dst] || ev.dst) : '');
      var extra = ev.data && ev.data.miss_m != null ? ' (miss ' + ev.data.miss_m.toFixed(1) + ' m)' : '';
      var row = document.createElement('div');
      row.className = 'ev';
      row.innerHTML = '<span class="t">' + fmtT(t) + '</span> <span class="kind">' +
        ev.kind + '</span> ' + who + extra;
      row.addEventListener('click', function () { seek(t); });
      evPanel.appendChild(row);
      var tick = document.createElement('div');
      tick.className = 'tick';
      tick.style.left = ((t - scene.span.min) / scene.duration * 100) + '%';
      ticks.appendChild(tick);
    });
    document.getElementById('eventsPanel').classList.toggle('hidden', f.events.length === 0);
  }

  function refreshEntityRows() {
    if (!scene) return;
    scene.views.forEach(function (v) {
      if (v.row) v.row.classList.toggle('following', followId === v.e.id);
    });
  }

  // ---------- render loop ----------
  function resize() {
    var dpr = window.devicePixelRatio || 1;
    var w = Math.round(canvas.clientWidth * dpr), h = Math.round(canvas.clientHeight * dpr);
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      gl.viewport(0, 0, w, h);
    }
  }

  var lastTs = null;
  function frame(ts) {
    requestAnimationFrame(frame);
    resize();
    gl.clearColor(0.051, 0.067, 0.09, 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    if (!scene) { lastTs = ts; return; }

    var dtWall = lastTs === null ? 0 : (ts - lastTs) / 1000;
    lastTs = ts;
    if (playing && !scrubbing) {
      timeSec += dtWall * speed;
      if (timeSec > scene.span.max)
        timeSec = loop ? scene.span.min + (timeSec - scene.span.max) % scene.duration
                       : (setPlaying(false), scene.span.max);
    }

    var f = scene.file;
    var eye = camEye();
    var aspect = canvas.width / Math.max(canvas.height, 1);
    var near = Math.max(0.5, cam.dist / 1000);
    var far = cam.dist * 20 + scene.radius * 10;
    gl.uniformMatrix4fv(loc.uProj, false, matPerspective(0.9, aspect, near, far));
    gl.uniformMatrix4fv(loc.uView, false, matLookAt(eye, cam.target, [0, 1, 0]));

    drawPositions(scene.grid.buf, gl.LINES, 0, scene.grid.count, [0.16, 0.21, 0.28, 1]);
    drawPositions(scene.axes.n, gl.LINES, 0, 2, [0.35, 0.55, 0.85, 1]);
    drawPositions(scene.axes.e, gl.LINES, 0, 2, [0.45, 0.42, 0.30, 1]);

    scene.views.forEach(function (view) {
      var e = view.e, c = view.color;
      // Full path, dim; flown-so-far portion, bright.
      drawPositions(view.buf, gl.LINE_STRIP, 0, view.nPts, [c[0], c[1], c[2], 0.22]);
      var flown = Math.floor((timeSec - f.startSec(e)) / f.dtSec / view.step) + 1;
      if (flown > 1)
        drawPositions(view.buf, gl.LINE_STRIP, 0, Math.min(flown, view.nPts), [c[0], c[1], c[2], 0.9]);

      var st = f.sampleAt(e, timeSec, false);
      var alive = st !== null;
      if (view.row && alive !== view.alive) {
        view.alive = alive;
        view.row.classList.toggle('dead', !alive);
      }
      if (!alive) return;

      var p = nedToRender(st.pos);
      if (followId === e.id) cam.target = p;

      // Altitude pole from the marker to the ground plane.
      gl.bindBuffer(gl.ARRAY_BUFFER, poleBuf);
      gl.bufferData(gl.ARRAY_BUFFER,
        new Float32Array([p[0], p[1], p[2], p[0], 0, p[2]]), gl.DYNAMIC_DRAW);
      drawPositions(poleBuf, gl.LINES, 0, 2, [c[0], c[1], c[2], 0.30]);

      // Body axes -> NED -> render; rebuild an orthonormal basis (NedUnity approach).
      var fwd = nedToRender(quatRotate(st.quat, [1, 0, 0]));
      var up = scale(nedToRender(quatRotate(st.quat, [0, 0, 1])), -1);
      var right = norm(cross(fwd, up));
      up = cross(right, fwd);
      var s = Math.min(Math.max(len(sub(p, eye)) * 0.014, 2), scene.radius * 0.2) * view.scaleBase;
      drawMarker(matBasis(scale(right, s), scale(up, s), scale(fwd, -s), p), [c[0], c[1], c[2], 1]);
    });

    if (!scrubbing) scrub.value = String((timeSec - scene.span.min) / scene.duration);
    var wall = new Date(f.header.epochUnixMs + timeSec * 1000);
    timeLbl.innerHTML = '<b>t=' + fmtT(timeSec) + '</b> / ' + fmtT(scene.span.max) +
      ' · ' + wall.toISOString().replace('T', ' ').replace('Z', 'Z');
  }
  requestAnimationFrame(frame);

  // ---------- served-mode scenario editor (tspi serve backend) ----------
  // The page stays a pure UI shell: the textarea holds the manifest JSON, the CLI
  // behind /api/run does all simulation, and the returned .tspi is reloaded at the
  // current playback time — determinism makes the resume seamless (Unity's
  // ScenarioEditController loop, browser-grade).
  var editorEl = document.getElementById('editor');
  var manifestTa = document.getElementById('manifestTa');
  var editStatus = document.getElementById('editStatus');

  function editorOpen(show) {
    editorEl.classList.toggle('hidden', !show);
    // The events panel shares the right edge; yield it to the editor.
    document.getElementById('eventsPanel').classList.toggle('hidden',
      show || !scene || scene.file.events.length === 0);
  }

  function setStatus(lines, cls) {
    editStatus.innerHTML = '';
    editStatus.className = cls || '';
    lines.forEach(function (l) {
      var d = document.createElement('div');
      d.textContent = l;
      editStatus.appendChild(d);
    });
  }

  function apiPost(url, body) {
    return fetch(url, { method: 'POST', body: body }).then(function (r) {
      return r.text().then(function (text) {
        var data;
        try { data = JSON.parse(text); }
        catch (e) { throw new Error('HTTP ' + r.status + ': ' + text.slice(0, 200)); }
        return { status: r.status, data: data };
      });
    });
  }

  function problemLines(d) {
    return (d.errors || [d.error || 'request failed']).map(function (e) { return 'error: ' + e; })
      .concat((d.warnings || []).map(function (w) { return 'warning: ' + w; }));
  }

  document.getElementById('validateBtn').addEventListener('click', function () {
    apiPost('/api/validate', manifestTa.value).then(function (res) {
      if (res.status !== 200 || !res.data.valid) { setStatus(problemLines(res.data), 'bad'); return; }
      var lines = ['valid ✓'].concat(res.data.warnings.map(function (w) { return 'warning: ' + w; }));
      setStatus(lines, 'ok');
    }).catch(function (e) { setStatus([String(e.message || e)], 'bad'); });
  });

  document.getElementById('runBtn').addEventListener('click', function () {
    var url = '/api/run';
    var seed = document.getElementById('seedInput').value.trim();
    if (seed) url += '?seed=' + encodeURIComponent(seed);
    setStatus(['running…']);
    apiPost(url, manifestTa.value).then(function (res) {
      var d = res.data;
      if (res.status !== 200) { setStatus(problemLines(d), 'bad'); return; }
      setStatus(['ran seed ' + d.seed + ' — ' + d.samples + ' samples, ' +
        d.elapsed_ms.toFixed(0) + ' ms'].concat(d.events.map(function (ev) {
          return 't=' + ev.t_s.toFixed(2) + 's ' + ev.kind +
            (ev.src ? ' ' + ev.src : '') + (ev.dst ? ' → ' + ev.dst : '') +
            (ev.miss_m != null ? ' (miss ' + ev.miss_m.toFixed(1) + ' m)' : '');
        })), 'ok');
      var resumeAt = document.getElementById('resumeChk').checked && scene ? timeSec : undefined;
      return fetch(d.file).then(function (r) {
        if (!r.ok) throw new Error('fetch ' + d.file + ': HTTP ' + r.status);
        return r.arrayBuffer();
      }).then(function (buf) {
        loadBuffer(buf, d.file.split('/').pop(), resumeAt);
        editorOpen(true); // loadBuffer unhides the events panel; re-yield it
      });
    }).catch(function (e) { setStatus([String(e.message || e)], 'bad'); });
  });

  document.getElementById('editBtn').addEventListener('click', function () {
    editorOpen(editorEl.classList.contains('hidden'));
  });
  document.getElementById('editorCloseBtn').addEventListener('click', function () { editorOpen(false); });
  document.getElementById('editOpenLink').addEventListener('click', function (ev) {
    ev.preventDefault();
    ev.stopPropagation(); // the drop screen underneath opens the file picker on click
    editorOpen(true);
  });

  // Deep links: ?file=<url> fetches a served .tspi (&t=<sec> opens paused there);
  // ?scenario=<url> preloads the editor. Needs http(s) — fetch is unavailable from
  // file://, where drag-drop still works.
  (function () {
    var params = new URLSearchParams(window.location.search);
    var url = params.get('file');
    if (!url) return;
    var t = params.has('t') ? parseFloat(params.get('t')) : undefined;
    fetch(url)
      .then(function (r) {
        if (!r.ok) throw new Error('fetch ' + url + ': HTTP ' + r.status);
        return r.arrayBuffer();
      })
      .then(function (buf) { loadBuffer(buf, url.split('/').pop(), t); })
      .catch(function (e) { showError(String(e.message || e)); });
  })();

  // Served mode: /api/version answering marks the edit-loop backend as present.
  (function () {
    if (location.protocol !== 'http:' && location.protocol !== 'https:') return;
    fetch('/api/version')
      .then(function (r) { if (!r.ok) throw new Error('no api'); return r.json(); })
      .then(function () {
        document.getElementById('editBtn').classList.remove('hidden');
        document.getElementById('serveHint').classList.remove('hidden');
        var url = new URLSearchParams(window.location.search).get('scenario');
        if (!url) return;
        return fetch(url).then(function (r) {
          if (!r.ok) throw new Error('fetch ' + url + ': HTTP ' + r.status);
          return r.text();
        }).then(function (text) {
          manifestTa.value = text;
          editorOpen(true);
        });
      })
      .catch(function (e) {
        // Not served (plain static hosting): the editor simply stays hidden.
        if (e && e.message && e.message !== 'no api') showError(String(e.message));
      });
  })();
})();
