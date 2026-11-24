'use strict';
let benchmarkRuns = [];
const labels = {sqlite:'SQLite', postgres:'PostgreSQL', sqlserver:'SQL Server', azurestorage:'Azure Storage / Azurite'};
function showView(name) {
  for (const view of ['operations', 'benchmarks']) {
    $(`${view}-view`).hidden = view !== name;
    $(`${view}-tab`).setAttribute('aria-pressed', String(view === name));
  }
}
$('operations-tab').addEventListener('click', () => showView('operations'));
$('benchmarks-tab').addEventListener('click', () => showView('benchmarks'));
function median(values) {
  const sorted = [...values].sort((a,b) => a-b), middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle-1] + sorted[middle]) / 2;
}
const number = value => value.toLocaleString(undefined, {maximumFractionDigits:2});
function renderBenchmarks() {
  const mode = $('bench-mode').value;
  const runs = benchmarkRuns.filter(r => r.mode === mode && r.concurrency === Number($('bench-concurrency').value) &&
    r.batchSize === Number($('bench-batch').value) && (!$('bench-provider').value || r.provider === $('bench-provider').value));
  const cohorts = Map.groupBy(runs.filter(r => r.successful && r.cohort), r => r.cohort);
  const comparisons = [];
  for (const cohort of cohorts.values()) {
    const groups = [...Map.groupBy(cohort, r => r.provider).entries()];
    const section = text('section', '', 'comparison');
    const sample = cohort[0];
    section.append(text('h3', `${number(sample.count)} envelopes · ${sample.payloadBytes} bytes each · ${sample.build}`));
    section.append(text('p', `${groups.length} provider(s) with matched conditions. Median and full range are shown. Local measurements only.`, 'muted'));
    const table = document.createElement('table');
    const caption = text('caption', `Environment ${sample.environmentId}. Workload: ${sample.mode}.`);
    const head = document.createElement('thead'), headings = document.createElement('tr');
    for (const label of ['Provider', 'Repetitions', 'Completed envelopes / second', 'Processing seconds', 'Enqueue seconds', 'Errors']) headings.append(text('th',label));
    head.append(headings); const body = document.createElement('tbody');
    const maximum = Math.max(1, ...groups.map(([, values]) => median(values.map(r => r.completionPerSecond))));
    for (const [provider, values] of groups.sort((a,b) => a[0].localeCompare(b[0]))) {
      const row = document.createElement('tr'), label = text('th', labels[provider]); label.scope = 'row';
      row.append(label, text('td', String(values.length)));
      for (const metric of ['completionPerSecond', 'consumeSeconds', 'enqueueSeconds']) {
        const samples = values.map(r => r[metric]);
        const cell = text('td', mode === 'enqueue' && metric !== 'enqueueSeconds' ? 'Not measured' : `${number(median(samples))} (${number(Math.min(...samples))}–${number(Math.max(...samples))})`);
        if (metric === 'completionPerSecond' && mode !== 'enqueue') {
          const bar = document.createElement('meter'); bar.min = 0; bar.max = maximum; bar.value = median(samples);
          bar.setAttribute('aria-label', `${labels[provider]} median completed envelopes per second`); cell.append(bar);
        }
        row.append(cell);
      }
      row.append(text('td','0')); body.append(row);
    }
    table.append(caption, head, body); const scroll = text('div','','table-scroll'); scroll.append(table); section.append(scroll); comparisons.push(section);
  }
  $('benchmark-comparisons').replaceChildren(...comparisons);
  $('benchmark-status').textContent = `${runs.length} saved runs match these filters. ${cohorts.size ? 'Comparable sets appear below.' : 'No matched comparison set. Review individual runs below.'}`;
  $('benchmark-history').replaceChildren(...runs.map(run => {
    const details = text('details','','benchmark-run');
    details.append(text('summary', `${labels[run.provider]} · ${number(run.count)} envelopes · ${run.successful ? 'Passed' : 'Failed'} · ${run.cohort ? run.capturedAt : 'Historical: comparison metadata incomplete'}`));
    details.append(text('p', `Completion rate: ${number(run.completionPerSecond)}/s. Processing: ${number(run.consumeSeconds)} seconds. Enqueue: ${number(run.enqueueSeconds)} seconds. Errors: ${run.storageErrors}.`));
    if (run.failure) details.append(text('p',run.failure,'error'));
    details.append(text('pre', `Build: ${run.build || 'not recorded'}\nDate: ${run.capturedAt || 'not recorded'}\nEngine: ${run.engine || 'not recorded'}\nEmulator: ${run.emulator || 'not recorded'}\nStorage SDK: ${run.storageSdkVersion || 'not recorded'}\nService API: ${run.serviceApiVersion || 'not recorded'}\nRuntime: ${run.runtime || 'not recorded'}\nOS: ${run.os || 'not recorded'}\nRepetition: ${run.repetition ?? 'not recorded'}`));
    const link = text('a','View saved JSON','external-link'); link.href=`/api/benchmarks/${encodeURIComponent(run.id)}/raw`; link.target='_blank'; link.rel='noopener'; details.append(link); return details;
  }));
}
for (const id of ['bench-mode','bench-concurrency','bench-batch','bench-provider']) $(id).addEventListener('change', renderBenchmarks);
fetch('/api/benchmarks').then(response => { if (!response.ok) throw new Error('Saved benchmarks are unavailable.'); return response.json(); })
  .then(runs => { benchmarkRuns = runs; renderBenchmarks(); }).catch(error => $('benchmark-status').textContent = error.message);
