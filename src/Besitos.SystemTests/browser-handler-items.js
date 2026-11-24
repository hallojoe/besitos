async page => {
  const errors = [], submissions = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.url().endsWith('/enqueue/batch')) submissions.push({url:request.url(), items:request.postDataJSON()});
  });
  const card = command => page.locator('.handler-card').filter({has:page.locator(`form[data-command="${command}"]`)});
  const form = command => page.locator(`form[data-command="${command}"]`);
  const status = command => form(command).locator('.handler-test-status');
  async function open(command) { if (await card(command).getAttribute('open') === null) await card(command).locator(':scope > summary').click(); }
  async function finished(command, prefix) {
    await page.waitForFunction(({command, prefix}) => document.querySelector(`form[data-command="${command}"] .handler-test-status`)?.textContent.startsWith(prefix), {command, prefix});
  }
  await page.reload();
  await page.waitForFunction(() => document.getElementById('handlers-status').textContent.startsWith('4 registered'));
  for (const command of ['demo.echo.v1', 'demo.delay.v1', 'demo.transient.v1', 'demo.poison.v1']) {
    await open(command);
    await form(command).getByLabel('Queue items', {exact:true}).fill(command === 'demo.echo.v1' ? '205' : '1');
    await form(command).getByLabel('Payload', {exact:true}).fill(`Card payload: ${command}`);
    const before = submissions.length;
    await form(command).getByRole('button', {name:'Add to queue', exact:true}).click();
    await finished(command, 'Added:');
    const sent = submissions.slice(before);
    if (sent.length !== (command === 'demo.echo.v1' ? 3 : 1)) throw new Error('Unexpected request batching.');
    for (const batch of sent) {
      if (!batch.url.includes('/sqlite/groups/default/')) throw new Error('Incorrect default group target.');
      if (batch.items.length > 100 || batch.items.some(item => item.command !== command || item.queueName !== 'default')) throw new Error('Incorrect handler routing.');
    }
    const first = sent[0].items[0];
    const saved = await page.evaluate(async id => (await fetch(`/api/sqlite/groups/default/items/${id}`)).json(), first.id);
    if (saved.command !== command || saved.payload !== first.payload || saved.metadata !== first.metadata) throw new Error('Saved item differs from card values.');
  }
  const echo = form('demo.echo.v1');
  await echo.locator('.handler-test-options > summary').click();
  await echo.getByLabel('Metadata JSON (optional)', {exact:true}).fill('[]');
  let before = submissions.length;
  await echo.getByRole('button', {name:'Add to queue', exact:true}).click();
  await page.waitForFunction(() => document.querySelector('form[data-command="demo.echo.v1"] .handler-test-status').textContent.includes('Metadata must be a JSON object'));
  if (submissions.length !== before) throw new Error('Invalid metadata was submitted.');
  await echo.getByLabel('Metadata JSON (optional)', {exact:true}).fill('');

  // Cancellation and routine snapshots must not enable a second submission during an active request.
  await echo.getByLabel('Queue items', {exact:true}).fill('201');
  let release, started;
  let gate = new Promise(resolve => { release = resolve; });
  let requested = new Promise(resolve => { started = resolve; });
  await page.route('**/api/sqlite/groups/default/enqueue/batch', async route => { started(); await gate; await route.continue(); });
  before = submissions.length;
  await echo.getByRole('button', {name:'Add to queue', exact:true}).click();
  await requested;
  await page.waitForTimeout(2200);
  if (await echo.getByRole('button', {name:'Add to queue', exact:true}).isEnabled()) throw new Error('Snapshot enabled a busy form.');
  await echo.getByRole('button', {name:'Cancel remaining items'}).click();
  release();
  await finished('demo.echo.v1', 'Stopped: 100 / 201');
  if (submissions.length !== before + 1) throw new Error('Cancellation submitted extra requests.');
  await page.unroute('**/api/sqlite/groups/default/enqueue/batch');

  // Changing the group stops subsequent requests without redirecting pending work.
  gate = new Promise(resolve => { release = resolve; });
  requested = new Promise(resolve => { started = resolve; });
  await page.route('**/api/sqlite/groups/default/enqueue/batch', async route => { started(); await gate; await route.continue(); });
  before = submissions.length;
  await echo.getByRole('button', {name:'Add to queue', exact:true}).click();
  await requested;
  await page.locator('#queue-group').selectOption('many-email');
  await page.waitForFunction(() => document.getElementById('handlers-status').textContent.startsWith('2 registered'));
  release();
  await page.waitForFunction(() => document.getElementById('notice').textContent.startsWith('Stopped: 100 / 201'));
  if (submissions.length !== before + 1 || !submissions[before].url.includes('/default/')) throw new Error('Group switch redirected submissions.');
  await page.unroute('**/api/sqlite/groups/default/enqueue/batch');

  for (const command of ['email.batch.v1', 'email.batch.protected.v1']) {
    await open(command);
    await form(command).getByLabel('Test emails', {exact:true}).fill('23');
    await form(command).getByLabel('Emails per queue item', {exact:true}).fill('10');
    await form(command).getByLabel('Subject', {exact:true}).fill('Card email test');
    before = submissions.length;
    await form(command).getByRole('button', {name:'Add to queue', exact:true}).click();
    await finished(command, 'Added: 3 / 3');
    const items = submissions.slice(before).flatMap(batch => batch.items);
    if (items.length !== 3 || items.some(item => item.command !== command || item.queueName !== 'many-email')) throw new Error('Email card used the wrong command.');
    const payloads = items.map(item => JSON.parse(item.payload));
    if (payloads.reduce((sum, payload) => sum + payload.recipients.length, 0) !== 23 || payloads.some(payload => payload.subject !== 'Card email test')) throw new Error('Email inputs were ignored.');
    const saved = await page.evaluate(async id => (await fetch(`/api/sqlite/groups/many-email/items/${id}`)).json(), items[0].id);
    if (saved.command !== command || saved.payload !== items[0].payload) throw new Error('Email card item was not stored.');
    if (command === 'email.batch.protected.v1') {
      const ids = payloads.map(payload => payload.operationId);
      if (new Set(ids).size !== 3 || payloads.some(payload => payload.recipients.some(recipient => !recipient.id || !recipient.address.endsWith('@example.test')))) throw new Error('Protected email identities are invalid.');
      before = submissions.length;
      await form(command).getByRole('button', {name:'Add to queue', exact:true}).click();
      await page.waitForFunction(() => document.querySelector('form[data-command="email.batch.protected.v1"] button[type=submit]').disabled === false);
      await page.waitForFunction(() => !document.querySelector('form[data-command="email.batch.protected.v1"] fieldset').disabled);
      if (submissions.slice(before).flatMap(batch => batch.items).some(item => ids.includes(JSON.parse(item.payload).operationId))) throw new Error('New submission reused an operation ID.');
    }
  }
  const protectedForm = form('email.batch.protected.v1');
  await page.route('**/api/sqlite/groups/many-email/enqueue/batch', route => route.fulfill({status:503,json:{error:'Test submission failure.'}}));
  before = submissions.length;
  await protectedForm.getByRole('button', {name:'Add to queue', exact:true}).click();
  await page.waitForFunction(() => document.querySelector('form[data-command="email.batch.protected.v1"] .handler-test-status').textContent.includes('last request may have saved items'));
  if (submissions.length !== before + 1) throw new Error('Failed submission was automatically retried.');
  await page.unroute('**/api/sqlite/groups/many-email/enqueue/batch');
  await page.setViewportSize({width:1440,height:1000});
  await card('email.batch.protected.v1').scrollIntoViewIfNeeded();
  await page.screenshot({path:'.artifacts/handler-item-controls-desktop.png'});
  await page.setViewportSize({width:390,height:844});
  if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Handler form overflows on mobile.');
  await card('email.batch.protected.v1').scrollIntoViewIfNeeded();
  await page.screenshot({path:'.artifacts/handler-item-controls-mobile.png',fullPage:true});
  if (errors.length) throw new Error(errors.join('\n'));
  return {handlerItems:true, allHandlers:true, storedItems:true, cancellation:true, groupSwitch:true, protectedIdentities:true, failures:true, mobile:true};
}
