'use strict';

let handlerFieldSequence = 0;
const handlerTestEditors = new Map([
  ['email.batch.v1', form => emailTestEditor(form, false)],
  ['email.batch.protected.v1', form => emailTestEditor(form, true)]
]);

function testField(parent, caption, name, value, options = {}) {
  const input = document.createElement(options.multiline ? 'textarea' : 'input');
  input.id = `handler-field-${++handlerFieldSequence}`;
  input.name = name;
  input.value = value;
  if (options.multiline) input.rows = 4;
  else input.type = options.type || 'text';
  for (const key of ['min', 'max', 'step', 'maxLength', 'required']) if (options[key] !== undefined) input[key] = options[key];
  const label = text('label', caption); label.htmlFor = input.id;
  const wrapper = text('div', '', 'handler-field'); wrapper.append(label, input); parent.append(wrapper);
  return input;
}

function genericTestEditor(form, handler) {
  const count = testField(form, 'Queue items', 'count', '1', {type:'number', min:1, max:100000, step:1, required:true});
  const payload = testField(form, 'Payload', 'payload', handler.examplePayload ?? '', {multiline:true});
  const media = testField(form, 'Media type', 'media', handler.payloadMediaType, {maxLength:256, required:true});
  form.append(text('p', 'Each item uses this payload exactly as entered.', 'muted'));
  return () => { const value = payload.value; return {count:Number(count.value), payload:() => value, media:media.value}; };
}

function emailTestEditor(form, protectedEmail) {
  const sizes = text('div', '', 'form-pair'); form.append(sizes);
  const total = testField(sizes, 'Test emails', 'emailCount', '10', {type:'number', min:1, max:100000, step:1, required:true});
  const size = testField(sizes, 'Emails per queue item', 'batchSize', '10', {type:'number', min:1, max:100, step:1, required:true});
  const subject = testField(form, 'Subject', 'subject', 'Besitos test email', {maxLength:200, required:true});
  const body = testField(form, 'Body', 'body', 'A generated test email from Besitos.', {multiline:true, maxLength:16000});
  form.append(text('p', 'Creates unique test recipients at example.test. Each queue item contains one email batch.', 'muted'));
  if (protectedEmail) form.append(text('p', 'Each batch receives a new operation ID. Retries retain that ID for duplicate protection.', 'muted'));
  return () => {
    const emails = Number(total.value), batchSize = Number(size.value), job = crypto.randomUUID();
    const content = {subject:subject.value, text:body.value};
    if (!content.subject.trim()) throw new Error('Enter an email subject.');
    return {
      count:Math.ceil(emails / batchSize), media:'application/json',
      payload(index) {
        const offset = index * batchSize;
        const recipients = Array.from({length:Math.min(batchSize, emails - offset)}, (_, i) => {
          const id = `recipient-${offset + i}`, address = `${job}-${offset + i}@example.test`;
          return protectedEmail ? {id, address} : address;
        });
        return JSON.stringify({...content, recipients, ...(protectedEmail ? {operationId:crypto.randomUUID()} : {})});
      }
    };
  };
}

