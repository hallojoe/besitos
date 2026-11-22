'use strict';
contributions.set('besitos.email.v1', {
  render(current, ready) {
    const active = current?.workload === 'besitos.email.v1';
    if (active) { $('open-enqueue').textContent = 'Generate test emails'; $('generic-load').hidden = true; }
    $('email-help').hidden = !active;
    $('mailpit-link').hidden = !active || !$('mailpit-link').href;
    $('email-metrics').hidden = !active;
    const counters = current?.email || current?.diagnostics?.['besitos.email.v1'] || {};
    $('email-metrics').textContent = `Live replica counters: ${counters.accepted || 0} SMTP accepted emails · ${counters.connections || 0} SMTP connections · ${counters.batches || 0} batch attempts. Counters reset when replicas restart.`;
    $('email-form').querySelector('button[type=submit]').disabled = !ready || current?.load?.running;
  },
  open(current) {
    const active = current?.workload === 'besitos.email.v1';
    $('email-form').hidden = !active;
    if (active) { $('enqueue-title').textContent = 'Generate test emails'; $('generic-enqueue').hidden = true; }
  }
});
$('email-form').addEventListener('submit', action(async () => {
  await api('/load', {count:Number($('email-count').value), batchSize:Number($('email-batch-size').value)});
  $('enqueue-dialog').close(); notify('Test email generation started. Start processing to send emails.');
}));
render();
