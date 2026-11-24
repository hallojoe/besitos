async page => {
  await page.getByRole('combobox', {name:'ACTIVE STORE'}).selectOption('postgres');
  await page.waitForFunction(() => document.getElementById('readiness').textContent === 'ready');
  await page.getByRole('button', {name:'Pause',exact:true}).click();
  const client=await page.context().newCDPSession(page);
  await client.send('Performance.enable');
  async function sample() {
    const metrics=await client.send('Performance.getMetrics');
    const values=Object.fromEntries(metrics.metrics.map(x=>[x.name,x.value]));
    return {heap:values.JSHeapUsedSize,nodes:values.Nodes,rows:await page.locator('#events .event').count()};
  }
  const before=await sample();
  await page.getByText('Generate a bounded workload',{exact:true}).click();
  await page.getByRole('spinbutton',{name:'Item count · maximum 100,000'}).fill('100000');
  await page.getByRole('button',{name:'Generate',exact:true}).click();
  await page.waitForFunction(() => document.getElementById('load-state').textContent.includes('running'),{},{timeout:10000});
  await page.getByRole('button',{name:'Cancel generation',exact:true}).click();
  await page.waitForFunction(() => document.getElementById('load-state').textContent.includes('cancelled'),{},{timeout:10000});
  // Drain the cancelled prefix before measuring the full workload.
  await page.getByRole('button',{name:'Start processing',exact:true}).click();
  await page.waitForFunction(() => ['ready','delayed','leased','dead'].every(id=>document.getElementById(id).textContent==='0'),{},{timeout:120000});
  await page.getByRole('button',{name:'Pause',exact:true}).click();
  await page.getByRole('button',{name:'Generate',exact:true}).click();
  await page.waitForFunction(() => document.getElementById('load-state').textContent.includes('completed') && document.getElementById('ready').textContent.replaceAll(',','')==='100000',{},{timeout:600000});
  const backlog=await sample();
  await page.getByRole('button',{name:'Start processing',exact:true}).click();
  const samples=[];
  let done=false;
  for(let i=0;i<120;i++){
    await new Promise(resolve=>setTimeout(resolve,5000));
    samples.push(await sample());
    done=await page.evaluate(() => ['ready','delayed','leased','dead'].every(id=>document.getElementById(id).textContent==='0'));
    if(done)break;
  }
  if(!done)throw new Error('Load did not drain.');
  if(samples.some(x=>x.rows>500))throw new Error('DOM row limit exceeded.');
  await client.send('HeapProfiler.collectGarbage');
  const after=await sample();
  const dropped=await page.locator('#dropped').textContent();
  await page.getByRole('button',{name:'Pause',exact:true}).click();
  await page.screenshot({path:'.artifacts/load-complete.png',fullPage:true});
  return {provider:'postgres',count:100000,concurrency:4,cancellation:true,finalRows:0,before,backlog,after,peakHeap:Math.max(...samples.map(x=>x.heap)),peakDomNodes:Math.max(...samples.map(x=>x.nodes)),maxEventRows:Math.max(...samples.map(x=>x.rows)),dropped,sampleCount:samples.length};
}
