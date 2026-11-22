async page => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.reload();
  await page.waitForFunction(() => document.getElementById('readiness').textContent === 'ready');
  if (await page.locator('#enqueue-form').isVisible()) throw new Error('Enqueue form is visible without opening the modal.');
  await page.locator('#open-enqueue').click();
  await page.locator('#enqueue-dialog').waitFor({state:'visible'});
  await page.keyboard.press('Escape');
  if (await page.locator('#enqueue-dialog').isVisible()) throw new Error('Escape did not close modal.');
  if (!await page.locator('#open-enqueue').evaluate(node => node === document.activeElement)) throw new Error('Focus was not restored.');
  await page.locator('#queue-group').selectOption('many-email');
  await page.waitForFunction(() => document.getElementById('worker-summary').textContent.includes('3 / 3 replicas'));
  await page.locator('#open-enqueue').click();
  if (!await page.locator('#email-form').isVisible()) throw new Error('Email generator missing.');
  await page.locator('#email-count').fill('12');
  await page.locator('#email-batch-size').fill('4');
  await page.getByRole('button',{name:'Generate test emails',exact:true}).last().click();
  await page.waitForFunction(() => document.getElementById('load-state').textContent.includes('12 / 12 emails'));
  await page.screenshot({path:'.artifacts/email-operations.png',fullPage:true});
  if (errors.length) throw new Error(errors.join('\n'));
  return {modal:true,keyboard:true,groups:true,emailGenerator:true};
}
