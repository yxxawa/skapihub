'use strict';
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icon = name => '<svg class="icon" width="18" height="18" fill="none" stroke="currentColor" aria-hidden="true"><use href="#i-' + name + '"/></svg>';
const spinner = (label = '加载中') => '<span class="spinner" role="status" aria-label="' + esc(label) + '"></span>';
const number = value => new Intl.NumberFormat('en-US').format(value ?? 0);
const shortNumber = value => value >= 1000000 ? (value / 1000000).toFixed(1).replace(/\.0$/, '') + 'M' : value >= 10000 ? (value / 1000).toFixed(1).replace(/\.0$/, '') + 'k' : number(value);
const latency = (value, html = true) => value == null ? '暂无' : value >= 1000 ? (value / 1000).toFixed(2) + (html ? '<small>s</small>' : ' s') : Math.round(value) + (html ? '<small>ms</small>' : ' ms');
const tokenCount = value => value == null ? '<span title="上游未返回 Token 用量">未知</span>' : number(value);
const localDay = date => { const d = new Date(date); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); };
const state = { hub: null, usage: null, page: 'relays', period: 'all', revealed: false, expanded: new Set(), drawer: null, editing: null, syncing: new Set(), pendingWork: 0, ready: false, refreshed: null };
let toastTimer, refreshTask;
function native(action) { if (window.chrome?.webview) window.chrome.webview.postMessage({ action, ...(action === 'ready' ? { buildId: document.querySelector('meta[name="skapihub-build"]')?.content } : {}) }); else if (action === 'data-folder') toast('数据目录地址已显示在设置中'); else if (action === 'restart') toast('请重新启动桌面程序'); }
function toast(message, failed = false) { const el = $('#toast'); $('span', el).textContent = message; $('use', el).setAttribute('href', '#i-' + (failed ? 'close' : 'check')); el.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => el.hidden = true, failed ? 6000 : 3300); }
async function api(route, options = {}) {
  const response = await fetch('/_app' + route, { cache: 'no-store', ...options, headers: { 'Content-Type': 'application/json', ...options.headers } });
  const text = await response.text(); let data; try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) throw new Error(response.status === 403 ? '本地管理会话失效，请重新打开程序。' : typeof data?.error === 'string' ? data.error : data?.error?.message || '操作失败 · HTTP ' + response.status);
  return data;
}
const send = (route, method, body) => api(route, { method, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
function setButtonBusy(button, active) {
  if (!button) return;
  button.disabled = active; button.classList.toggle('is-loading', active);
  if (active) button.setAttribute('aria-busy', 'true'); else button.removeAttribute('aria-busy');
}
function startWork() {
  state.pendingWork++; $('#app-loading').hidden = false;
  let ended = false;
  return () => { if (ended) return; ended = true; state.pendingWork = Math.max(0, state.pendingWork - 1); $('#app-loading').hidden = state.pendingWork === 0; };
}
async function busy(button, work) {
  if (button?.disabled) return;
  setButtonBusy(button, true); const finish = startWork();
  try { return await work(); } catch (error) { toast(error.message, true); }
  finally { finish(); if (button?.isConnected) setButtonBusy(button, false); }
}
async function copy(value, message) { try { await navigator.clipboard.writeText(value); toast(message); } catch { toast('复制失败，请重新尝试', true); } }
function findRelay(id) { return state.hub?.relays.find(x => x.id === id); }
function findGroup() { const relay = findRelay(state.drawer?.relayId); return { relay, group: relay?.groups.find(x => x.id === state.drawer?.groupId) }; }
function groupPath() { return '/relays/' + state.drawer.relayId + '/groups/' + state.drawer.groupId; }
function toggle(enabled, label, extra) { return '<button class="switch" role="switch" aria-label="' + esc(label) + '" aria-checked="' + enabled + '" ' + extra + '><span></span></button>'; }

async function refresh(force = false) {
  if (refreshTask) { if (!force) return refreshTask; await refreshTask; }
  refreshTask = (async () => {
    try {
      const [hub, usage] = await Promise.all([api('/state'), api('/usage')]);
      const signature = JSON.stringify([hub, usage]);
      const changed = state.refreshed !== signature;
      state.refreshed = signature;
      state.hub = hub; state.usage = usage.snapshot;
      $('#connection-error').hidden = !hub.persistenceError && !usage.persistenceError;
      if (hub.persistenceError || usage.persistenceError) $('#connection-error-text').textContent = hub.persistenceError || usage.persistenceError;
      if (changed) render();
    } catch (error) {
      $('#connection-error').hidden = false; $('#connection-error-text').textContent = error.message;
      $('#service-label').textContent = '连接中断'; $('#service-dot').className = 'status-dot offline';
    }
  })();
  try { await refreshTask; } finally {
    refreshTask = null;
    if (!state.ready) {
      state.ready = true; document.body.classList.remove('booting');
      requestAnimationFrame(() => requestAnimationFrame(() => native('ready')));
    }
  }
}
function render() {
  const hub = state.hub; if (!hub) return;
  const groups = hub.relays.flatMap(x => x.groups);
  $('#nav-count').textContent = hub.relays.length;
  $('#relay-summary').textContent = hub.relays.length + ' 个中转 · ' + groups.length + ' 个分组 · ' + hub.relays.filter(r => r.enabled).flatMap(r => r.groups).filter(g => g.enabled).length + ' 个分组已启用';
  $('#relay-count').textContent = hub.relays.length;
  $('#endpoint-url').textContent = hub.url;
  $('#endpoint-key').textContent = state.revealed ? (hub.apiKey || '未设置') : '••••••••••••';
  $('.connection-key').classList.toggle('revealed', state.revealed);
  $('#reveal-key use').setAttribute('href', '#i-' + (state.revealed ? 'eye-off' : 'eye'));
  $('#reveal-key').setAttribute('aria-label', state.revealed ? '隐藏接入 Key' : '显示接入 Key');
  $('#service-label').textContent = hub.enabled ? '本地服务运行中' : '中转已暂停';
  $('#service-dot').className = 'status-dot' + (hub.enabled ? '' : ' paused');
  $('#service-toggle').setAttribute('aria-label', hub.enabled ? '暂停中转' : '开启中转');
  $('#service-toggle').title = hub.enabled ? '暂停中转' : '开启中转';
  $('#gateway-badge').className = 'live-badge' + (hub.enabled ? '' : ' paused');
  $('#gateway-badge').innerHTML = '<span class="status-dot' + (hub.enabled ? '' : ' paused') + '"></span>' + (hub.enabled ? '运行中' : '已暂停');
  $('#overview-input').textContent = shortNumber(hub.totals.inputTokens);
  $('#overview-input').title = number(hub.totals.inputTokens);
  $('#overview-output').textContent = shortNumber(hub.totals.outputTokens);
  $('#overview-output').title = number(hub.totals.outputTokens);
  $('#overview-note').textContent = hub.totals.unknownUsage ? hub.totals.unknownUsage + ' 次未返回用量' : '累计用量';
  $('#restart-button').hidden = !hub.restartRequired;
  renderRelays();
  if (state.drawer) renderDrawer();
  if (state.page === 'usage') renderUsage();
}
const emptyArt = '<svg class="empty-art" viewBox="0 0 220 116" aria-hidden="true"><path class="art-line" d="M45 29h28c23 0 12 29 37 29h20M45 87h28c23 0 12-29 37-29h20M165 58h32"/><rect class="art-box" x="14" y="13" width="43" height="33" rx="9"/><rect class="art-box" x="14" y="70" width="43" height="33" rx="9"/><rect class="art-core" x="126" y="34" width="48" height="48" rx="14"/><g fill="none" stroke="#b6a8d9" stroke-width="1.5" stroke-linecap="round"><path d="M26 24h19m-19 6h12m-12 51h19m-19 6h12M139 52h20m-20 7h12m-12 7h16"/></g><circle cx="202" cy="58" r="5" fill="#e8e1f6" stroke="#d8ceed"/><circle cx="80" cy="14" r="2" fill="#e6e1f0"/><circle cx="108" cy="100" r="2.5" fill="#e6e1f0"/><path d="M193 24v6m-3-3h6" stroke="#ded7eb" stroke-width="1.3"/></svg>';
function renderRelays() {
  if (!state.hub) return;
  $('#relay-list').setAttribute('aria-busy', 'false');
  const query = $('#relay-search').value.trim().toLowerCase();
  const relays = state.hub.relays.filter(relay => !query || [relay.name, relay.url, ...relay.groups.flatMap(g => [g.name, ...g.models.map(m => m.id)])].some(value => value.toLowerCase().includes(query)));
  if (!state.hub.relays.length) {
    $('#relay-list').innerHTML = '<div class="empty-state">' + emptyArt + '<h2>从第一个中转开始</h2><p>填入地址和 Key，模型会自动出现在这里</p><button class="button secondary" data-action="add">' + icon('plus') + '添加中转</button></div>'; return;
  }
  if (!relays.length) { $('#relay-list').innerHTML = '<div class="no-results">没有找到匹配的中转或模型</div>'; return; }
  $('#relay-list').innerHTML = '<div class="relay-table-head"><span>中转 / 分组</span><span>可用模型</span><span>状态</span></div>' + relays.map(relay => {
    const expanded = state.expanded.has(relay.id);
    const relaySyncing = state.syncing.has(relay.id) || relay.groups.some(group => state.syncing.has(relay.id + '/' + group.id));
    const models = new Set(relay.groups.filter(g => g.enabled).flatMap(g => g.models.filter(m => m.enabled).map(m => m.id)));
    return '<div class="relay-block' + (relay.enabled ? '' : ' dimmed') + '" data-relay="' + esc(relay.id) + '"><div class="relay-row"><div class="relay-main"><span class="relay-symbol">' + icon('hub') + '</span><div style="min-width:0"><div class="relay-name"><span>' + esc(relay.name) + '</span></div><div class="relay-url" title="' + esc(relay.url) + '">' + esc(relay.url.replace(/^https?:\/\//, '')) + '</div></div></div><div class="row-model-count">' + (relaySyncing ? spinner('正在获取模型') : models.size + '<small>个</small>') + '</div><div class="relay-actions">' + toggle(relay.enabled, '启用中转 ' + relay.name, 'data-action="toggle-relay"') + '<button class="icon-button" data-action="edit" aria-label="编辑 ' + esc(relay.name) + '" title="编辑中转">' + icon('edit') + '</button><button class="icon-button collapse-button' + (expanded ? ' expanded' : '') + '" data-action="expand" aria-label="' + (expanded ? '收起' : '展开') + '分组" aria-expanded="' + expanded + '">' + icon('chevron') + '</button></div></div>' + (expanded ? relay.groups.map(group => {
        const enabledModels = group.models.filter(m => m.enabled).length;
      const syncing = state.syncing.has(relay.id + '/' + group.id) || state.syncing.has(relay.id);
      return '<div class="group-row' + (group.enabled && relay.enabled ? '' : ' dimmed') + '" data-group="' + esc(group.id) + '"><div class="group-main">' + icon('key') + '<button class="group-name-button" data-action="open-group" aria-label="管理 ' + esc(group.name) + ' 的模型"><span>' + esc(group.name) + (group.syncError ? '<span class="tiny-warning" title="' + esc(group.syncError) + '"></span>' : '') + '</span><code>' + esc(group.keyPreview) + '</code></button></div><div class="row-model-count" title="已开启 ' + enabledModels + ' / 共 ' + group.models.length + ' 个模型">' + (syncing ? spinner('正在获取模型') : enabledModels + '<small> / ' + group.models.length + '</small>') + '</div><div class="relay-actions">' + toggle(group.enabled, '启用分组 ' + group.name, 'data-action="toggle-group"') + '<button class="icon-button" data-action="open-group" aria-label="打开 ' + esc(group.name) + ' 模型" title="管理模型">' + icon('chevron') + '</button></div></div>';
    }).join('') : '') + '</div>';
  }).join('');
}
function openDrawer(relayId, groupId) { state.drawer = { relayId, groupId }; $('#model-search').value = ''; $('#manual-model-form').hidden = true; $('#model-drawer').hidden = false; $('#drawer-backdrop').hidden = false; renderDrawer(); $('#close-drawer').focus(); }
function closeDrawer() { state.drawer = null; $('#model-drawer').hidden = true; $('#drawer-backdrop').hidden = true; }
function renderDrawer() {
  const { relay, group } = findGroup(); if (!group) { closeDrawer(); return; }
  const syncing = state.syncing.has(relay.id) || state.syncing.has(relay.id + '/' + group.id);
  setButtonBusy($('#drawer-sync'), syncing);
  $('#model-list').setAttribute('aria-busy', String(syncing));
  $('#drawer-relay').textContent = relay.name;
  $('#drawer-group').textContent = group.name;
  $('#drawer-key').textContent = group.keyPreview;
  $('#drawer-toggle').setAttribute('aria-checked', group.enabled);
  $('#drawer-warning').hidden = !group.syncError && relay.enabled;
  $('#drawer-warning').textContent = group.syncError || (!relay.enabled ? '中转已关闭，开启后此分组才能接收请求。' : '');
  $('#drawer-model-count').textContent = group.models.length;
  $('#model-enabled-count').textContent = '已开启 ' + group.models.filter(m => m.enabled).length + ' / ' + group.models.length;
  $('#sync-time').textContent = group.syncedAt ? '同步于 ' + new Date(group.syncedAt).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }) : '尚未同步';
  const query = $('#model-search').value.trim().toLowerCase();
  const models = group.models.filter(m => m.id.toLowerCase().includes(query));
  $('#model-list').innerHTML = syncing && !group.models.length ? '<div class="model-loading" role="status" aria-label="正在获取模型">' + Array.from({ length: 5 }, () => '<div class="model-skeleton"><span class="skeleton"></span><span class="skeleton"></span></div>').join('') + '</div>' : models.length ? models.map(model => '<div class="model-item' + (model.enabled ? '' : ' off') + '" data-model="' + esc(model.id) + '">' + icon('model') + '<span title="' + esc(model.id) + '">' + esc(model.id) + '</span><button class="icon-button model-delete" data-model-action="delete" aria-label="移除模型 ' + esc(model.id) + '" title="移除模型">' + icon('trash') + '</button>' + toggle(model.enabled, '启用模型 ' + model.id, 'data-model-action="toggle"') + '</div>').join('') : '<div class="no-results">' + (query ? '没有找到匹配的模型' : group.syncError ? '可刷新重试，或手动添加模型' : '刷新以获取模型，也可以手动添加') + '</div>';
}
async function syncRelay(relayId, groupId) {
  const id = relayId + (groupId ? '/' + groupId : ''); if (state.syncing.has(id)) return;
  state.syncing.add(id); const finish = startWork(); renderRelays(); if (state.drawer) renderDrawer();
  try {
    await send('/relays/' + relayId + (groupId ? '/groups/' + groupId : '') + '/sync', 'POST');
    await refresh(true);
    const groups = findRelay(relayId)?.groups.filter(g => !groupId || groupId === g.id) || [];
    const failed = groups.filter(g => g.syncError);
    toast(failed.length ? failed.length + ' 个分组未能获取模型，可进入分组查看' : '模型已更新', failed.length > 0);
  } finally { state.syncing.delete(id); finish(); renderRelays(); if (state.drawer) renderDrawer(); }
}
function showPage(page) {
  state.page = page;
  $$('.page').forEach(el => el.hidden = el.id !== 'page-' + page);
  $$('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  $('#main').scrollTop = 0; closeDrawer();
  if (page === 'usage') renderUsage();
  if (page === 'settings' && state.hub) {
    $('#setting-key').value = state.hub.apiKey; $('#setting-port').value = state.hub.port;
    $('#data-path').textContent = state.hub.dataDirectory; $('#settings-feedback').textContent = state.hub.restartRequired ? '端口将在重新启动后生效' : '';
  }
}
function selectedTotals() {
  if (!state.usage) return {};
  if (state.period === 'all') return state.usage.total;
  const totals = { inputTokens: 0, outputTokens: 0, totalTokens: 0, unknownUsage: 0, requests: 0, firstTokenSumMs: 0, firstTokenSamples: 0 };
  const days = state.period === 'today' ? 1 : 7;
  const since = new Date(); since.setDate(since.getDate() - days + 1);
  for (const [date, value] of Object.entries(state.usage.days)) if (date >= localDay(since)) for (const key of Object.keys(totals)) totals[key] += value[key] || 0;
  totals.averageFirstTokenMs = totals.firstTokenSamples ? totals.firstTokenSumMs / totals.firstTokenSamples : null;
  return totals;
}
function renderUsage() {
  if (!state.usage) return;
  const totals = selectedTotals();
  $('#usage-total').textContent = number(totals.totalTokens);
  $('#usage-input').textContent = number(totals.inputTokens);
  $('#usage-token-detail').textContent = '合计 ' + number(totals.totalTokens) + ' Token';
  $('#usage-output').textContent = number(totals.outputTokens);
  $('#usage-unknown').textContent = totals.unknownUsage ? totals.unknownUsage + ' 次未返回 Token 用量' : '仅记录上游实际用量';
  $$('#usage-period button').forEach(el => el.classList.toggle('selected', el.dataset.period === state.period));
  const since = new Date(); since.setDate(since.getDate() - (state.period === 'today' ? 0 : 6));
  const recent = state.usage.recent.filter(item => state.period === 'all' || localDay(item.at) >= localDay(since));
  $('#log-caption').textContent = recent.length ? '最近 ' + recent.length + ' 条' : '最多保留 300 条';
  $('#request-rows').innerHTML = recent.length ? recent.map(item => '<tr><td title="' + esc(new Date(item.at).toLocaleDateString()) + '">' + esc(new Date(item.at).toLocaleTimeString('zh-CN', { hour12: false })) + '</td><td><div class="log-model" title="' + esc(item.model) + '">' + esc(item.model) + '</div><div class="log-group" title="' + esc(item.relayName + ' / ' + item.groupName) + '">' + esc(item.relayName + ' / ' + item.groupName) + '</div></td><td title="' + (item.firstTokenMs == null ? '非流式请求或没有有效内容，无法采样首字延迟' : '') + '">' + latency(item.firstTokenMs, false) + '</td><td>' + tokenCount(item.inputTokens) + '</td><td>' + tokenCount(item.outputTokens) + '</td><td><span class="log-status' + (item.status >= 400 ? ' failed' : '') + '" title="' + esc(item.error || '完成') + '">' + (item.status >= 400 ? item.status === 499 ? '已取消' : item.status : '完成') + '</span></td></tr>').join('') : '<tr><td colspan="6" class="empty-table">调用模型后，用量会显示在这里</td></tr>';
  renderChart();
}
function renderChart() {
  const days = Array.from({ length: 14 }, (_, index) => { const date = new Date(); date.setDate(date.getDate() - 13 + index); const key = localDay(date); return { key, label: (date.getMonth() + 1) + '/' + date.getDate(), value: state.usage.days[key]?.totalTokens || 0 }; });
  const max = Math.max(1, ...days.map(d => d.value));
  let svg = '<svg viewBox="0 0 820 160" preserveAspectRatio="none" role="img" aria-label="近 14 天每日 Token 用量">';
  for (let i = 0; i < 3; i++) { const y = 14 + i * 48; svg += '<line x1="40" x2="815" y1="' + y + '" y2="' + y + '" stroke="#ebe8f0" stroke-dasharray="3 4"/><text x="31" y="' + (y + 3) + '" text-anchor="end">' + shortNumber(Math.round(max * (2 - i) / 2)) + '</text>'; }
  days.forEach((day, index) => { const x = 52 + index * 54.7; const height = day.value ? Math.max(3, day.value / max * 96) : 2; svg += '<rect x="' + x + '" y="' + (110 - height) + '" width="22" height="' + height + '" rx="3" fill="' + (index === 13 ? '#8e7bd4' : day.value ? '#d5c9ee' : '#eeebf4') + '"><title>' + day.key + ' · ' + number(day.value) + ' Token</title></rect>'; if (index % 2 === 0 || index === 13) svg += '<text x="' + (x + 11) + '" y="141" text-anchor="middle">' + day.label + '</text>'; });
  $('#token-chart').innerHTML = svg + '</svg>';
}

let groupRowSequence = 0;
function addKeyRow(group = {}) {
  const row = document.createElement('div'); row.className = 'key-row'; if (group.id) row.dataset.id = group.id;
  const index = ++groupRowSequence;
  row.innerHTML = '<input class="group-name-input" aria-label="分组名称 ' + index + '" placeholder="分组 ' + index + '" value="' + esc(group.name || '') + '" maxlength="100" autocomplete="off"><input class="key-input" type="password" aria-label="分组 Key ' + index + '" placeholder="API Key" value="' + esc(group.key || '') + '" required autocomplete="off"><button class="icon-button" type="button" data-remove-key aria-label="删除分组 ' + index + '">' + icon('close') + '</button>';
  $('#key-rows').append(row); return row;
}
async function openRelayDialog(id = null) {
  state.editing = id;
  $('#relay-form').reset(); $('#key-rows').replaceChildren(); groupRowSequence = 0;
  $('#relay-form-error').hidden = true; $('#batch-keys-section').hidden = true;
  $('#delete-relay').hidden = !id;
  $('#relay-dialog-title').textContent = id ? '编辑中转' : '添加中转';
  $('#save-relay').textContent = id ? '保存并更新模型' : '添加并获取模型';
  try {
    if (id) { const relay = await api('/relays/' + id); $('#relay-name').value = relay.name; $('#relay-url').value = relay.url; relay.groups.forEach(addKeyRow); }
    else addKeyRow();
    $('#relay-dialog').showModal();
    $('#relay-name').focus();
  } catch (error) { toast(error.message, true); }
}
function confirmDelete(title, message, onConfirm) {
  $('#confirm-ok').textContent = '删除';
  $('#confirm-title').textContent = title; $('#confirm-message').textContent = message;
  $('#confirm-dialog').showModal();
  $('#confirm-ok').onclick = async () => { $('#confirm-dialog').close(); await onConfirm(); };
  $('#confirm-cancel').focus();
}

$$('.nav-item').forEach(button => button.addEventListener('click', () => showPage(button.dataset.page)));
$$('[data-window]').forEach(button => button.addEventListener('click', () => native(button.dataset.window)));
$('#drag-area').addEventListener('pointerdown', event => { if (event.button === 0) native('drag'); });
$('#drag-area').addEventListener('dblclick', () => native('maximize'));
$('#add-relay').onclick = () => openRelayDialog();
$('#relay-search').oninput = renderRelays;
$('#copy-url').onclick = () => state.hub && copy(state.hub.url, '接入地址已复制');
$('#copy-key').onclick = () => state.hub && copy(state.hub.apiKey, 'Key 已复制');
$('#reveal-key').onclick = () => { state.revealed = !state.revealed; render(); };
$('#service-toggle').onclick = event => busy(event.currentTarget, async () => { if (!state.hub) return; await send('/enabled', 'PUT', { enabled: !state.hub.enabled }); await refresh(true); });
$('#sync-all').onclick = event => busy(event.currentTarget, async () => {
  if (!state.hub?.relays.length) return toast('先添加一个中转');
  await Promise.all(state.hub.relays.map(relay => syncRelay(relay.id)));
});
$('#relay-list').onclick = async event => {
  const button = event.target.closest('button'); if (!button) return;
  const action = button.dataset.action; const relayId = button.closest('[data-relay]')?.dataset.relay; const groupId = button.closest('[data-group]')?.dataset.group;
  if (action === 'add') return openRelayDialog();
  if (action === 'edit') return openRelayDialog(relayId);
  if (action === 'expand') { state.expanded.has(relayId) ? state.expanded.delete(relayId) : state.expanded.add(relayId); renderRelays(); return; }
  if (action === 'open-group') return openDrawer(relayId, groupId);
  if (action === 'toggle-relay' || action === 'toggle-group') await busy(button, async () => {
    await send('/relays/' + relayId + (action === 'toggle-group' ? '/groups/' + groupId : '') + '/enabled', 'PUT', { enabled: button.getAttribute('aria-checked') !== 'true' }); await refresh(true);
  });
};
$('#close-drawer').onclick = closeDrawer; $('#drawer-backdrop').onclick = closeDrawer;
$('#model-search').oninput = renderDrawer;
$('#drawer-toggle').onclick = event => busy(event.currentTarget, async () => { await send(groupPath() + '/enabled', 'PUT', { enabled: !findGroup().group.enabled }); await refresh(true); });
$('#drawer-sync').onclick = event => busy(event.currentTarget, async () => { const context = { ...state.drawer }; await syncRelay(context.relayId, context.groupId); });
async function toggleAllModels(enabled, button) { await busy(button, async () => { const models = findGroup().group.models.filter(m => m.id.toLowerCase().includes($('#model-search').value.trim().toLowerCase())).map(m => m.id); await send(groupPath() + '/models/enabled', 'PUT', { models, enabled }); await refresh(true); }); }
$('#models-enable-all').onclick = event => toggleAllModels(true, event.currentTarget);
$('#models-disable-all').onclick = event => toggleAllModels(false, event.currentTarget);
$('#model-list').onclick = async event => {
  const button = event.target.closest('button'); if (!button) return; const model = button.closest('[data-model]')?.dataset.model;
  if (button.dataset.modelAction === 'toggle') return busy(button, async () => { await send(groupPath() + '/models/enabled', 'PUT', { models: [model], enabled: button.getAttribute('aria-checked') !== 'true' }); await refresh(true); });
  if (button.dataset.modelAction === 'delete') {
    const route = groupPath() + '/models/remove';
    confirmDelete('移除模型', '移除 ' + model + '？重新同步时，上游提供的模型会再次出现。', () => busy(null, async () => { await send(route, 'POST', { models: [model] }); await refresh(true); }));
  }
};
$('#manual-open').onclick = () => { $('#manual-model-form').hidden = false; $('#manual-models').focus(); };
$('#manual-cancel').onclick = () => $('#manual-model-form').hidden = true;
$('#manual-model-form').onsubmit = event => { event.preventDefault(); busy($('button[type=submit]', event.currentTarget), async () => { const models = $('#manual-models').value.split(/\r?\n/).map(x => x.trim()).filter(Boolean); if (!models.length) return toast('请填写模型 ID'); await send(groupPath() + '/models', 'POST', { models }); $('#manual-models').value = ''; $('#manual-model-form').hidden = true; await refresh(true); toast('模型已添加'); }); };
$('#close-relay-dialog').onclick = $('#cancel-relay-dialog').onclick = () => $('#relay-dialog').close();
$('#add-key-row').onclick = () => $('.group-name-input', addKeyRow()).focus();
$('#key-rows').onclick = event => { const button = event.target.closest('[data-remove-key]'); if (!button) return; if ($$('.key-row').length > 1) button.closest('.key-row').remove(); else toast('至少保留一个 Key 分组'); };
$('#batch-keys-toggle').onclick = () => { $('#batch-keys-section').hidden = !$('#batch-keys-section').hidden; if (!$('#batch-keys-section').hidden) $('#batch-keys').focus(); };
$('#apply-batch-keys').onclick = () => {
  const keys = $('#batch-keys').value.split(/\r?\n/).map(x => x.trim()).filter(Boolean); if (!keys.length) return toast('每行填写一个 Key');
  $$('.key-row').filter(row => !$('.key-input', row).value && !$('.group-name-input', row).value).forEach(row => row.remove());
  keys.forEach(key => addKeyRow({ key })); $('#batch-keys').value = ''; $('#batch-keys-section').hidden = true;
};
$('#relay-form').onsubmit = async event => {
  event.preventDefault(); const button = $('#save-relay'); setButtonBusy(button, true); const finish = startWork(); $('#relay-form-error').hidden = true;
  try {
    const input = { name: $('#relay-name').value.trim(), url: $('#relay-url').value.trim(), groups: $$('.key-row').map((row, index) => ({ name: $('.group-name-input', row).value.trim() || '分组 ' + (index + 1), key: $('.key-input', row).value.trim(), ...(row.dataset.id ? { id: row.dataset.id } : {}) })) };
    const id = state.editing; const result = await send('/relays' + (id ? '/' + id : ''), id ? 'PUT' : 'POST', input);
    $('#relay-dialog').close(); closeDrawer(); await refresh(true); toast('已保存，正在获取模型');
    await syncRelay(id || result.id);
  } catch (error) { if ($('#relay-dialog').open) { $('#relay-form-error').textContent = error.message; $('#relay-form-error').hidden = false; } else toast(error.message, true); }
  finally { finish(); setButtonBusy(button, false); }
};
$('#delete-relay').onclick = () => {
  const id = state.editing; const relay = findRelay(id);
  confirmDelete('删除中转', '删除“' + relay.name + '”及其中的所有 Key 分组？', () => busy(null, async () => { await send('/relays/' + id, 'DELETE'); $('#relay-dialog').close(); closeDrawer(); await refresh(true); toast('中转已删除'); }));
};
$('#confirm-cancel').onclick = () => $('#confirm-dialog').close();
$$('#usage-period button').forEach(button => button.onclick = () => { state.period = button.dataset.period; renderUsage(); });
$('#setting-eye').onclick = () => { const input = $('#setting-key'); input.type = input.type === 'password' ? 'text' : 'password'; $('#setting-eye use').setAttribute('href', '#i-' + (input.type === 'password' ? 'eye' : 'eye-off')); };
$('#settings-form').onsubmit = event => { event.preventDefault(); busy($('button[type=submit]', event.currentTarget), async () => { const result = await send('/settings', 'PUT', { port: Number($('#setting-port').value), apiKey: $('#setting-key').value }); $('#settings-feedback').textContent = result.restartRequired ? '已保存，端口将在重新启动后生效' : '已保存'; await refresh(true); toast('设置已保存'); }); };
$('#restart-button').onclick = () => native('restart'); $('#open-data').onclick = () => native('data-folder');
$('#retry-connect').onclick = () => refresh(true);
for (const dialog of $$('.dialog')) dialog.addEventListener('click', event => { if (event.target === dialog) { const rect = dialog.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close(); } });
document.addEventListener('keydown', event => { if (event.key === 'Escape' && state.drawer && !$$('dialog[open]').length) closeDrawer(); if (event.ctrlKey && event.key.toLowerCase() === 'n') { event.preventDefault(); if (!$$('dialog[open]').length) openRelayDialog(); } });

$('#show-notices').onclick = event => busy(event.currentTarget, async () => { const response = await fetch('/_app/notices'); if (!response.ok) throw new Error('无法读取开源许可'); $('#notices-text').textContent = await response.text(); $('#notices-dialog').showModal(); });
$('#close-notices').onclick = () => $('#notices-dialog').close();
refresh(); setInterval(() => { if (!document.hidden && !$$('dialog[open]').length) refresh(); }, 6000);
