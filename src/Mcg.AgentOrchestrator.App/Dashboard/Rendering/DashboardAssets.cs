namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static class DashboardAssets
{
    public const string Styles = """
body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f7f7f8;color:#1f2328;font-size:14px;line-height:1.45}
header{background:#20242c;color:white;padding:16px 24px}
main{padding:20px 24px;display:grid;gap:18px}
section{background:white;border:1px solid #d8dee4;border-radius:8px;padding:16px}
h1,h2,h3{margin:0 0 10px}h3{margin-top:18px}.meta,.section-note{color:#59636e;font-size:13px}.section-note{margin:0 0 8px}
table{width:100%;border-collapse:collapse;margin-top:8px} th,td{text-align:left;border-bottom:1px solid #eaeef2;padding:8px;vertical-align:top}th{font-weight:600}
.pill{display:inline-block;border-radius:999px;padding:2px 8px;background:#eef2f7;font-size:12px}
.attention{border-left:4px solid #c2410c;padding-left:10px;margin:6px 0}.ok{color:#166534}.bad{color:#991b1b}.completion-banner{border:1px solid #86b782;background:#edf7ed;color:#166534;border-radius:8px;padding:12px;display:flex;flex-wrap:wrap;gap:8px 12px;align-items:baseline}.completion-banner strong{font-size:16px}
.goal-card{display:grid;gap:16px}.goal-header{display:grid;gap:4px}.goal-actions{display:grid;gap:10px;margin:0}.goal-control-card,.goal-panel{border:1px solid #d8dee4;border-radius:8px;background:#f6f8fa;padding:12px;box-sizing:border-box}.goal-control-card-head{display:flex;justify-content:space-between;gap:12px;align-items:baseline;border-bottom:1px solid #d8dee4;padding-bottom:8px;margin-bottom:10px}.goal-control-grid{display:grid;grid-template-columns:minmax(260px,.7fr) minmax(360px,1fr) minmax(420px,1.25fr);gap:14px;align-items:stretch}.goal-control-group{display:grid;gap:8px;align-content:start;box-sizing:border-box}.goal-control-group h4{margin:0;color:#59636e;font-size:12px}.goal-control-group-wide,.goal-control-group-planning{grid-column:1/-1}.goal-control-group-dispatch{grid-column:span 2}.goal-control-group-dispatch .controls.compact{grid-template-columns:minmax(220px,320px) auto;margin:0}.goal-control-group-dispatch .buttonbar{margin-top:4px}.goal-control-stack{display:grid;gap:8px}.goal-overview-grid,.goal-work-grid,.goal-readiness-grid{display:grid;gap:14px;align-items:start}.goal-overview-grid{grid-template-columns:minmax(260px,.6fr) minmax(360px,1.4fr)}.goal-work-grid,.goal-readiness-grid{grid-template-columns:repeat(2,minmax(360px,1fr))}.goal-panel{overflow:auto}.goal-panel h3{margin:0 0 8px}.goal-panel p{margin:6px 0 10px}.goal-panel-wide{grid-column:1/-1}.work-model-panel{background:white}.work-model-head{display:flex;justify-content:space-between;gap:12px;align-items:baseline;margin-bottom:10px}.work-model-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px}.work-model-step{border:1px solid #d8dee4;border-radius:6px;background:#f6f8fa;padding:10px;display:grid;grid-template-columns:auto 1fr;gap:10px;align-items:start}.work-model-number{width:28px;height:28px;border-radius:999px;background:#1f6feb;color:white;display:flex;align-items:center;justify-content:center;font-weight:600}.work-model-step p{margin:4px 0 0;color:#59636e;font-size:13px}.linkbar,.buttonbar,.task-actions{display:flex;flex-wrap:wrap;gap:8px;align-items:center}.linkbar{gap:10px}.linkbar a{white-space:nowrap}
.controls{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,max-content));gap:8px;align-items:end;margin:10px 0}.controls.compact{grid-template-columns:repeat(auto-fit,minmax(150px,max-content))}.controls.wide-form{grid-template-columns:minmax(130px,150px) minmax(240px,1fr) minmax(240px,1fr) auto auto}.controls.answer-controls{grid-template-columns:minmax(280px,900px);align-items:start}.controls.answer-controls button{justify-self:start}.field{display:grid;gap:4px;min-width:130px}.field label{font-size:12px;color:#59636e;min-height:16px}
input,select,textarea,button{font:inherit}input,select,textarea{box-sizing:border-box;border:1px solid #d8dee4;border-radius:6px;padding:7px 9px;min-width:130px;width:100%;min-height:36px}textarea{min-height:92px;resize:vertical}button,.button-link{box-sizing:border-box;border:1px solid #8c959f;border-radius:6px;background:#fff;padding:7px 12px;min-height:36px;display:inline-flex;align-items:center;justify-content:center;cursor:pointer;white-space:nowrap;text-decoration:none;color:#1f2328}button.primary,.button-link.primary{background:#1f6feb;border-color:#1f6feb;color:#fff}button:hover,.button-link:hover{background:#f6f8fa}.button-link.primary:hover{background:#1158c7}button:disabled{color:#59636e;background:#f6f8fa;border-color:#d8dee4;cursor:not-allowed}
input[type=checkbox]{min-width:auto;width:auto;min-height:auto}.checkrow{display:flex;gap:6px;align-items:center;min-height:36px;white-space:nowrap}
.report-preview-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px;align-items:stretch}.report-preview-card{border:1px solid #d8dee4;border-radius:6px;background:white;padding:12px;display:grid;gap:8px;align-content:start;min-height:128px;box-sizing:border-box}.report-preview-card-wide{grid-column:span 2}.report-preview-card-head{display:flex;justify-content:space-between;gap:8px;align-items:baseline}.report-preview-card-head a{font-size:12px;white-space:nowrap}.report-preview-card p{margin:0;font-weight:600}.report-preview-list{display:grid;gap:4px;color:#59636e;font-size:13px}.report-preview-detail{overflow-wrap:anywhere}.workspace-context{display:grid;gap:8px;margin:0 0 12px;padding:10px;border:1px solid #d8dee4;border-radius:6px;background:#f6f8fa}.workspace-context dl{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:6px 12px;margin:0}.workspace-context dt{color:#59636e;font-size:12px}.workspace-context dd{margin:0;min-width:0;overflow-wrap:anywhere}.statusline{min-height:20px;color:#59636e;font-size:13px}.task-summary-row td{border-bottom:0}.task-action-row td{padding:0 8px 14px 40px}.task-action-card{border:1px solid #d8dee4;border-radius:8px;background:#f6f8fa;padding:12px}.task-action-card-head{display:flex;justify-content:space-between;gap:12px;align-items:baseline;border-bottom:1px solid #d8dee4;padding-bottom:8px;margin-bottom:10px}.task-action-panel{display:grid;grid-template-columns:minmax(260px,1fr) minmax(300px,1.15fr) minmax(340px,1.25fr);gap:14px;align-items:stretch}.task-action-panel-advanced{margin-top:10px}.task-action-group{display:grid;gap:8px;align-content:start;align-self:stretch;box-sizing:border-box}.task-action-group h4{margin:0;color:#59636e;font-size:12px}.task-action-group-wide{grid-column:1/-1;align-self:start}.task-primary-action{border:1px solid #b6c7dc;background:white;border-radius:6px;padding:10px}.task-advanced-controls{grid-column:1/-1}.task-advanced-controls summary{cursor:pointer;color:#59636e;font-weight:600}.task-actions{display:flex;flex-wrap:wrap;gap:8px;align-items:center}.task-action-panel .buttonbar{margin:0}.task-action-panel .controls.compact{grid-template-columns:minmax(150px,1fr) auto;gap:8px;margin:0}.task-action-panel .task-primary-action .controls.compact{grid-template-columns:minmax(260px,1fr) auto}.task-action-group-verification .controls.compact{grid-template-columns:minmax(170px,1fr) minmax(170px,1fr) auto}.task-action-group-verification .controls.compact:first-of-type,.task-action-group-verification .controls.compact:nth-of-type(2){grid-template-columns:minmax(220px,1fr) auto}.task-action-panel .controls.compact button,.task-action-panel .buttonbar button{min-width:120px}.task-actions a,.task-actions button{margin:0}.wide{min-width:240px;max-width:680px;width:100%}.answer-form:target{outline:2px solid #1f6feb;outline-offset:3px;background:#f6f8ff}
.answer-form{border:1px solid #d8dee4;border-radius:8px;padding:12px;margin:10px 0;display:grid;gap:12px}.decision-question{font-size:16px;margin:2px 0 0;max-width:900px}.context-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:10px}.context-block{background:#f6f8fa;border:1px solid #d8dee4;border-radius:6px;padding:10px}.context-block h4{margin:0 0 6px;font-size:13px}.context-actions,.answer-choice-row{display:flex;flex-wrap:wrap;gap:8px}.answer-choice-row button{min-width:88px}.disabled-action{display:inline-flex;flex-wrap:wrap;gap:6px;align-items:center}.custom-answer[hidden]{display:none}.context-list{margin:0;padding-left:18px}
pre{white-space:pre-wrap;background:#f6f8fa;border:1px solid #d8dee4;border-radius:6px;padding:8px;max-height:220px;overflow:auto}
.dashboard-nav{display:flex;gap:0;background:#2d323c;padding:0 24px}.dashboard-nav a{color:#c9d1d9;text-decoration:none;padding:10px 16px;font-size:14px;border-bottom:2px solid transparent}.dashboard-nav a:hover{color:white;background:#363b47}.dashboard-nav a.active{color:white;border-bottom-color:#58a6ff;font-weight:600}
@media (max-width:900px){main{padding:12px}.controls,.controls.wide-form,.controls.answer-controls,.goal-control-grid,.goal-control-group-dispatch .controls.compact,.goal-overview-grid,.goal-work-grid,.goal-readiness-grid,.task-action-panel,.task-action-panel .controls.compact,.task-action-group-verification .controls.compact,.task-action-group-verification .controls.compact:first-of-type,.task-action-group-verification .controls.compact:nth-of-type(2){grid-template-columns:1fr;min-width:0}.goal-control-card-head,.task-action-card-head,.work-model-head{display:grid}.goal-control-group-wide,.goal-control-group-dispatch,.goal-control-group-planning,.goal-panel-wide,.report-preview-card-wide{grid-column:auto}.task-action-row td{padding:0 4px 12px}.buttonbar,.linkbar{align-items:stretch}.buttonbar button,.goal-control-grid button,.task-action-panel button,.controls.answer-controls button{width:100%}}
""";

    public const string OperatorControlsScript = """
function statusElement(){ return document.getElementById('op-status'); }
function setStatus(text){ const status = statusElement(); if(status) status.textContent = text; }
function isEditing(){ const active = document.activeElement; if(active?.closest?.('form[data-action]')) return true; if(document.querySelector('.custom-answer:not([hidden])')) return true; return Array.from(document.querySelectorAll('form[data-action] input:not([type=hidden]),form[data-action] textarea')).some(item => !item.disabled && item.value && item.value !== item.defaultValue); }
function payload(form, submitter){ let data; try { data = submitter ? new FormData(form, submitter) : new FormData(form); } catch { data = new FormData(form); if(submitter?.name) data.append(submitter.name, submitter.value); } const value = {}; for (const [key,item] of data.entries()) { value[key] = item === 'true' ? true : item === 'false' ? false : item; } return value; }
function summarizeResponse(text){
  if(!text) return 'Updated.';
  try {
    const value = JSON.parse(text);
    const goalId = value.goalId || value.GoalId || value.goal?.id || value.Goal?.Id || '';
    const action = value.action || value.Action;
    const stopReason = value.stopReason || value.StopReason;
    const stepCount = value.stepCount ?? value.StepCount ?? 0;
    const count = value.count ?? value.Count ?? 0;
    const goal = value.goal || value.Goal;
    const tasks = value.tasks || value.Tasks;
    const task = value.task || value.Task;
    const continuation = value.continuation || value.Continuation;
    const name = value.name || value.Name;
    const role = value.role || value.Role;
    const commandTemplate = value.commandTemplate || value.CommandTemplate;
    const processId = value.processId || value.ProcessId;
    const restartCommand = value.restartCommand || value.RestartCommand;
    const command = value.command || value.Command;
    const outputLogPath = value.outputLogPath || value.OutputLogPath;
    const errorLogPath = value.errorLogPath || value.ErrorLogPath;
    const listeningPorts = value.listeningPorts || value.ListeningPorts || [];
    const siblings = value.siblingProcesses || value.SiblingProcesses || [];
    if(stopReason) return `Stopped: ${stopReason}. Steps: ${stepCount}. Goal: ${goalId.slice(0, 8) || 'n/a'}.${continuation?.IsRunning || continuation?.isRunning ? ' Server continuation is watching.' : ''}`;
    if(action) return `Ran ${action}. Changed: ${count}. Goal: ${goalId.slice(0, 8) || 'n/a'}.`;
    if(processId && command && outputLogPath) return `Started build/test cycle PID ${processId}: ${command}. Logs: ${outputLogPath}${errorLogPath ? `, ${errorLogPath}` : ''}`;
    if(processId && restartCommand) return `Dashboard stop requested for PID ${processId}${listeningPorts.length ? ` on port(s) ${listeningPorts.join(', ')}` : ''}. Siblings: ${siblings.length}. Restart: ${restartCommand}`;
    if(goal?.Id || goal?.id) return `Goal ${(goal.Id || goal.id).slice(0, 8)} created with ${tasks?.length ?? goal.TotalTasks ?? goal.totalTasks ?? 0} task(s).`;
    if(task?.TaskNumber || task?.taskNumber) return `Task ${task.TaskNumber || task.taskNumber} updated: ${task.Status || task.status}.`;
    if(name && commandTemplate) return `Worker profile ${name} saved.`;
    if(role && name) return `${role} agent ${name} saved.`;
  } catch {}
  return 'Updated.';
}
const agentProviderOptions = {
  OpenAI: {
    apiModels: [['gpt-5.5','GPT-5.5'], ['gpt-5.5-pro','GPT-5.5 pro'], ['gpt-5.4','GPT-5.4'], ['gpt-5.4-pro','GPT-5.4 pro'], ['gpt-5.4-mini','GPT-5.4 mini'], ['gpt-5.4-nano','GPT-5.4 nano'], ['gpt-5.3-codex','GPT-5.3-Codex'], ['gpt-5-mini','GPT-5 mini'], ['gpt-5-nano','GPT-5 nano'], ['gpt-5.2','GPT-5.2 (previous)']],
    apiReasoning: [['','Default'], ['none','None'], ['low','Low'], ['medium','Medium'], ['high','High'], ['xhigh','Extra high']],
    subscriptionModels: [['','Default CLI model'], ['gpt-5.3-codex','GPT-5.3-Codex'], ['gpt-5.5','GPT-5.5']],
    subscriptionReasoning: [['','Default'], ['none','None'], ['low','Low'], ['medium','Medium'], ['high','High'], ['xhigh','Extra high']],
    preferredProfile: 'codex-cli'
  },
  Anthropic: {
    apiModels: [['claude-sonnet-4-20250514','Claude Sonnet 4'], ['claude-opus-4-20250514','Claude Opus 4']],
    apiReasoning: [['','Default']],
    subscriptionModels: [['','Default CLI model'], ['claude-sonnet','Claude Sonnet'], ['claude-opus','Claude Opus']],
    subscriptionReasoning: [['','Default']],
    preferredProfile: 'claude-cli'
  }
};
function setOptions(select, options, selected, allowConfigured){
  if(!select) return;
  const selectedValue = selected ?? '';
  select.replaceChildren(...options.map(([value,label]) => new Option(label, value, value === selectedValue, value === selectedValue)));
  if(allowConfigured && selectedValue && !options.some(([value]) => value.toLowerCase() === selectedValue.toLowerCase())){
    select.add(new Option(`Configured: ${selectedValue}`, selectedValue, true, true));
  }
}
function profileOptions(form, preferred){
  const select = form.querySelector('select[name="subscriptionProfileName"]');
  const names = Array.from(select?.options || []).map(option => option.value).filter(Boolean);
  if(preferred && !names.some(name => name.toLowerCase() === preferred.toLowerCase())) names.push(preferred);
  const unique = Array.from(new Set(names));
  unique.sort((left,right) => {
    if(left.toLowerCase() === preferred.toLowerCase()) return -1;
    if(right.toLowerCase() === preferred.toLowerCase()) return 1;
    return left.localeCompare(right);
  });
  return unique.map(name => [name, name]);
}
function syncAgentConfig(form, preserve){
  const providerSelect = form.querySelector('select[name="providerName"]');
  const provider = providerSelect?.value || 'OpenAI';
  const options = agentProviderOptions[provider] || agentProviderOptions.OpenAI;
  const currentProvider = form.dataset.currentProvider || provider;
  const providerChanged = currentProvider !== provider;
  form.dataset.currentProvider = provider;
  const role = form.querySelector('input[name="role"]')?.value || 'Agent';
  const nameInput = form.querySelector('input[data-agent-name]');
  if(nameInput && providerChanged) nameInput.value = `${provider} ${role.toLowerCase()}`;
  const keep = preserve && !providerChanged;
  setOptions(form.querySelector('select[name="modelName"]'), options.apiModels, keep ? form.querySelector('select[name="modelName"]')?.value : options.apiModels[0]?.[0], keep);
  setOptions(form.querySelector('select[name="reasoningEffort"]'), options.apiReasoning, keep ? form.querySelector('select[name="reasoningEffort"]')?.value : '', keep);
  setOptions(form.querySelector('select[name="subscriptionProfileName"]'), profileOptions(form, options.preferredProfile), keep ? form.querySelector('select[name="subscriptionProfileName"]')?.value : options.preferredProfile, keep);
  setOptions(form.querySelector('select[name="subscriptionModelAlias"]'), options.subscriptionModels, keep ? form.querySelector('select[name="subscriptionModelAlias"]')?.value : '', keep);
  setOptions(form.querySelector('select[name="subscriptionReasoningEffort"]'), options.subscriptionReasoning, keep ? form.querySelector('select[name="subscriptionReasoningEffort"]')?.value : '', keep);
  setOptions(form.querySelector('select[name="complexReasoningEffort"]'), options.apiReasoning, keep ? form.querySelector('select[name="complexReasoningEffort"]')?.value : '', keep);
}
function syncAgentConfigs(){ document.querySelectorAll('form[data-agent-config]').forEach(form => syncAgentConfig(form, true)); }
async function refreshContent(force){ if(!force && isEditing()) return; const current = document.getElementById('dashboard-content'); if(!current) return; const response = await fetch(location.href, { cache: 'no-store' }); if(!response.ok) return; const text = await response.text(); const doc = new DOMParser().parseFromString(text, 'text/html'); const next = doc.getElementById('dashboard-content'); if(next) current.replaceWith(next); }
async function post(url, body){ setStatus('Working...'); const options = { method: 'POST' }; if(body){ options.headers = {'Content-Type':'application/json'}; options.body = JSON.stringify(body); } const response = await fetch(url, options); const text = await response.text(); if(!response.ok){ throw new Error(text || response.statusText); } setStatus(summarizeResponse(text)); setTimeout(() => refreshContent(true), 350); }
window.__dashboardSubmitForm = async function(form, submitter){ return post(form.dataset.action, payload(form, submitter)); };
document.addEventListener('submit', async event => { const form = event.target.closest('form[data-action]'); if(!form) return; event.preventDefault(); try { await window.__dashboardSubmitForm(form, event.submitter); } catch(error) { setStatus(error.message); } });
document.addEventListener('change', event => { const provider = event.target.closest('form[data-agent-config] select[name="providerName"]'); if(!provider) return; syncAgentConfig(provider.closest('form'), false); });
document.addEventListener('click', async event => { const toggle = event.target.closest('button[data-toggle-custom-answer]'); if(!toggle) return; event.preventDefault(); const form = toggle.closest('form'); const panel = form?.querySelector('.custom-answer'); const textarea = document.getElementById(toggle.dataset.toggleCustomAnswer); if(!panel) return; const opening = panel.hidden; panel.hidden = !opening; toggle.setAttribute('aria-expanded', opening ? 'true' : 'false'); toggle.textContent = opening ? 'Hide custom answer' : 'Write custom answer'; if(textarea){ textarea.disabled = !opening; if(opening) textarea.focus(); else toggle.focus(); } });
document.addEventListener('click', async event => { const button = event.target.closest('button[data-action-button]'); if(!button) return; event.preventDefault(); try { await post(button.dataset.actionButton); } catch(error) { setStatus(error.message); } });
syncAgentConfigs();
const content = document.getElementById('dashboard-content'); const seconds = Number(content?.dataset.refreshSeconds || 0); if(seconds > 0) setInterval(() => refreshContent(false), seconds * 1000);
window.__dashboardReady = true;
""";
}


