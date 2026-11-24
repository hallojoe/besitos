async page => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.reload();
  await page.getByRole('combobox', {name:'ACTIVE STORE'}).selectOption('azurestorage');
  await page.waitForFunction(() => document.getElementById('readiness').textContent === 'ready');
  await page.locator('#approximate-stats').waitFor({state:'visible'});
  for (const id of ['ready','delayed','leased','dead','oldest']) {
    if (await page.locator(`#${id}`).textContent() !== '—') throw new Error('Invented exact counts.');
  }
  for (const id of ['dead-refresh','dead-next','replay']) {
    if (await page.locator(`#${id}`).isEnabled()) throw new Error('Unsupported administration enabled.');
  }
  await page.locator('#open-enqueue').click();
  await page.getByRole('button', {name:'Success',exact:true}).click();
  await page.getByRole('button', {name:'Enqueue command'}).click();
  await page.waitForFunction(() => document.getElementById('notice').textContent.startsWith('Enqueued '));
  await page.getByRole('button', {name:'Start processing',exact:true}).click();
  await page.waitForFunction(() => document.getElementById('events').textContent.includes('completed'));
  await page.getByRole('button', {name:'Pause',exact:true}).click();
  await page.getByRole('combobox', {name:'ACTIVE STORE'}).selectOption('sqlite');
  if (await page.locator('#approximate-stats').isVisible()) throw new Error('Azure counts leaked into SQLite view.');
  await page.getByRole('combobox', {name:'ACTIVE STORE'}).selectOption('azurestorage');
  await page.setViewportSize({width:390,height:844});
  if (await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)) throw new Error('Mobile overflow.');
  await page.screenshot({path:'.artifacts/azurestorage-smoke/console-mobile.png',fullPage:true});
  if (errors.length) throw new Error(errors.join('\n'));
  return {passed:true,approximateCounts:true,administrationDisabled:true,enqueue:true,processing:true,mobile:true};
}
