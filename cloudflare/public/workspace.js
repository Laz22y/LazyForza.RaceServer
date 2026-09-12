(() => {
// Navigation keeps existing controls mounted, so changing pages never drops edits.
const workspaceText=(zh,en)=>raceI18n.isEnglish?en:zh;
const sameEvent=(a,b)=>(a??'00000000-0000-0000-0000-000000000000')===(b??'00000000-0000-0000-0000-000000000000');
let workspacePage='live',workspaceBusy=false,workspaceProjectResults=null;
const workspaceGroups={live:['#raceBanner','.summary-grid','#raceControl','#liveTiming','#stewards'],projects:['#eventProjects'],rules:['#roomConfiguration'],history:['#resultHistoryPanel','#raceEvents'],server:['#publicTimingAccess','#controlAccess']};
const workspaceTitles={live:['比赛现场','Live race'],projects:['赛事项目','Events'],rules:['规则与赛程','Rules & schedule'],history:['赛果与记录','Results & log'],server:['服务器','Server']};
for(const [key,selectors] of Object.entries(workspaceGroups)){
  const page=document.createElement('section');page.id=`workspace-${key}`;page.className='control-page';page.setAttribute('aria-label',workspaceText(...workspaceTitles[key]));
  for(const selector of selectors){const element=$(selector);if(element)page.append(element);}
  dashboard.append(page);
}
const dock=$('.dock-links');dock.replaceChildren();
for(const [key,title] of Object.entries(workspaceTitles)){
  const button=document.createElement('button');button.type='button';button.textContent=workspaceText(...title);button.dataset.workspace=key;
  if(['projects','rules','server'].includes(key))button.dataset.permission='manage';
  button.addEventListener('click',()=>showWorkspace(key));dock.append(button);
}
const context=document.createElement('section');context.className='event-context';context.innerHTML='<div class="event-context-copy"><span id="currentEventKind" class="eyebrow"></span><h2 id="currentEventTitle"></h2><p id="currentEventDetail"></p></div><div class="event-context-actions" data-permission="manage"><button id="chooseEvent"></button><button id="newEvent" class="primary"></button></div>';
$('.control-dock').after(context);
$('#chooseEvent').textContent=workspaceText('选择赛事项目','Choose event');$('#chooseEvent').onclick=()=>showWorkspace('projects');
$('#newEvent').textContent=workspaceText('准备新一场','Prepare next race');$('#newEvent').onclick=prepareNextEvent;
const room=$('#roomConfiguration');room.open=true;room.querySelector(':scope > summary').hidden=true;$('.settings-footer').append($('#settingsSaved'));$('#settingsSaved').textContent='';$('#saveSettings').textContent=workspaceText('保存规则与赛程','Save rules & schedule');
const ruleTemplates=$('.rule-template-panel');
const schedule=$('.session-options');schedule.open=true;ruleTemplates.after(schedule);
const rulesHint=document.createElement('p');rulesHint.className='workflow-note';rulesHint.textContent=workspaceText('模板是可复用的规则副本。应用后可继续修改；保存会更新当前房间和活动项目，不会反向修改模板。房间赛程也会持久保存。','Templates are reusable copies. Apply, edit, then save to the room and active event. Templates remain unchanged. The room schedule is also saved.');ruleTemplates.prepend(rulesHint);
const projectEditor=document.createElement('details');projectEditor.id='projectEditor';projectEditor.className='project-editor';
const editorHeading=document.createElement('summary');editorHeading.textContent=workspaceText('新建项目 · 使用当前配置','New event · from current configuration');projectEditor.append(editorHeading,$('.event-project-workspace'));$('#eventProjectStatus').before(projectEditor);
const projectToolbar=document.createElement('div');projectToolbar.className='project-toolbar';
projectToolbar.innerHTML='<input id="projectSearch" type="search"><select id="projectFilter"></select>';
$('#eventProjectList').before(projectToolbar);$('#projectSearch').placeholder=workspaceText('搜索赛事名称或赛道','Search event or track');$('#projectSearch').setAttribute('aria-label',$('#projectSearch').placeholder);
for(const [value,zh,en] of [['open','当前与待办','Active & planned'],['all','全部项目','All events'],['history','已完成与归档','Completed & archived']]){const option=new Option(workspaceText(zh,en),value);$('#projectFilter').add(option);}
$('#projectFilter').onchange=renderEventProjects;$('#projectSearch').oninput=renderEventProjects;
const historyFilter=document.createElement('select');historyFilter.id='historyScope';historyFilter.setAttribute('aria-label',workspaceText('记录范围','Result scope'));
historyFilter.add(new Option(workspaceText('当前赛事','Current event'),'current'));historyFilter.add(new Option(workspaceText('服务器近期记录','Recent server history'),'all'));
$('#resultHistoryPanel .section-heading').append(historyFilter);
historyFilter.onchange=()=>{workspaceProjectResults=null;renderResultHistory(stageResults);renderEvents(eventHistory);};
const steps=document.createElement('ol');steps.className='event-steps';
for(const [key,zh,en] of [['lobby','准备','Prepare'],['practice','练习','Practice'],['qualifying','排位','Qualifying'],['race','正赛','Race'],['finished','完成','Finish']]){const step=document.createElement('li');step.dataset.phase=key;step.textContent=workspaceText(zh,en);steps.append(step);}
$('#raceControl .session-flow .section-heading').after(steps);
const flowHint=document.createElement('p');flowHint.id='eventFlowHint';flowHint.className='workflow-note';steps.after(flowHint);
function showWorkspace(key){
  if(!workspaceGroups[key])key='live';
  if(['projects','rules','server'].includes(key)&&controlPrincipal&&!canManageRace())key='live';
  workspacePage=key;for(const name of Object.keys(workspaceGroups))$(`#workspace-${name}`).hidden=name!==key;
  for(const button of dock.querySelectorAll('button'))button.setAttribute('aria-current',button.dataset.workspace===key?'page':'false');
}
function projectVisible(project){const term=$('#projectSearch')?.value.trim().toLocaleLowerCase()??'',filter=$('#projectFilter')?.value??'open';return(!term||`${project.name} ${project.trackName??''}`.toLocaleLowerCase().includes(term))&&(filter==='all'||(filter==='history'?['completed','archived'].includes(project.status):['draft','active'].includes(project.status)));}
function renderWorkspace(state){
  if(!state)return;const active=eventProjects.find(p=>p.status==='active');
  text('#currentEventKind',active?workspaceText('当前项目','CURRENT EVENT'):workspaceText('当前房间 · 未关联项目','ROOM · NO EVENT PROJECT'));
  text('#currentEventTitle',active?.name??state.sessionName);
  text('#currentEventDetail',`${state.trackName??workspaceText('未绑定赛道','No track')} · ${tr(phaseLabels[state.phase]??state.phase)} · ${state.totalRaceLaps} ${workspaceText('圈','laps')}`);
  $('#newEvent').disabled=workspaceBusy||!['lobby','finished'].includes(state.phase);
  const group=state.phase==='suspended'?state.suspendedFromPhase:state.phase==='grid'?'qualifying':['outLap','formationLap','countdown'].includes(state.phase)?'race':state.phase;
  for(const step of steps.children)step.classList.toggle('current',step.dataset.phase===group);
  text('#eventFlowHint',state.phase==='finished'?workspaceText('本场已结束。先核对赛果，再准备新一场或选择其他项目。','This race is finished. Review results, then prepare another event.'):workspaceText('阶段切换保留本场记录；准备新一场会清理成绩、处罚和掉线占位，在线车手需重新准备。','Stage changes retain event records. A new event resets results, penalties and disconnected seats; connected drivers must ready again.'));
  updateSessionControls(state);
  $('#saveSettings').disabled=state.phase!=='lobby'||stageResults.some(r=>sameEvent(r.eventId,state.eventId)&&r.phase==='race'&&r.isComplete);
  if($('#saveSettings').disabled)text('#settingsSaved',workspaceText('比赛期间保留当前规则；结束后先准备新一场。','Rules stay fixed during the event. Prepare the next event before changing them.'));
  for(const button of document.querySelectorAll('[data-session]'))button.disabled=(['practice','qualifying'].includes(button.dataset.session)&&button.disabled)||workspaceBusy||((state.phase==='finished'||stageResults.some(r=>sameEvent(r.eventId,state.eventId)&&r.phase==='race'&&r.isComplete))&&button.dataset.session!=='lobby');
  showWorkspace(workspacePage);
}
async function prepareNextEvent(){
  if(workspaceBusy)return;
  if(!await confirmWorkspace(workspaceText('准备新一场？当前项目会完成归档，旧赛果保留；沿用房间配置，清理成绩、处罚和离线席位，在线车手需重新准备。','Prepare a new race? The active event will be completed and its results kept. Room settings remain; results, penalties and offline seats reset. Connected drivers must ready again.')))return;
  workspaceBusy=true;renderWorkspace(lastState);
  try{const response=await request('/api/admin/events/new',{});if(!response.ok)throw new Error(await responseError(response,'新赛事准备失败。'));workspaceProjectResults=null;await loadEventProjects();await loadResultHistory();await refresh();showWorkspace('live');}
  catch(error){actionError.textContent=error.message;}
  finally{workspaceBusy=false;renderWorkspace(lastState);}
}
async function viewProjectResults(project){
  const response=await fetch(`/api/admin/event-projects/${encodeURIComponent(project.id)}`,{cache:'no-store'});if(!response.ok){setEventProjectStatus(await responseError(response,'赛事项目读取失败。'),true);return;}
  const detail=(await response.json()).project;workspaceProjectResults=detail;
  let option=historyFilter.querySelector('[value="project"]');if(!option){option=new Option('','project');historyFilter.add(option);}option.textContent=project.name;historyFilter.value='project';
  renderResultHistory(stageResults);renderEvents(eventHistory);showWorkspace('history');
}
function renderResultHistory(results){renderWorkspace(lastState);const source=workspaceProjectResults?.results??results;renderResultHistoryRaw(workspaceProjectResults||historyFilter.value==='all'?source:source.filter(r=>(r.eventId??'00000000-0000-0000-0000-000000000000')===(lastState?.eventId??'00000000-0000-0000-0000-000000000000')));}
function renderEvents(events){const source=workspaceProjectResults?.auditEvents??events;renderEventsRaw(workspaceProjectResults||historyFilter.value==='all'?source:source.filter(e=>(e.eventId??'00000000-0000-0000-0000-000000000000')===(lastState?.eventId??'00000000-0000-0000-0000-000000000000')));}
async function saveActiveProjectConfiguration(){
  const active=eventProjects.find(p=>p.status==='active');if(!active)return true;
  const loaded=await fetch(`/api/admin/event-projects/${encodeURIComponent(active.id)}`,{cache:'no-store'});if(!loaded.ok)return false;
  const project=(await loaded.json()).project;
  const response=await request(`/api/admin/event-projects/${encodeURIComponent(active.id)}`,{...project,schedule:readEventSchedule(),captureConfiguration:true},'PUT');
  if(!response.ok){text('#settingsSaved',await responseError(response,'项目配置保存失败。'));return false;}
  await loadEventProjects();return true;
}
showWorkspace('live');

async function loadWorkspaceSchedule(){
  const response=await fetch('/api/admin/schedule',{cache:'no-store'});
  if(response.ok)applyEventSchedule((await response.json()).schedule);
}
async function saveWorkspaceConfiguration(){
  try {
    const response=await request('/api/admin/schedule',readEventSchedule(),'PUT');
    if(!response.ok){text('#settingsSaved',await responseError(response,'赛程保存失败。'));return false;}
    return await saveActiveProjectConfiguration();
  } catch(error) {text('#settingsSaved',workspaceText('房间设置已保存，赛程或项目保存失败，请重试。','Room settings saved; schedule or event saving failed. Please retry.'));return false;}
}

function confirmWorkspace(message){
  return new Promise(resolve=>{
    const previous=document.activeElement,dialog=document.createElement('dialog');
    dialog.className='event-confirm';dialog.setAttribute('aria-labelledby','eventConfirmTitle');
    dialog.innerHTML='<h2 id="eventConfirmTitle"></h2><p></p><form method="dialog"><button value="cancel"></button><button class="primary" value="confirm"></button></form>';
    dialog.querySelector('h2').textContent=workspaceText('确认切换赛事','Confirm event switch');
    dialog.querySelector('p').textContent=message;
    dialog.querySelector('[value="cancel"]').textContent=workspaceText('取消','Cancel');
    dialog.querySelector('[value="confirm"]').textContent=workspaceText('确认并准备','Confirm and prepare');
    dialog.addEventListener('close',()=>{const accepted=dialog.returnValue==='confirm';dialog.remove();if(previous?.isConnected)previous.focus();resolve(accepted);},{once:true});
    document.body.append(dialog);dialog.showModal();
  });
}
Object.assign(window,{confirmWorkspace,workspaceText,showWorkspace,projectVisible,renderWorkspace,viewProjectResults,renderResultHistory,renderEvents,saveWorkspaceConfiguration,loadWorkspaceSchedule});
})();
