const token = location.hash.slice(1) || sessionStorage.getItem('launcher-token') || '';
if (token) sessionStorage.setItem('launcher-token', token);
history.replaceState(null, '', '/');
const element = (id) => document.getElementById(id);
const factions = ['Marquise (host)', 'Eyrie', 'Woodland Alliance', 'Vagabond', 'Lizard Cult', 'Riverfolk'];
let lastInvitations = '';
let lastSaves = '';
let closed = false;
async function request(path, values) {
  const response = await fetch('/api/' + path, {
    method: values ? 'POST' : 'GET',
    headers: {Authorization: 'Bearer ' + token, ...(values ? {'Content-Type': 'application/json'} : {})},
    ...(values ? {body: JSON.stringify(values)} : {}),
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || 'Launcher request failed.');
  return result;
}
for (const button of document.querySelectorAll('[data-action]')) {
  button.addEventListener('click', async () => {
    const values = Object.fromEntries(['game', 'save', 'invite'].map(id => [id, element(id).value.trim()]));
    try {
      await request(button.dataset.action, values);
      element('error').textContent = '';
      if (button.dataset.action === 'quit') {
        closed = true;
        sessionStorage.removeItem('launcher-token');
        element('status').textContent = 'Launcher closed. You can close this tab.';
      } else await refresh();
    } catch (error) { element('error').textContent = error.message; }
  });
}
async function refresh() {
  if (closed) return;
  try {
    const state = await request('status');
    element('status').textContent = state.message;
    element('storage').textContent = 'Private files: ' + state.data + (state.save ? ' · Save: ' + state.save : '');
    if (state.error) element('error').textContent = state.error;
    for (const button of document.querySelectorAll('[data-action]')) {
      const action = button.dataset.action;
      button.disabled = state.busy || (state.active && !['stop'].includes(action)) ||
        (!state.prepared && ['host', 'join', 'wait', 'resume'].includes(action));
    }
    const saves = JSON.stringify(state.saves);
    if (saves !== lastSaves) {
      lastSaves = saves;
      element('save').replaceChildren(...state.saves.map(name => new Option(name, name)));
    }
    const invitations = JSON.stringify(state.invitations);
    if (invitations !== lastInvitations) {
      lastInvitations = invitations;
      element('invitation-section').hidden = !state.invitations.length;
      element('invitations').replaceChildren();
      state.invitations.forEach((invite, index) => {
        if (index === 0) return;
        const row = document.createElement('div'); row.className = 'seat';
        const title = document.createElement('strong'); title.textContent = `${index + 1}. ${factions[index]}`;
        const field = document.createElement('input'); field.value = invite; field.readOnly = true;
        field.setAttribute('aria-label', factions[index] + ' invitation');
        const copy = document.createElement('button'); copy.textContent = 'Copy invite';
        copy.onclick = async () => {
          try { await navigator.clipboard.writeText(invite); copy.textContent = 'Copied'; }
          catch { field.select(); element('error').textContent = 'Select and copy the invitation text.'; }
        };
        row.append(title, field, copy); element('invitations').append(row);
      });
    }
  } catch (error) { element('error').textContent = error.message + ' Reopen the launcher if it has stopped.'; }
}
refresh();
setInterval(refresh, 1500);
