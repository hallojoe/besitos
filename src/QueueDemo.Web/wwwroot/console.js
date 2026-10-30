'use strict';
const $ = id => document.getElementById(id);
let provider = 'sqlite', snapshots = [], events = [], displayPaused = false, cursor = null, inspected = null;
const completed = {sqlite: 0, postgres: 0, sqlserver: 0};
let renderPending = false;
function scheduleEvents() {
  if (renderPending) return;
  renderPending = true;
  setTimeout(() => { renderPending = false; renderEvents(); }, 100);
}
function text(tag, value, className) { const node = document.createElement(tag); node.textContent = value; if (className) node.className = className; return node; }
function notify(message, error = false) { $('notice').textContent = message; $('notice').className = error ? 'error' : ''; }
async function api(path, body, method = 'POST') {
  const response = await fetch(`/api/${provider}${path}`, {method, headers: {'Content-Type':'application/json','X-Besitos-Request':'console'}, body: body === undefined ? undefined : JSON.stringify(body)});
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.error || `Request failed (${response.status}).`);
  return result;
}
function action(fn) { return async event => { event?.preventDefault(); try { await fn(event); } catch (error) { notify(error.message, true); } }; }
function render() {
  const current = snapshots.find(x => x.provider === provider);
  const ready = current?.state === 'ready';
  $('readiness').textContent = current?.state || 'not initialized'; $('readiness').className = `badge ${ready ? 'ready' : ''}`;
  $('startup-hint').textContent = ready ? 'Store initialized. Processing starts only when requested.' : provider === 'sqlite' ? 'Start the SQLite worker in Aspire. The file is created at that point.' : 'Start this database container, then its worker, in Aspire.';
  document.querySelectorAll('.requires-ready').forEach(button => button.disabled = !ready);
  const snapshot = current?.snapshot;
  for (const name of ['ready','delayed','leased','dead']) $(name).textContent = snapshot ? snapshot.stats[name].toLocaleString() : '—';
  $('oldest').textContent = snapshot?.stats.oldestAvailableAt ? `${Math.max(0, Math.floor((Date.parse(snapshot.stats.observedAt) - Date.parse(snapshot.stats.oldestAvailableAt))/1000))}s` : '—';
  $('observed').textContent = snapshot ? `Observed ${new Date(snapshot.stats.observedAt).toLocaleTimeString()}${ready ? '' : ' · stale'}` : 'No snapshot yet';
  const workers = current?.workers || [];
  $('worker-summary').textContent = workers.length ? `${workers.length} process${workers.length > 1 ? 'es' : ''} · X = ${workers.reduce((sum,w) => sum+w.concurrency,0)} total` : 'No worker connected';
  const nodes = [];
  for (const worker of workers) {
    nodes.push(text('p', `${worker.processId} · ${worker.queueName}`, 'worker-process'));
    for (const loop of worker.loops) {
      const row = text('div', '', 'loop'); row.append(text('span', `LOOP ${loop.id.split('/').pop()}`),text('span', loop.state, 'state'));
      if (loop.itemId) row.append(text('small', `${loop.itemId} · attempt ${loop.attempt}`));
      if (loop.lastRenewed) row.append(text('small', `Lease renewed ${new Date(loop.lastRenewed).toLocaleTimeString()}`));
      nodes.push(row);
    }
  }
  $('workers').replaceChildren(...(nodes.length ? nodes : [text('p','The console stays available while all stores are stopped.','empty')]));
  $('dropped').textContent = `Worker telemetry dropped: ${current?.droppedEvents || 0}`;
  $('session-counts').textContent = `Observed completions: ${completed[provider]} · Best-effort telemetry for this console session.`;
  if (snapshot) {
    $('load-progress').max = Math.max(1,snapshot.load.requested); $('load-progress').value = snapshot.load.enqueued;
    $('load-state').textContent = `${snapshot.load.state} · ${snapshot.load.enqueued.toLocaleString()} / ${snapshot.load.requested.toLocaleString()} enqueued`;
    $('load-start').disabled = !ready || snapshot.load.running;
    $('load-cancel').disabled = !ready || !snapshot.load.running;
  }
}
function renderEvents() {
  if (displayPaused) return;
  const query = $('event-search').value.toLowerCase(), type = $('event-type').value;
  const rows = events.filter(e => e.provider === provider && (!type || e.value.type === type) && (!query || JSON.stringify(e).toLowerCase().includes(query))).map(e => {
    const v = e.value, row = text('div', '', `event ${v.type}`);
    row.append(text('time',new Date(v.timestamp).toLocaleTimeString()),text('span',e.provider),text('span',v.type,'type'));
    row.append(text('span',`${v.itemId || '—'} · ${v.loop} · #${v.attempt}${v.durationMs != null ? ` · ${Math.round(v.durationMs)} ms` : ''}${v.error ? ` · ${v.error}` : ''}`,'detail'));
    return row;
  });
  $('events').replaceChildren(...(rows.length ? rows : [text('p','No matching events.','empty')]));
  if ($('follow').checked) $('events').scrollTop = $('events').scrollHeight;
}
$('provider').addEventListener('change', () => { provider = $('provider').value; cursor = null; inspected = null; $('dead-list').replaceChildren(text('p','Select Refresh to inspect this store.','empty')); $('dead-next').disabled = true; render(); renderEvents(); });
$('enqueue-form').addEventListener('submit', action(async () => {
  const seconds = Number($('delay').value), days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600), minutes = Math.floor(seconds % 3600 / 60);
  const queueName = snapshots.find(x => x.provider === provider)?.snapshot?.worker.queueName || 'default';
  const result = await api('/enqueue',{command:$('command').value,payload:$('payload').value,payloadMediaType:$('media').value,metadata:$('metadata').value || null,queueName,maxAttempts:Number($('attempts').value),initialDelay:`${days ? days + '.' : ''}${String(hours).padStart(2,'0')}:${String(minutes).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`,id:crypto.randomUUID()});
  notify(`Enqueued ${result.id}`);
}));
const presets = {success:['demo.echo.v1',''],transient:['demo.transient.v1','{"failFirst":2}'],poison:['demo.poison.v1',''],long:['demo.delay.v1','{"milliseconds":45000}']};
document.querySelectorAll('[data-preset]').forEach(button => button.addEventListener('click', () => { const [command,metadata] = presets[button.dataset.preset]; $('command').value=command; $('metadata').value=metadata; $('attempts').value='5'; $('delay').value='0'; notify('Preset loaded. Select Enqueue command to save it.'); }));
$('start').addEventListener('click', action(async () => { await api('/control/start'); notify('Processing started on the configured worker.'); }));
$('pause').addEventListener('click', action(async () => { await api('/control/pause'); notify('New claims paused. Active handlers can finish.'); }));
$('load-start').addEventListener('click', action(async () => { await api('/load',{count:Number($('load-count').value)}); notify('Workload generation started.'); }));
$('load-cancel').addEventListener('click', action(async () => { await api('/load/cancel'); notify('Cancellation requested.'); }));
$('event-search').addEventListener('input',renderEvents); $('event-type').addEventListener('change',renderEvents);
$('display-pause').addEventListener('click', () => { displayPaused=!displayPaused; $('display-pause').textContent=displayPaused?'Resume display':'Pause display'; $('display-pause').setAttribute('aria-pressed',String(displayPaused)); renderEvents(); });
async function loadDead(next = false) {
  const query = next && cursor ? `?created=${encodeURIComponent(cursor.createdAt)}&after=${cursor.id}` : '';
  const result = await api(`/items${query}`,undefined,'GET'); cursor=result.next; $('dead-next').disabled=!cursor;
  const rows = result.items.map(item => { const row=text('div','','dead-row'),label=text('div',''); label.append(text('p',item.command),text('small',`${item.id} · ${item.attemptCount} attempts`)); const button=text('button','Inspect →'); button.addEventListener('click',action(() => inspect(item.id))); row.append(label,button);return row; });
  $('dead-list').replaceChildren(...(rows.length?rows:[text('p','No dead letters on this page.','empty')]));
}
async function inspect(id) {
  inspected=await api(`/items/${id}`,undefined,'GET');
  const fields=[]; for (const [key,value] of Object.entries({ID:inspected.id,Command:inspected.command,'Media type':inspected.payloadMediaType,Attempts:inspected.attemptCount})) fields.push(text('dt',key),text('dd',String(value)));
  $('item-fields').replaceChildren(...fields); $('item-payload').textContent=inspected.payload; $('item-metadata').textContent=inspected.metadata || '—'; $('item-error').textContent=inspected.lastError || '—'; $('replay').disabled=inspected.state!==2; $('inspector').showModal();
}
$('dead-refresh').addEventListener('click',action(() => loadDead())); $('dead-next').addEventListener('click',action(() => loadDead(true)));
$('close-inspector').addEventListener('click',() => $('inspector').close());
$('replay').addEventListener('click',action(async () => { if (!inspected) return; await api(`/items/${inspected.id}/replay`); $('inspector').close(); notify(`Replayed ${inspected.id}. Attempts reset to zero.`); await loadDead(); }));
const stream = new EventSource('/events');
stream.addEventListener('open',() => $('stream-state').textContent='● Event stream connected');
stream.addEventListener('error',() => $('stream-state').textContent='○ Reconnecting · history may contain gaps');
stream.addEventListener('reset',event => {const data=JSON.parse(event.data);snapshots=data.snapshot;render();if(data.reason==='history-gap')notify('Event history gap. Current snapshots have been restored.',true);});
stream.addEventListener('snapshot',event => {snapshots=JSON.parse(event.data);render();});
stream.addEventListener('queue',event => {const data=JSON.parse(event.data);events.push(data);if(events.length>500)events.shift();if(data.value.type==='completed')completed[data.provider]++;scheduleEvents();});
render();