function createHandlerTestForm(handler, target, generation) {
  const form = text('form', '', 'handler-test-form');
  form.dataset.command = handler.command;
  form.setAttribute('aria-label', `Test ${handler.displayName}`);
  const fields = document.createElement('fieldset');
  fields.append(text('legend', 'Add test items'));
  fields.append(text('p', 'Keep this tab open until submission finishes.', 'muted'));
  const prepare = handlerTestEditors.has(handler.command)
    ? handlerTestEditors.get(handler.command)(fields) : genericTestEditor(fields, handler);
  const advanced = text('details', '', 'handler-test-options');
  advanced.append(text('summary', 'Queue options'));
  const metadata = testField(advanced, 'Metadata JSON (optional)', 'metadata', handler.exampleMetadata ?? '', {multiline:true});
  const pair = text('div', '', 'form-pair'); advanced.append(pair);
  const attempts = testField(pair, 'Max attempts', 'attempts', '5', {type:'number', min:1, max:1000, step:1, required:true});
  const delay = testField(pair, 'Initial delay (seconds)', 'delay', '0', {type:'number', min:0, max:86400, step:1, required:true});
  fields.append(advanced);
  const submit = text('button', 'Add to queue', 'primary'); submit.type = 'submit'; fields.append(submit);
  const cancel = text('button', 'Cancel remaining items'); cancel.type = 'button'; cancel.hidden = true;
  const progress = document.createElement('progress'); progress.hidden = true; progress.setAttribute('aria-label', 'Confirmed queue items');
  const status = text('p', '', 'handler-test-status'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
  form.append(fields, progress, cancel, status);
  let running = false, cancelled = false;
  const available = () => form.isConnected && catalogueState === 'loaded' && generation === catalogueRequest &&
    provider === target.provider && group === target.group && currentGroup()?.state === 'ready';
  cancel.addEventListener('click', () => { cancelled = true; cancel.disabled = true; status.textContent = 'Stopping after the current request completes…'; });
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (running || !form.reportValidity()) return;
    let confirmed = 0, requested = 0, pending = false;
    try {
      if (!available()) throw new Error('Reload the handler catalogue for the selected queue group.');
      if (metadata.value.trim()) {
        const parsed = JSON.parse(metadata.value);
        if (parsed === null || Array.isArray(parsed) || typeof parsed !== 'object') throw new Error('Metadata must be a JSON object.');
      }
      const job = prepare(); requested = job.count;
      const maxAttempts = Number(attempts.value), seconds = Number(delay.value);
      const initialDelay = `${Math.floor(seconds / 86400)}.${String(Math.floor(seconds % 86400 / 3600)).padStart(2, '0')}:${String(Math.floor(seconds % 3600 / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
      const jobMetadata = metadata.value || null;
      running = true; cancelled = false; fields.disabled = true; cancel.hidden = false; cancel.disabled = false;
      progress.hidden = false; progress.max = requested; progress.value = 0; status.classList.remove('error');
      const encoder = new TextEncoder();
      let next = null;
      while (confirmed < requested && !cancelled && available()) {
        const batch = []; let bytes = 2;
        while (confirmed + batch.length < requested && batch.length < 100) {
          const envelope = next ?? {id:crypto.randomUUID(), command:handler.command, queueName:target.group,
            payload:job.payload(confirmed + batch.length), payloadMediaType:job.media, metadata:jobMetadata, maxAttempts, initialDelay};
          const length = encoder.encode(JSON.stringify(envelope)).length + 1;
          if (length > 256 * 1024) throw new Error('A test item exceeds the console request limit. Reduce its payload.');
          if (batch.length && bytes + length > 256 * 1024) { next = envelope; break; }
          next = null; batch.push(envelope); bytes += length;
        }
        status.textContent = `Adding ${handler.displayName}: ${confirmed} / ${requested} queue items confirmed.`;
        pending = true;
        const response = await fetch(`/api/${encodeURIComponent(target.provider)}/groups/${encodeURIComponent(target.group)}/enqueue/batch`, {
          method:'POST', headers:{'Content-Type':'application/json', 'X-Besitos-Request':'console'}, body:JSON.stringify(batch)
        });
        const result = await response.json().catch(() => null);
        if (!response.ok) throw new Error(result?.error || result?.title || `Request failed (${response.status}).`);
        if (!Array.isArray(result) || result.length !== batch.length || result.some((id, index) => id !== batch[index].id))
          throw new Error('The worker did not confirm the submitted item IDs.');
        pending = false; confirmed += batch.length; progress.value = confirmed;
      }
      const message = `${confirmed === requested ? 'Added' : 'Stopped'}: ${confirmed} / ${requested} queue items confirmed for ${target.provider}/${target.group} · ${handler.displayName}.`;
      status.textContent = message; notify(message);
    } catch (error) {
      const message = `${target.provider}/${target.group} · ${handler.displayName}: ${error.message} ${confirmed} / ${requested} queue items confirmed.${pending ? ' The last request may have saved items. Inspect the queue before submitting again.' : ''}`;
      status.textContent = message; status.classList.add('error'); notify(message, true);
    } finally {
      running = false; fields.disabled = !available(); cancel.hidden = true;
    }
  });
  return form;
}
