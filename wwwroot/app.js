/* CodeGraph frontend: canvas force-directed graph (D3 v7). No build step. */
(() => {
  'use strict';

  if (typeof d3 === 'undefined') {
    const error = document.getElementById('error');
    error.textContent = 'The D3 graph library could not be loaded. The file wwwroot/lib/d3.min.js is missing ' +
      'from this copy of CodeGraph. Put D3 7.9.0 back in that place, then reload.';
    error.hidden = false;
    document.getElementById('empty').hidden = true;
    return;
  }

  // ------------------------------------------------------------------ vocabulary

  const LARGE_GRAPH = 3000;    // above this many nodes, methods start hidden
  const REGION_LIMIT = 3000;   // above this many visible nodes, community areas are not shaded
  const MAX_LABELS = 300;      // node labels drawn per frame
  const KIND_ORDER = ['project', 'namespace', 'class', 'interface', 'record', 'struct', 'enum', 'method'];

  /** How each node kind is drawn. Kinds not listed are plain circles. */
  const KIND_SHAPE = { project: 'square', namespace: 'diamond', interface: 'ring', method: 'dot' };
  const shapeOf = kind => KIND_SHAPE[kind] || 'circle';

  /**
   * Relationship types. `out` / `in` are the phrases used in the details pane
   * for an edge leaving / entering the selected node.
   */
  const EDGE_TYPES = [
    { id: 'inherits', label: 'inherits', color: '#f778ba', out: 'Inherits from', in: 'Inherited by',
      hint: 'A class derives from a base class' },
    { id: 'implements', label: 'implements', color: '#bc8cff', out: 'Implements', in: 'Implemented by',
      hint: 'A type implements an interface' },
    { id: 'calls', label: 'calls', color: '#58a6ff', out: 'Calls', in: 'Called by',
      hint: 'A method calls another method' },
    { id: 'references', label: 'references', color: '#7ee787', out: 'Uses', in: 'Used by',
      hint: 'Code uses a type, e.g. as a field, parameter or local' },
    { id: 'projectReference', label: 'project reference', color: '#f0883e', out: 'Depends on project', in: 'Project dependency of',
      hint: 'A project references another project' },
    { id: 'contains', label: 'contains', color: '#6e7681', out: 'Contains', in: 'Inside',
      hint: 'Structure: project → namespace → type → method' },
  ];
  const EDGE_BY_ID = Object.fromEntries(EDGE_TYPES.map(t => [t.id, t]));

  const PALETTE = ['#58a6ff', '#f78166', '#7ee787', '#d2a8ff', '#e3b341', '#56d4dd', '#ff7b9c', '#a5d6ff',
    '#ffa657', '#3fb950', '#bc8cff', '#f2cc60', '#39c5cf', '#ff9bce', '#79c0ff', '#db6d28'];
  const OTHER_COLOR = '#6e7681';
  const BACKGROUND = '#0d1117';
  const TEXT = '#e6edf3';
  const FONT_STACK = '-apple-system, "Segoe UI", Roboto, sans-serif';
  const NODE_FONT = `11px ${FONT_STACK}`;
  const COMMUNITY_FONT = `600 12px ${FONT_STACK}`;

  // ------------------------------------------------------------------ state + helpers

  const $ = id => document.getElementById(id);
  const canvas = $('graph');
  const ctx = canvas.getContext('2d');
  const stage = $('stage');

  const state = {
    graph: null,            // last API response
    nodes: [], links: [],   // all nodes / edges (links hold node objects)
    byId: new Map(),
    kinds: new Set(), edgeTypes: new Set(),
    hiddenCommunities: new Set(),
    minDegree: 0,
    visNodes: [], visLinks: [],   // what passes the filters
    groups: new Map(),            // community id → visible nodes
    labelOrder: [],               // visible nodes, most important label first
    selected: null,               // clicked node (pinned focus)
    hover: null,                  // node under the pointer
    focus: null,                  // selected || hover: the node whose connections are highlighted
    neighbours: null,             // Set of focus + its visible neighbours
    hoverCommunity: null,         // community id hovered in the side list
    transform: d3.zoomIdentity,
    width: 0, height: 0,
    request: null,                // { path, excludeTests } of the graph on screen
    fitPending: false,
  };

  /** Tiny DOM helper: el('li', {className: 'x'}, child, 'text') */
  function el(tag, props, ...children) {
    const node = Object.assign(document.createElement(tag), props || {});
    for (const child of children) if (child != null) node.append(child);
    return node;
  }

  /** The catch-all community: always last, and only grey if it is really named "other". */
  const isOther = id => {
    const c = state.graph.communities[id];
    return !c || (c.name === 'other' && id === state.graph.communities.length - 1);
  };

  const communityColor = id => {
    if (isOther(id)) return OTHER_COLOR;
    return id < PALETTE.length ? PALETTE[id] : `hsl(${(id * 137.508) % 360} 65% 62%)`;
  };

  const glyph = kind => el('span', { className: `glyph ${shapeOf(kind)}` });

  function colorDot(communityId) {
    const dot = el('span', { className: 'dot' });
    dot.style.background = communityColor(communityId);
    return dot;
  }

  const plural = (n, word) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;
  const shorten = (text, max) => text.length > max ? text.slice(0, max - 1) + '…' : text;

  function storageGet(key) { try { return localStorage.getItem(key); } catch { return null; } }
  function storageSet(key, value) { try { localStorage.setItem(key, value); } catch { /* storage unavailable */ } }

  // ------------------------------------------------------------------ analysis request

  $('analyze-form').addEventListener('submit', e => { e.preventDefault(); analyze(false); });
  $('reanalyze').addEventListener('click', () => analyze(true));

  async function analyze(refresh) {
    const path = refresh && state.request ? state.request.path : $('path').value.trim();
    const excludeTests = $('exclude-tests').checked;
    if (!path) { showError('Enter a folder path or a .sln path first.'); return; }

    setLoading(true, refresh ? 'Re-analyzing…' : 'Analyzing…');
    const poll = setInterval(async () => {
      try {
        const r = await fetch('/api/progress');
        const { message } = await r.json();
        if (message) $('loading-text').textContent = message;
      } catch { /* ignore */ }
    }, 600);

    try {
      const response = await fetch('/api/analyze', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path, excludeTests, refresh }),
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.error || `Request failed (${response.status})`);
      state.request = { path, excludeTests };
      storageSet('codegraph.path', path);
      loadGraph(body);
    } catch (err) {
      showError(err.message || String(err));
    } finally {
      clearInterval(poll);
      setLoading(false);
    }
  }

  function setLoading(on, text) {
    $('loading').hidden = !on;
    if (text) $('loading-text').textContent = text;
    $('analyze').disabled = on;
    $('reanalyze').disabled = on || !state.request;
    if (on) { $('error').hidden = true; $('empty').hidden = true; }
  }

  function showError(message) {
    $('error').textContent = message;
    $('error').hidden = false;
    $('empty').hidden = true;
  }

  // ------------------------------------------------------------------ graph loading

  function loadGraph(graph) {
    state.graph = graph;
    state.byId = new Map();
    state.selected = state.hover = state.focus = state.neighbours = state.hoverCommunity = null;
    state.hiddenCommunities = new Set();
    state.minDegree = 0;

    // Start each community in its own spot on a ring so the layout settles quickly.
    const communityCount = Math.max(1, graph.communities.length);
    const ring = 120 + Math.sqrt(graph.nodes.length) * 14;
    state.nodes = graph.nodes.map(n => {
      const angle = (n.community / communityCount) * 2 * Math.PI;
      const node = {
        ...n,
        x: Math.cos(angle) * ring + (Math.random() - 0.5) * 120,
        y: Math.sin(angle) * ring + (Math.random() - 0.5) * 120,
        out: [], in: [],
      };
      state.byId.set(n.id, node);
      return node;
    });
    state.links = graph.edges.map(e => {
      const link = { source: state.byId.get(e.source), target: state.byId.get(e.target), type: e.type, weight: e.weight };
      link.source.out.push(link);
      link.target.in.push(link);
      return link;
    });

    // Defaults: everything on; large graphs start without methods.
    const large = state.nodes.length > LARGE_GRAPH;
    state.kinds = new Set(state.nodes.map(n => n.kind));
    if (large) state.kinds.delete('method');
    state.edgeTypes = new Set(EDGE_TYPES.map(t => t.id));

    $('empty').hidden = true;
    $('error').hidden = true;
    $('search').disabled = false;
    $('search').value = '';
    $('min-degree').value = 0;
    $('min-degree-value').textContent = '0';

    buildKindFilters(large);
    buildEdgeFilters();
    buildCommunities();
    buildWarnings();
    renderDetails();
    ticks = 0;
    state.fitPending = true;
    applyFilters(true);
  }

  // ------------------------------------------------------------------ side panels

  function buildKindFilters(large) {
    const counts = d3.rollup(state.nodes, v => v.length, n => n.kind);
    const box = $('kind-filters');
    box.replaceChildren();
    for (const kind of KIND_ORDER.filter(k => counts.has(k))) {
      const input = el('input', { type: 'checkbox', checked: state.kinds.has(kind) });
      input.dataset.kind = kind;
      input.addEventListener('change', () => {
        input.checked ? state.kinds.add(kind) : state.kinds.delete(kind);
        applyFilters(true);
      });
      box.append(el('label', null, input, glyph(kind), kind,
        el('span', { className: 'count' }, counts.get(kind).toLocaleString())));
    }

    const note = $('large-note');
    note.hidden = !large;
    if (large) {
      note.replaceChildren(
        `Large graph (${state.nodes.length.toLocaleString()} nodes): methods are hidden by default. `,
        'Tick “method” below to include them.');
    }
  }

  function buildEdgeFilters() {
    const counts = d3.rollup(state.links, v => v.length, l => l.type);
    const box = $('edge-filters');
    box.replaceChildren();
    for (const type of EDGE_TYPES) {
      const input = el('input', { type: 'checkbox', checked: true });
      input.addEventListener('change', () => {
        input.checked ? state.edgeTypes.add(type.id) : state.edgeTypes.delete(type.id);
        applyFilters(false);
      });
      const swatch = el('span', { className: 'swatch' });
      swatch.style.background = type.color;
      box.append(el('label', { title: type.hint }, input, swatch, type.label,
        el('span', { className: 'count' }, (counts.get(type.id) || 0).toLocaleString())));
    }
  }

  function buildCommunities() {
    const list = $('communities');
    list.replaceChildren();
    $('community-count').textContent = state.graph.communities.length;
    for (const c of state.graph.communities) {
      const input = el('input', { type: 'checkbox', checked: true });
      input.dataset.community = c.id;
      input.addEventListener('change', () => {
        input.checked ? state.hiddenCommunities.delete(c.id) : state.hiddenCommunities.add(c.id);
        syncSelectAll();
        applyFilters(false);
      });
      const row = el('li', null, el('label', { title: c.name }, input, colorDot(c.id),
        el('span', { className: 'name' }, c.name),
        el('span', { className: 'count' }, c.size.toLocaleString())));
      // Pointing at a community in the list spotlights it in the graph.
      row.addEventListener('mouseenter', () => { state.hoverCommunity = c.id; draw(); });
      row.addEventListener('mouseleave', () => { state.hoverCommunity = null; draw(); });
      list.append(row);
    }
    syncSelectAll();
  }

  $('select-all').addEventListener('change', e => {
    const on = e.target.checked;
    state.hiddenCommunities = new Set(on ? [] : state.graph ? state.graph.communities.map(c => c.id) : []);
    for (const input of $('communities').querySelectorAll('input')) input.checked = on;
    applyFilters(false);
  });

  function syncSelectAll() {
    const total = state.graph.communities.length;
    const hidden = state.hiddenCommunities.size;
    $('select-all').checked = hidden === 0;
    $('select-all').indeterminate = hidden > 0 && hidden < total;
  }

  function buildWarnings() {
    const warnings = state.graph.warnings || [];
    $('warnings-section').hidden = warnings.length === 0;
    $('warnings-count').textContent = warnings.length;
    $('warnings').replaceChildren(...warnings.map(w => el('li', null, w)));
  }

  $('min-degree').addEventListener('input', e => {
    state.minDegree = +e.target.value;
    $('min-degree-value').textContent = state.minDegree;
    applyFilters(false);
  });

  // The legend remembers whether it was left open or closed.
  const legend = $('legend');
  if (storageGet('codegraph.legend') === 'closed') legend.open = false;
  legend.addEventListener('toggle', () => storageSet('codegraph.legend', legend.open ? 'open' : 'closed'));

  // ------------------------------------------------------------------ filtering

  function applyFilters(reheatHard) {
    if (!state.graph) return;

    const pass = new Set();
    for (const n of state.nodes) {
      n.deg = 0;
      if (state.kinds.has(n.kind) && !state.hiddenCommunities.has(n.community)) pass.add(n);
    }

    let links = state.links.filter(l => state.edgeTypes.has(l.type) && pass.has(l.source) && pass.has(l.target));
    for (const l of links) { l.source.deg++; l.target.deg++; }

    // Minimum connections is measured on what is currently shown.
    if (state.minDegree > 0) {
      for (const n of [...pass]) if (n.deg < state.minDegree) pass.delete(n);
      links = links.filter(l => pass.has(l.source) && pass.has(l.target));
    }

    state.visNodes = [...pass];
    state.visLinks = links;
    state.groups = d3.group(state.visNodes, n => n.community);
    for (const n of state.visNodes) {
      n.r = (n.kind === 'method' ? 2.2 : 3) + Math.sqrt(n.deg) * 1.6 + (n.kind === 'project' ? 3 : 0);
    }
    // Labels compete for space; the most connected nodes win.
    state.labelOrder = [...state.visNodes].sort((a, b) => b.deg - a.deg);

    const maxDeg = d3.max(state.nodes, n => n.deg) || 0;
    $('min-degree').max = Math.max(5, Math.min(50, maxDeg));

    if (state.hover && !pass.has(state.hover)) state.hover = null;
    if (state.selected && !pass.has(state.selected)) select(null);
    else updateFocus();

    updateStatus();
    restartSimulation(reheatHard ? 1 : 0.35);
  }

  function updateStatus() {
    const g = state.graph, s = g.stats;
    const parts = [
      `${state.visNodes.length.toLocaleString()} / ${state.nodes.length.toLocaleString()} nodes`,
      `${state.visLinks.length.toLocaleString()} / ${state.links.length.toLocaleString()} edges`,
      plural(s.projects, 'project') +
        (s.syntaxOnlyProjects ? ` (${s.syntaxOnlyProjects} syntax-only)` : '') +
        (s.skippedTestProjects ? `, ${plural(s.skippedTestProjects, 'test project')} ignored` : ''),
      g.fromCache ? 'cached' : `analyzed in ${(s.elapsedMs / 1000).toFixed(1)}s`,
    ];
    $('status').textContent = parts.join('  ·  ');
  }

  // ------------------------------------------------------------------ simulation

  let ticks = 0;
  const simulation = d3.forceSimulation()
    .force('link', d3.forceLink().distance(l => l.type === 'contains' ? 28 : 55).strength(l => l.type === 'contains' ? 0.5 : 0.15))
    .force('charge', d3.forceManyBody().strength(n => -40 - n.r * 6).theta(0.9).distanceMax(700))
    .force('collide', d3.forceCollide(n => n.r + 1.5).iterations(1))
    .force('x', d3.forceX().strength(0.02))
    .force('y', d3.forceY().strength(0.02))
    .force('cluster', clusterForce)
    .on('tick', onTick)
    .stop();

  /** Pulls nodes towards the centre of their community so communities form visible groups. */
  function clusterForce(alpha) {
    const k = 0.12 * alpha;
    for (const members of state.groups.values()) {
      const cx = d3.mean(members, n => n.x), cy = d3.mean(members, n => n.y);
      for (const n of members) {
        n.vx += (cx - n.x) * k;
        n.vy += (cy - n.y) * k;
      }
    }
  }

  function restartSimulation(alpha) {
    simulation.nodes(state.visNodes);
    simulation.force('link').links(state.visLinks);
    // Big graphs: cool down faster so the browser is not busy for long.
    simulation.alphaDecay(state.visNodes.length > 2000 ? 0.05 : 0.0228);
    simulation.alpha(Math.max(simulation.alpha(), alpha)).restart();
    draw();
  }

  function onTick() {
    ticks++;
    // Keep the whole graph in view while the layout settles (until the user zooms or pans).
    if (state.fitPending) {
      const settled = simulation.alpha() < 0.05 || ticks > 400;
      if (settled) { fit(0); state.fitPending = false; }
      else if (ticks % 5 === 1) fit(0);
    }
    draw();
  }

  // ------------------------------------------------------------------ drawing

  function resize() {
    state.width = stage.clientWidth;
    state.height = stage.clientHeight;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(state.width * dpr);
    canvas.height = Math.round(state.height * dpr);
    draw();
  }
  new ResizeObserver(resize).observe(stage);

  /**
   * One frame. Back to front: community areas, edges, nodes, rings, labels.
   * Everything except labels is drawn in graph ("world") coordinates.
   */
  function draw() {
    const dpr = window.devicePixelRatio || 1;
    const t = state.transform;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, state.width, state.height);
    if (!state.graph) return;

    ctx.translate(t.x, t.y);
    ctx.scale(t.k, t.k);

    // What is emphasised this frame: a node's neighbourhood, or else a community hovered in the list.
    const view = {
      k: t.k,
      focus: state.focus,
      near: state.neighbours,
      pinned: !!state.selected,                         // pinned focus fades the rest harder than a hover
      spotlight: state.focus ? null : state.hoverCommunity,
    };

    const regions = drawRegions(view);
    drawEdges(view);
    drawNodes(view);
    drawRings(view);

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);   // labels are laid out in screen pixels
    drawLabels(t, view, regions);
    ctx.globalAlpha = 1;
  }

  // Points around each node used to build a padded outline of a community.
  const HULL_DIRS = d3.range(10).map(i => [Math.cos(i * Math.PI / 5), Math.sin(i * Math.PI / 5)]);
  const HULL_PAD = 14;

  /**
   * Shades the area of each community and returns where its name should go:
   * [{ id, x, top }] in world coordinates. "other" is a grab bag, so it gets no area.
   */
  function drawRegions(view) {
    const regions = [];
    const shade = state.visNodes.length <= REGION_LIMIT;
    ctx.lineWidth = 1 / view.k;

    for (const [id, members] of state.groups) {
      if (isOther(id)) continue;
      const top = d3.min(members, n => n.y - n.r) - HULL_PAD;
      regions.push({ id, x: d3.mean(members, n => n.x), top });
      if (!shade) continue;

      const points = [];
      for (const n of members) {
        const reach = n.r + HULL_PAD;
        for (const [dx, dy] of HULL_DIRS) points.push([n.x + dx * reach, n.y + dy * reach]);
      }
      const hull = d3.polygonHull(points);
      ctx.beginPath();
      ctx.moveTo(hull[0][0], hull[0][1]);
      for (let i = 1; i < hull.length; i++) ctx.lineTo(hull[i][0], hull[i][1]);
      ctx.closePath();

      const strength = view.spotlight === id ? 2 : (view.focus || view.spotlight != null) ? 0.5 : 1;
      ctx.fillStyle = ctx.strokeStyle = communityColor(id);
      ctx.globalAlpha = 0.07 * strength;
      ctx.fill();
      ctx.globalAlpha = 0.3 * strength;
      ctx.stroke();
    }
    return regions;
  }

  /** Edges, batched per type. With a focus, unrelated edges fade and the focus's own edges get arrows. */
  function drawEdges(view) {
    const { focus, k } = view;
    const fade = focus ? (view.pinned ? 0.04 : 0.1) : view.spotlight != null ? 0.12 : null;

    ctx.lineWidth = 1 / k;
    for (const type of EDGE_TYPES) {
      if (!state.edgeTypes.has(type.id)) continue;
      ctx.beginPath();
      for (const l of state.visLinks) {
        if (l.type !== type.id) continue;
        if (focus && (l.source === focus || l.target === focus)) continue;
        ctx.moveTo(l.source.x, l.source.y);
        ctx.lineTo(l.target.x, l.target.y);
      }
      ctx.strokeStyle = type.color;
      ctx.globalAlpha = fade ?? (type.id === 'contains' ? 0.18 : 0.38);
      ctx.stroke();
    }

    if (!focus) return;
    ctx.globalAlpha = 0.95;
    ctx.lineWidth = 1.6 / k;
    for (const l of state.visLinks) {
      if (l.source !== focus && l.target !== focus) continue;
      ctx.strokeStyle = ctx.fillStyle = EDGE_BY_ID[l.type].color;
      ctx.beginPath();
      ctx.moveTo(l.source.x, l.source.y);
      ctx.lineTo(l.target.x, l.target.y);
      ctx.stroke();
      arrowHead(l, k);
    }
  }

  function arrowHead(l, k) {
    const dx = l.target.x - l.source.x, dy = l.target.y - l.source.y;
    const len = Math.hypot(dx, dy);
    if (len < 1) return;
    const ux = dx / len, uy = dy / len, size = 7 / k;
    const tipX = l.target.x - ux * (l.target.r + 1 / k), tipY = l.target.y - uy * (l.target.r + 1 / k);
    ctx.beginPath();
    ctx.moveTo(tipX, tipY);
    ctx.lineTo(tipX - ux * size - uy * size * 0.5, tipY - uy * size + ux * size * 0.5);
    ctx.lineTo(tipX - ux * size + uy * size * 0.5, tipY - uy * size - ux * size * 0.5);
    ctx.closePath();
    ctx.fill();
  }

  /** True when the node should be faded because something else is emphasised. */
  function isDimmed(n, view) {
    if (view.focus) return !view.near.has(n);
    return view.spotlight != null && n.community !== view.spotlight;
  }

  function drawNodes(view) {
    const fade = view.pinned ? 0.12 : 0.3;
    for (const n of state.visNodes) {
      ctx.globalAlpha = isDimmed(n, view) ? fade : 1;
      drawNode(n, communityColor(n.community), view.k);
    }
  }

  /** Shape tells the kind: square project, diamond namespace, hollow interface, small dot method. */
  function drawNode(n, color, k) {
    const { x, y, r } = n;
    ctx.fillStyle = color;
    ctx.beginPath();
    switch (shapeOf(n.kind)) {
      case 'square':
        ctx.rect(x - r, y - r, 2 * r, 2 * r);
        ctx.fill();
        ctx.lineWidth = 2 / k;
        ctx.strokeStyle = TEXT;
        ctx.stroke();
        break;
      case 'diamond': {
        const d = r * 1.3;
        ctx.moveTo(x, y - d); ctx.lineTo(x + d, y); ctx.lineTo(x, y + d); ctx.lineTo(x - d, y);
        ctx.closePath();
        ctx.fill();
        break;
      }
      case 'ring': {
        const width = Math.max(r * 0.42, 1.5 / k);
        ctx.arc(x, y, r - width / 2, 0, 2 * Math.PI);
        ctx.fillStyle = BACKGROUND;
        ctx.fill();
        ctx.lineWidth = width;
        ctx.strokeStyle = color;
        ctx.stroke();
        break;
      }
      default:
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.fill();
    }
  }

  /** Outline around the hovered and the selected node. */
  function drawRings(view) {
    ctx.globalAlpha = 1;
    for (const [n, color] of [[state.hover, TEXT], [state.selected, '#ffffff']]) {
      if (!n) continue;
      ctx.beginPath();
      ctx.arc(n.x, n.y, n.r * 1.3 + 3 / view.k, 0, 2 * Math.PI);
      ctx.lineWidth = 2 / view.k;
      ctx.strokeStyle = color;
      ctx.stroke();
    }
  }

  /**
   * Labels, in screen pixels so they stay the same size at any zoom. A label is only drawn
   * if it does not overlap one already placed; order decides who wins: community names,
   * then the selected / hovered node, then nodes by number of connections.
   */
  function drawLabels(t, view, regions) {
    const placed = [];
    const overlaps = (x0, y0, x1, y1) => placed.some(p => x0 < p[2] && x1 > p[0] && y0 < p[3] && y1 > p[1]);

    /** Text centred on sx with its top at sy. Returns false if there was no room. */
    const place = (text, width, sx, sy, height, color, alpha, force) => {
      const x0 = sx - width / 2 - 3, x1 = sx + width / 2 + 3, y1 = sy + height;
      if (x1 < 0 || x0 > state.width || y1 < 0 || sy > state.height) return false;
      if (!force && overlaps(x0, sy, x1, y1)) return false;
      placed.push([x0, sy, x1, y1]);
      ctx.globalAlpha = alpha;
      ctx.strokeText(text, sx, sy);
      ctx.fillStyle = color;
      ctx.fillText(text, sx, sy);
      return true;
    };

    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3;
    ctx.strokeStyle = BACKGROUND;

    // Community names sit just above their area.
    ctx.font = COMMUNITY_FONT;
    for (const region of regions) {
      const name = shorten(state.graph.communities[region.id].name, 44);
      const [sx, sy] = t.apply([region.x, region.top]);
      const quiet = view.focus || (view.spotlight != null && view.spotlight !== region.id);
      place(name, ctx.measureText(name).width, sx, sy - 17, 15, communityColor(region.id), quiet ? 0.45 : 1, false);
    }

    // Node names sit just below the node.
    ctx.font = NODE_FONT;
    const nodeLabel = (n, force) => {
      if (n.label === undefined) {
        n.label = shorten(n.name, 34);
        n.labelWidth = ctx.measureText(n.label).width;
      }
      const [sx, sy] = t.apply([n.x, n.y]);
      return place(n.label, n.labelWidth, sx, sy + n.r * 1.3 * t.k + 3, 13, TEXT, 0.95, force);
    };

    if (state.selected) nodeLabel(state.selected, true);
    if (state.hover && state.hover !== state.selected) nodeLabel(state.hover, true);

    const labelNeighbours = view.focus && view.near.size <= 80;
    let count = 0;
    for (const n of state.labelOrder) {
      if (count >= MAX_LABELS) break;
      if (n === state.selected || n === state.hover || isDimmed(n, view)) continue;
      const landmark = (n.kind === 'project' || n.kind === 'namespace') && t.k > 0.6;
      const bigEnough = n.r * t.k > 7;
      if (!(labelNeighbours || landmark || bigEnough)) continue;
      if (nodeLabel(n, false)) count++;
    }
  }

  // ------------------------------------------------------------------ zoom, drag, hover, click

  const zoom = d3.zoom()
    .scaleExtent([0.03, 10])
    .on('zoom', e => {
      if (e.sourceEvent) state.fitPending = false; // the user took over the camera
      state.transform = e.transform;
      draw();
    });

  const drag = d3.drag()
    .subject(e => {
      const [x, y] = state.transform.invert(d3.pointer(e.sourceEvent || e, canvas));
      return findNode(x, y) || undefined; // no node → let zoom pan instead
    })
    .on('start', e => {
      if (!e.active) simulation.alphaTarget(0.2).restart();
      e.subject.fx = e.subject.x;
      e.subject.fy = e.subject.y;
    })
    .on('drag', e => {
      const [x, y] = state.transform.invert(d3.pointer(e.sourceEvent, canvas));
      e.subject.fx = x;
      e.subject.fy = y;
    })
    .on('end', e => {
      if (!e.active) simulation.alphaTarget(0);
      e.subject.fx = e.subject.fy = null;
    });

  d3.select(canvas).call(drag).call(zoom).on('dblclick.zoom', null);

  function findNode(x, y) {
    const slack = 5 / state.transform.k;
    let best = null, bestDistance = Infinity;
    for (const n of state.visNodes) {
      const d = Math.hypot(n.x - x, n.y - y);
      if (d <= n.r + slack && d < bestDistance) { best = n; bestDistance = d; }
    }
    return best;
  }

  function setHover(node) {
    if (node === state.hover) return;
    state.hover = node;
    canvas.classList.toggle('pointing', !!node);
    updateFocus();
    draw();
  }

  canvas.addEventListener('mousemove', e => {
    if (!state.graph) return;
    const [px, py] = d3.pointer(e, canvas);
    const [x, y] = state.transform.invert([px, py]);
    const node = findNode(x, y);
    setHover(node);
    showTooltip(node, px, py);
  });
  canvas.addEventListener('mouseleave', () => { setHover(null); $('tooltip').hidden = true; });

  canvas.addEventListener('click', e => {
    if (!state.graph || e.defaultPrevented) return; // a drag suppresses its click
    const [x, y] = state.transform.invert(d3.pointer(e, canvas));
    select(findNode(x, y));
  });

  function location(n) {
    if (!n.filePath) return null;
    return n.line > 0 ? `${n.filePath}:${n.line}` : n.filePath;
  }

  function showTooltip(node, px, py) {
    const tip = $('tooltip');
    if (!node) { tip.hidden = true; return; }
    const community = state.graph.communities[node.community];
    const row = (label, value) => el('div', { className: 't-row' }, `${label}: `, el('b', null, value));
    tip.replaceChildren(
      el('div', { className: 't-name' }, node.name),
      row('kind', node.kind),
      row('project', node.project),
      community ? row('community', community.name) : null,
      location(node) ? row('file', location(node)) : null,
      row('connections', String(node.deg)));
    tip.hidden = false;
    const w = tip.offsetWidth, h = tip.offsetHeight;
    tip.style.left = Math.min(px + 14, state.width - w - 8) + 'px';
    tip.style.top = (py + 14 + h > state.height ? py - h - 10 : py + 14) + 'px';
  }

  // ------------------------------------------------------------------ selection + details

  function select(node) {
    state.selected = node || null;
    updateFocus();
    renderDetails();
    draw();
  }

  /** The focus is the selected node, or failing that the hovered one; its neighbours stay bright. */
  function updateFocus() {
    const focus = state.focus = state.selected || state.hover;
    if (!focus) { state.neighbours = null; return; }
    const near = new Set([focus]);
    for (const l of state.visLinks) {
      if (l.source === focus) near.add(l.target);
      else if (l.target === focus) near.add(l.source);
    }
    state.neighbours = near;
  }

  function renderDetails() {
    const box = $('details');
    const n = state.selected;
    if (!n) {
      box.replaceChildren(el('p', { className: 'dim' },
        'Hover a node to preview its connections. Click it to keep them highlighted and list them here.'));
      return;
    }
    const community = state.graph.communities[n.community];

    box.replaceChildren(
      el('h3', null, n.name),
      n.fullName && n.fullName !== n.name ? el('div', { className: 'full' }, n.fullName) : null,
      el('dl', null,
        el('dt', null, 'Kind'), el('dd', null, glyph(n.kind), n.kind),
        el('dt', null, 'Project'), el('dd', null, n.project),
        el('dt', null, 'File'), el('dd', null, location(n) || '—'),
        el('dt', null, 'Community'), el('dd', null, colorDot(n.community), community ? community.name : '—')),
      relationList('This uses', n.out, 'out', l => l.target),
      relationList('Used by', n.in, 'in', l => l.source));
    box.scrollTop = 0;
  }

  /**
   * One direction of a node's relationships, grouped by type under a plain-language
   * heading ("Calls", "Called by", …). `other` picks the node at the far end of a link.
   */
  function relationList(title, links, direction, other) {
    const LIMIT = 100; // per group
    const visible = new Set(state.visNodes);
    const section = el('div', { className: 'relations' }, el('h4', null, `${title} (${links.length})`));
    if (links.length === 0) section.append(el('p', { className: 'dim' }, 'nothing'));

    const byType = d3.group(links, l => l.type);
    for (const type of EDGE_TYPES) {
      const group = byType.get(type.id);
      if (!group) continue;
      group.sort((a, b) => other(a).name.localeCompare(other(b).name));

      const swatch = el('span', { className: 'swatch' });
      swatch.style.background = type.color;
      const list = el('ul');
      for (const l of group.slice(0, LIMIT)) {
        const o = other(l);
        const shown = visible.has(o);
        const item = el('li', {
          className: shown ? 'link' : 'hidden-node',
          title: `${o.fullName} (${o.kind})${shown ? '' : ' (hidden by the current filters)'}`,
        }, colorDot(o.community), el('span', { className: 'target' }, o.name),
          l.weight > 1 ? el('span', { className: 'times' }, `×${l.weight}`) : null);
        if (shown) item.addEventListener('click', () => { select(o); focusNode(o); });
        list.append(item);
      }
      if (group.length > LIMIT) list.append(el('li', { className: 'dim' }, `+ ${group.length - LIMIT} more`));
      section.append(el('h5', null, swatch, `${type[direction]} (${group.length})`), list);
    }
    return section;
  }

  // ------------------------------------------------------------------ camera

  function focusNode(n) {
    const k = Math.max(state.transform.k, 1.4);
    const target = d3.zoomIdentity.translate(state.width / 2, state.height / 2).scale(k).translate(-n.x, -n.y);
    d3.select(canvas).transition().duration(600).call(zoom.transform, target);
  }

  function fit(duration) {
    if (state.visNodes.length === 0) return;
    const [x0, x1] = d3.extent(state.visNodes, n => n.x);
    const [y0, y1] = d3.extent(state.visNodes, n => n.y);
    const pad = 110; // room for community areas and their names
    const k = Math.min(4, Math.min((state.width - pad) / Math.max(1, x1 - x0), (state.height - pad) / Math.max(1, y1 - y0)));
    const target = d3.zoomIdentity.translate(state.width / 2, state.height / 2).scale(k)
      .translate(-(x0 + x1) / 2, -(y0 + y1) / 2);
    if (duration) d3.select(canvas).transition().duration(duration).call(zoom.transform, target);
    else d3.select(canvas).call(zoom.transform, target);
  }

  $('fit').addEventListener('click', () => fit(500));
  $('relayout').addEventListener('click', () => {
    if (!state.graph) return;
    ticks = 0;
    state.fitPending = true;
    restartSimulation(1);
  });

  // ------------------------------------------------------------------ search

  const search = $('search'), results = $('search-results');
  let matches = [], active = 0;

  function runSearch() {
    const q = search.value.trim().toLowerCase();
    if (!q || !state.graph) { results.hidden = true; return; }
    const rank = n => {
      const name = n.name.toLowerCase();
      if (name === q) return 0;
      if (name.startsWith(q)) return 1;
      if (name.includes(q)) return 2;
      return (n.fullName || '').toLowerCase().includes(q) ? 3 : -1;
    };
    matches = state.visNodes
      .map(n => [rank(n), n]).filter(([r]) => r >= 0)
      .sort((a, b) => a[0] - b[0] || b[1].deg - a[1].deg || a[1].name.localeCompare(b[1].name))
      .slice(0, 15).map(([, n]) => n);
    active = 0;
    renderSearch();
  }

  function renderSearch() {
    results.replaceChildren();
    if (matches.length === 0) {
      results.append(el('li', { className: 'none' }, 'No visible node matches (check the filters).'));
    }
    matches.forEach((n, i) => {
      const item = el('li', { className: i === active ? 'active' : '', title: n.fullName },
        glyph(n.kind),
        el('span', { className: 'name' }, n.name),
        el('span', { className: 'kind' }, `${n.kind} · ${n.project}`));
      item.addEventListener('mousedown', e => { e.preventDefault(); choose(n); });
      results.append(item);
    });
    results.hidden = false;
  }

  function choose(n) {
    results.hidden = true;
    search.value = n.name;
    select(n);
    focusNode(n);
  }

  search.addEventListener('input', runSearch);
  search.addEventListener('focus', runSearch);
  search.addEventListener('blur', () => { results.hidden = true; });
  search.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (matches.length === 0) return;
      e.preventDefault();
      active = (active + (e.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length;
      renderSearch();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (results.hidden) runSearch();
      if (matches[active]) choose(matches[active]);
    } else if (e.key === 'Escape') {
      results.hidden = true;
    }
  });

  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && document.activeElement !== search) select(null);
  });

  // ------------------------------------------------------------------ start

  const lastPath = storageGet('codegraph.path');
  if (lastPath) $('path').value = lastPath;
  resize();
})();
