async page => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.reload();
  await page.locator('#benchmarks-tab').click();
  await page.waitForFunction(() => document.getElementById('benchmark-comparisons').querySelectorAll('tbody tr').length === 4);
  const runs = await page.evaluate(async () => (await fetch('/api/benchmarks')).json());
  for (const concurrency of [1,4,16]) {
    await page.locator('#bench-concurrency').selectOption(String(concurrency));
    const rows = page.locator('#benchmark-comparisons tbody tr');
    if (await rows.count() !== 4) throw new Error('Matched set does not contain four providers.');
    for (const provider of ['sqlite','postgres','sqlserver','azurestorage']) {
      const samples = runs.filter(r => r.cohort && r.successful && r.mode === 'benchmark' && r.concurrency === concurrency && r.batchSize === 100 && r.provider === provider);
      if (samples.length !== 3) throw new Error('Expected three recorded repetitions.');
      const expected = samples.map(r => r.completionPerSecond).sort((a,b) => a-b)[1];
      const label = {sqlite:'SQLite',postgres:'PostgreSQL',sqlserver:'SQL Server',azurestorage:'Azure Storage / Azurite'}[provider];
      const row = rows.filter({has:page.getByRole('rowheader',{name:label,exact:true})});
      if (await row.locator('meter').evaluate(meter => meter.value) !== expected) throw new Error('Chart median differs from saved results.');
      if (await row.locator('td').first().textContent() !== '3') throw new Error('Incorrect repetition count.');
    }
  }
  await page.locator('#bench-concurrency').selectOption('4');
  await page.screenshot({path:'.artifacts/benchmark-comparison-desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});
  if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Comparison overflows mobile viewport.');
  await page.screenshot({path:'.artifacts/benchmark-comparison-mobile.png',fullPage:true});
  // A failed run stays in details, but cannot increase chart values or repetition counts.
  const good = runs.find(r => r.cohort && r.successful && r.concurrency === 4 && r.batchSize === 100);
  const failed = {...good, id:'failed-test', successful:false, failure:'Synthetic failed run', completionPerSecond:99999999};
  const historical = {...good, id:'historical-test', cohort:null};
  await page.route('**/api/benchmarks', route => route.fulfill({json:[good, failed, historical]}));
  await page.reload();
  await page.locator('#benchmarks-tab').click();
  await page.waitForFunction(() => document.querySelectorAll('#benchmark-history details').length === 3);
  if (await page.locator('#benchmark-comparisons meter').count() !== 1) throw new Error('Failed or historical run entered comparison.');
  if (await page.locator('#benchmark-comparisons meter').evaluate(meter => meter.value) !== good.completionPerSecond) throw new Error('Failed result changed the median.');
  if (!(await page.locator('#benchmark-history').textContent()).includes('Failed')) throw new Error('Failed run was hidden.');
  await page.unroute('**/api/benchmarks');
  if (errors.length) throw new Error(errors.join('\n'));
  return {matchedProviders:4,repetitions:3,concurrencyLevels:3,medians:true,failuresExcluded:true,historicalSeparate:true,mobile:true};
}
