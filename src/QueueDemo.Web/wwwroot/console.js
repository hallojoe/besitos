'use strict';
const $ = id => document.getElementById(id);
let provider = 'sqlite', group = 'default', snapshots = [], events = [], displayPaused = false, cursor = null, inspected = null;
const completed = {};
let renderPending = false;
function scheduleEvents() {
  if (renderPending) return;
  renderPending = true;
  setTimeout(() => { renderPending = false; renderEvents(); }, 100);
}
function text(tag, value, className) { const node = document.createElement(tag); node.textContent = value; if (className) node.className = className; return node; }
function notify(message, error = false) { if ($('enqueue-dialog').open) $('enqueue-error').textContent = error ? message : '';  $('notice').textContent = message; $('notice').className = error ? 'error' : ''; }
async function api(path, body, method = 'POST') {
  const response = await fetch(`/api/${provider}/groups/${group}${path}`, {method, headers: {'Content-Type':'application/json','X-Besitos-Request':'console'}, body: body === undefined ? undefined : JSON.stringify(body)});
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.error || `Request failed (${response.status}).`);
  return result;
}
function action(fn) { return async event => { event?.preventDefault(); try { await fn(event); } catch (error) { notify(error.message, true); } }; }
function render() {
  const current = snapshots.find(x => x.provider === provider && x.queueName === group);
  const ready = current?.state === 'ready';
  $('readiness').textContent = current?.state || 'not initialized'; $('readiness').className = `badge ${ready ? 'ready' : ''}`;
  $('startup-hint').textContent = ready ? 'Store initialized. Processing starts only when requested.' : provider === 'sqlite' ? 'Start the SQLite worker in Aspire. The file is created at that point.' : 'Start this database container, then its worker, in Aspire.';
  document.querySelectorAll('.requires-ready').forEach(button => button.disabled = !ready);
  const snapshot = current?.snapshot;
  for (const name of ['ready','delayed','leased','dead']) $(name).textContent = snapshot?.stats ? snapshot.stats[name].toLocaleString() : '—';
  $('oldest').textContent = snapshot?.stats?.oldestAvailableAt ? `${Math.max(0, Math.floor((Date.parse(snapshot.stats.observedAt) - Date.parse(snapshot.stats.oldestAvailableAt))/1000))}s` : '—';
  $('observed').textContent = snapshot?.stats ? `Observed ${new Date(snapshot.stats.observedAt).toLocaleTimeString()}${ready ? '' : ' · stale'}` : 'No snapshot yet';
  const approximate = snapshot?.approximateStats;
  $('approximate-stats').hidden = !approximate;
  $('approximate-stats').textContent = approximate ? `Approximate source messages: ${approximate.source.toLocaleString()} · Poison messages: ${approximate.poison.toLocaleString()} · Includes invisible messages · Observed ${new Date(approximate.observedAt).toLocaleTimeString()}${ready ? '' : ' · stale'}` : '';
  const administration = snapshot?.supportsAdministration ?? provider !== 'azurestorage';
  $('dead-refresh').disabled = !ready || !administration;
  $('dead-next').disabled = !ready || !administration || !cursor;
  $('replay').disabled = !ready || !administration || inspected?.state !== 2;
  if (!administration) $('dead-list').replaceChildren(text('p','Failed messages are retained in the poison queue. Browsing and replay are unavailable.','empty'));
  if (provider === 'azurestorage' && !ready) $('startup-hint').textContent = 'Start Azurite, then the Azure Storage worker, in Aspire.';
  const workers = current?.workers || [];
  const pending = workers.length < (current?.expectedReplicas || 1) || workers.some(w => w.paused === current?.processingEnabled);
  $('worker-summary').textContent = `${workers.length} / ${current?.expectedReplicas || (group === 'many-email' ? 3 : 1)} replicas connected · ${current?.concurrencyPerReplica || 1} configured slots per replica · ${workers.filter(w => !w.paused).reduce((sum,w) => sum+w.concurrency,0)} active handler slots`;
  $('control-state').textContent = `Requested: ${current?.processingEnabled ? 'processing' : 'paused'}${pending ? ' · waiting for replica acknowledgement' : ' · acknowledged by all replicas'}`;
  $('open-enqueue').textContent = group === 'many-email' ? 'Generate test emails' : 'Enqueue a command';
  $('generic-load').hidden = group === 'many-email';
  $('email-help').hidden = group !== 'many-email';
  $('mailpit-link').hidden = group !== 'many-email' || !$('mailpit-link').href;
  $('email-metrics').hidden = group !== 'many-email';
  $('email-metrics').textContent = `Live replica counters: ${current?.email?.accepted || 0} SMTP accepted emails · ${current?.email?.connections || 0} SMTP connections · ${current?.email?.batches || 0} batch attempts. Counters reset when replicas restart.`;
  const nodes = [];
  for (const worker of workers) {
    nodes.push(text('p', `${worker.processId} · ${worker.queueName} · ${worker.concurrency} slots · ${worker.paused ? 'paused' : 'processing'}`, 'worker-process'));
    for (const loop of worker.loops) {
      const row = text('div', '', 'loop'); row.append(text('span', `LOOP ${loop.id.split('/').pop()}`),text('span', loop.state, 'state'));
      if (loop.itemId) row.append(text('small', `${loop.itemId} · attempt ${loop.attempt}`));
      if (loop.lastRenewed) row.append(text('small', `Lease renewed ${new Date(loop.lastRenewed).toLocaleTimeString()}`));
      nodes.push(row);
    }
  }
  $('workers').replaceChildren(...(nodes.length ? nodes : [text('p','The console stays available while all stores are stopped.','empty')]));
  $('dropped').textContent = `Worker telemetry dropped: ${current?.droppedEvents || 0}`;
  $('session-counts').textContent = `Observed completions: ${completed[`${provider}/${group}`] || 0} · Best-effort telemetry for this console session.`;
  const load = current?.load || snapshot?.load;
  if (load) {
    $('load-progress').max = Math.max(1,load.requested); $('load-progress').value = load.enqueued;
    $('load-state').textContent = `${load.state} · ${load.enqueued.toLocaleString()} / ${load.requested.toLocaleString()} envelopes enqueued${load.emailsRequested ? ` · ${load.emailsEnqueued} / ${load.emailsRequested} emails enqueued` : ''}`;
    $('email-form').querySelector('button[type=submit]').disabled = !ready || load.running;
    $('load-start').disabled = !ready || load.running;
    $('load-cancel').disabled = !ready || !load.running;
  }
}
function renderEvents() {
  if (displayPaused) return;
  const query = $('event-search').value.toLowerCase(), type = $('event-type').value;
  const rows = events.filter(e => e.provider === provider && (e.queueName || 'default') === group && (!type || e.value.type === type) && (!query || JSON.stringify(e).toLowerCase().includes(query))).map(e => {
    const v = e.value, row = text('div', '', `event ${v.type}`);
    row.append(text('time',new Date(v.timestamp).toLocaleTimeString()),text('span',e.provider),text('span',v.type,'type'));
    row.append(text('span',`${v.itemId || '—'} · ${v.loop} · #${v.attempt}${v.durationMs != null ? ` · ${Math.round(v.durationMs)} ms` : ''}${v.error ? ` · ${v.error}` : ''}`,'detail'));
    return row;
  });
  $('events').replaceChildren(...(rows.length ? rows : [text('p','No matching events.','empty')]));
  if ($('follow').checked) $('events').scrollTop = $('events').scrollHeight;
}
function selectGroup() { provider = $('provider').value; group = $('queue-group').value; cursor = null; inspected = null; $('dead-list').replaceChildren(text('p','Select Refresh to inspect this store.','empty')); $('dead-next').disabled = true; render(); renderEvents(); }
$('provider').addEventListener('change', selectGroup);
$('queue-group').addEventListener('change', selectGroup);
$('enqueue-form').addEventListener('submit', action(async () => {
  const seconds = Number($('delay').value), days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600), minutes = Math.floor(seconds % 3600 / 60);
  const queueName = group;
  const result = await api('/enqueue',{command:$('command').value,payload:$('payload').value,payloadMediaType:$('media').value,metadata:$('metadata').value || null,queueName,maxAttempts:Number($('attempts').value),initialDelay:`${days ? days + '.' : ''}${String(hours).padStart(2,'0')}:${String(minutes).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`,id:crypto.randomUUID()});
  $('enqueue-dialog').close(); notify(`Enqueued ${result.id}`);
}));
const presets = {success:['demo.echo.v1',''],transient:['demo.transient.v1','{"failFirst":2}'],poison:['demo.poison.v1',''],long:['demo.delay.v1','{"milliseconds":45000}']};
document.querySelectorAll('[data-preset]').forEach(button => button.addEventListener('click', () => { const [command,metadata] = presets[button.dataset.preset]; $('command').value=command; $('metadata').value=metadata; $('attempts').value='5'; $('delay').value='0'; notify('Preset loaded. Select Enqueue command to save it.'); }));
$('start').addEventListener('click', action(async () => { await api('/control/start'); notify('Processing requested for every replica in this group.'); }));
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
stream.addEventListener('queue',event => {const data=JSON.parse(event.data);events.push(data);if(events.length>500)events.shift();if(data.value.type==='completed'){const key=`${data.provider}/${data.queueName || 'default'}`;completed[key]=(completed[key] || 0)+1;}scheduleEvents();});
render();

$('open-enqueue').addEventListener('click', () => {
  $('enqueue-error').textContent = '';
  $('enqueue-title').textContent = group === 'many-email' ? 'Generate test emails' : 'Enqueue a command';
  $('generic-enqueue').hidden = group === 'many-email';
  $('email-form').hidden = group !== 'many-email';
  $('enqueue-dialog').showModal();
});
$('close-enqueue').addEventListener('click', () => $('enqueue-dialog').close());
$('enqueue-dialog').addEventListener('close', () => $('open-enqueue').focus());
$('email-form').addEventListener('submit', action(async () => {
  await api('/load', {count:Number($('email-count').value), batchSize:Number($('email-batch-size').value)});
  $('enqueue-dialog').close(); notify('Test email generation started. Start processing to send emails.');
}));
fetch('/api/config').then(r => r.json()).then(config => {
  if (config.mailpitUrl) { const url = new URL(config.mailpitUrl); if (['http:', 'https:'].includes(url.protocol)) $('mailpit-link').href = url.href; }
  render();
}).catch(() => {});
