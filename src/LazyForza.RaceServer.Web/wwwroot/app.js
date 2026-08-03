const $=selector=>document.querySelector(selector);
const loginPanel=$('#loginPanel'),setupPanel=$('#setupPanel'),dashboard=$('#dashboard');
const loginError=$('#loginError'),setupError=$('#setupError'),actionError=$('#actionError');
const connectionState=$('#connectionState span'),timingRows=$('#timingRows');
let polling=null;
const phaseLabels={lobby:'大厅',qualifying:'排位赛',grid:'发车区',countdown:'发车倒计时',race:'正赛',suspended:'比赛暂停',finished:'比赛结束'};
const statusLabels={connected:'已连接',ready:'已准备',onTrack:'赛道上',inPitLane:'维修区通道',inService:'正在维修',finished:'已完赛',didNotFinish:'退赛',disqualified:'取消资格',disconnected:'已掉线'};
const flagLabels={green:'GREEN',yellow:'YELLOW',red:'RED',chequered:'CHEQUERED'};

initialize();
async function initialize(){
  try{
    const response=await fetch('/api/setup/status',{cache:'no-store'});
    const status=await response.json();
    if(!status.isConfigured){
      const defaults=status.defaults??{};
      $('#setupSessionName').value=defaults.sessionName??'地产赛事';
      $('#setupRaceLaps').value=defaults.totalRaceLaps??10;
      $('#setupSectorCount').value=defaults.sectorCount??3;
      setupPanel.classList.remove('hidden');connectionState.textContent='等待首次设置';
    }else{loginPanel.classList.remove('hidden');connectionState.textContent='等待总控登录';}
  }catch{loginPanel.classList.remove('hidden');connectionState.textContent='无法读取服务端状态';}
}

$('#setupForm').addEventListener('submit',async event=>{
  event.preventDefault();setupError.textContent='';
  const body={playerPassword:$('#setupPlayerPassword').value,adminPassword:$('#setupAdminPassword').value,sessionName:value('#setupSessionName'),totalRaceLaps:numberValue('#setupRaceLaps'),sectorCount:numberValue('#setupSectorCount')};
  if(body.playerPassword===body.adminPassword){setupError.textContent='房间密码和总控密码不能相同。';return;}
  const response=await request('/api/setup',body);
  if(!response.ok){setupError.textContent=(await safeJson(response))?.error??'首次设置未能保存。';return;}
  setupPanel.classList.add('hidden');loginPanel.classList.remove('hidden');connectionState.textContent='设置完成，请登录总控';$('#adminPassword').focus();
});

$('#loginForm').addEventListener('submit',async event=>{
  event.preventDefault();loginError.textContent='';
  const response=await request('/api/admin/login',{password:$('#adminPassword').value});
  if(!response.ok){loginError.textContent=(await safeJson(response))?.error??'登录失败。';return;}
  loginPanel.classList.add('hidden');dashboard.classList.remove('hidden');connectionState.textContent='总控已登录';
  await loadSettings();await refresh();polling=window.setInterval(refresh,500);
});

$('#logoutButton').addEventListener('click',async()=>{await fetch('/api/admin/logout',{method:'POST'});if(polling)clearInterval(polling);dashboard.classList.add('hidden');loginPanel.classList.remove('hidden');connectionState.textContent='等待总控登录';});
$('#saveSettings').addEventListener('click',saveSettings);
document.querySelectorAll('[data-session]').forEach(button=>button.addEventListener('click',()=>post('/api/admin/session',{phase:button.dataset.session,sessionName:null,totalRaceLaps:null,countdownSeconds:numberValue('#countdownSeconds'),qualifyingMinutes:numberValue('#qualifyingMinutes')})));
document.querySelectorAll('[data-flag]').forEach(button=>button.addEventListener('click',()=>{
  const selected=$('#flagSector').value;
  post('/api/admin/flag',{flag:button.dataset.flag,message:value('#flagReason')||null,sectorIndex:button.dataset.flag==='red'||selected==='all'?null:Number.parseInt(selected,10)});
}));

async function loadSettings(){const response=await fetch('/api/admin/settings',{cache:'no-store'});if(!response.ok)return;const settings=await response.json();$('#sessionName').value=settings.sessionName;$('#raceLaps').value=settings.totalRaceLaps;$('#sectorCount').value=settings.sectorCount;$('#automaticYellow').checked=settings.automaticYellowEnabled;$('#allowTeams').checked=settings.allowTeams!==false;$('#trackName').value=settings.trackName??'';$('#trackId').value=settings.trackId??'';$('#trackPackageHash').value=settings.trackPackageHash??'';$('#slowSpeedKph').value=settings.slowSpeedKph;$('#slowDurationSeconds').value=settings.slowDurationSeconds;$('#severeLateralOffsetMeters').value=settings.severeLateralOffsetMeters;$('#recoveryDurationSeconds').value=settings.recoveryDurationSeconds;rebuildSectorOptions(settings.sectorCount);}
async function saveSettings(){const body={sessionName:value('#sessionName'),totalRaceLaps:numberValue('#raceLaps'),sectorCount:numberValue('#sectorCount'),automaticYellowEnabled:$('#automaticYellow').checked,slowSpeedKph:numberValue('#slowSpeedKph'),slowDurationSeconds:numberValue('#slowDurationSeconds'),severeLateralOffsetMeters:numberValue('#severeLateralOffsetMeters'),recoveryDurationSeconds:numberValue('#recoveryDurationSeconds'),allowTeams:$('#allowTeams').checked,trackName:value('#trackName')||null,trackId:value('#trackId')||null,trackRevision:null,trackPackageHash:value('#trackPackageHash')||null};const response=await request('/api/admin/settings',body);const indicator=$('#settingsSaved');if(!response.ok){indicator.textContent=(await safeJson(response))?.error??'保存失败';indicator.className='save-state error';return;}indicator.textContent='已保存';indicator.className='save-state success';rebuildSectorOptions(body.sectorCount);setTimeout(()=>indicator.textContent='',2500);await refresh();}
function rebuildSectorOptions(count){const select=$('#flagSector'),selected=select.value;select.replaceChildren(new Option('全场','all'));for(let index=0;index<count;index++)select.append(new Option(`第 ${index+1} 分段`,String(index)));select.value=[...select.options].some(option=>option.value===selected)?selected:'all';}

async function refresh(){const response=await fetch('/api/admin/state',{cache:'no-store'});if(response.status===401){if(polling)clearInterval(polling);dashboard.classList.add('hidden');loginPanel.classList.remove('hidden');connectionState.textContent='登录已过期';return;}if(!response.ok){connectionState.textContent='服务端状态读取失败';return;}connectionState.textContent=`总控在线 · ${new Date().toLocaleTimeString()}`;render(await response.json());}
function render(state){text('#phaseValue',phaseLabels[state.phase]??state.phase);const remaining=state.phase==='qualifying'&&state.qualifyingEndsAt?Math.max(0,Math.ceil((Date.parse(state.qualifyingEndsAt)-Date.now())/1000)):null;text('#sessionValue',remaining===null?`${state.sessionName} · ${state.totalRaceLaps} 圈`:`${state.sessionName} · 剩余 ${Math.floor(remaining/60)}:${String(remaining%60).padStart(2,'0')}`);const flagCard=$('#flagCard');flagCard.className=`panel summary-card flag-card ${state.flag}`;text('#flagValue',flagLabels[state.flag]??state.flag.toUpperCase());text('#flagMessage',state.flagMessage||(state.flag==='green'?'赛道正常':'等待说明'));const online=state.participants.filter(item=>item.isConnected).length,ready=state.participants.filter(item=>item.isReady).length;text('#onlineValue',online);text('#readyValue',`${ready} 人已准备`);text('#fastestValue',formatTime(state.fastestLapSeconds));const fastest=state.participants.find(item=>item.id===state.fastestParticipantId);text('#fastestDriver',fastest?.displayName??'尚无有效圈');renderBanner(state.banner);renderZones(state.yellowZones??[]);renderParticipants(state.participants,state.allowTeams!==false);}
function renderBanner(banner){const element=$('#raceBanner');if(!banner){element.classList.add('hidden');return;}element.classList.remove('hidden');element.dataset.kind=banner.kind;text('#bannerKind',banner.kind.replace(/([A-Z])/g,' $1').toUpperCase());text('#bannerTitle',banner.title);text('#bannerDetail',banner.detail??'');}
function renderZones(zones){const root=$('#yellowZones');root.replaceChildren();if(zones.length===0){const empty=document.createElement('span');empty.className='zone-empty';empty.textContent='当前没有黄旗分区';root.append(empty);return;}for(const zone of zones){const item=document.createElement('span');item.className=`zone-pill ${zone.isAutomatic?'automatic':'manual'}`;item.textContent=`${zone.sectorIndex===null?'全场':`S${zone.sectorIndex+1}`} · ${zone.isAutomatic?'自动':'人工'} · ${zone.participantName??zone.reason}`;item.title=zone.reason;root.append(item);}}
function renderParticipants(participants,allowTeams){timingRows.replaceChildren();for(const participant of participants){const row=document.createElement('tr');row.append(cell(participant.position,'position'));const driver=document.createElement('td'),driverWrap=document.createElement('div');driverWrap.className='driver-cell';const color=document.createElement('span');color.className='driver-color';color.style.backgroundColor=participant.themeColor;const names=document.createElement('div'),name=document.createElement('div');name.className='driver-name';name.textContent=participant.displayName;names.append(name);if(allowTeams&&participant.teamName){const team=document.createElement('div');team.className='team-name';team.textContent=participant.teamName;names.append(team);}driverWrap.append(color,names);driver.append(driverWrap);row.append(driver);const statusCell=document.createElement('td'),status=document.createElement('span');status.className=`status ${participant.isInPitLane?'pit':''}`;status.textContent=participantStatusText(participant);statusCell.append(status);row.append(statusCell);row.append(cell(participant.completedLaps));row.append(cell(formatTime(participant.currentLapSeconds)));row.append(cell(formatTime(participant.bestLapSeconds),'best-lap'));row.append(cell(formatGap(participant.gapToLeaderSeconds)));row.append(actionsCell(participant));timingRows.append(row);}}
function participantStatusText(participant){if(participant.isInServiceZone){if(participant.pitServiceRequirementMet)return`维修停留完成 · ${participant.completedPitServices} 次`;if(participant.pitServiceElapsedSeconds>0)return`维修停留 ${participant.pitServiceElapsedSeconds.toFixed(1)} 秒`;}return statusLabels[participant.status]??participant.status;}
function actionsCell(participant){const td=document.createElement('td'),wrap=document.createElement('div');wrap.className='actions';wrap.append(actionButton('警告',()=>penalty(participant,'warning')),actionButton('+5秒',()=>penalty(participant,'time',5)),actionButton('通过维修区',()=>penalty(participant,'driveThrough')),actionButton('退赛',()=>participantStatus(participant,'didNotFinish')),actionButton('取消资格',()=>penalty(participant,'disqualification')));td.append(wrap);return td;}
async function penalty(participant,kind,valueSeconds=null){const reason=prompt(`处罚 ${participant.displayName} 的原因：`);if(reason)await post('/api/admin/penalty',{participantId:participant.id,kind,valueSeconds,gridPlaces:null,reason});}
async function participantStatus(participant,status){const reason=prompt(`将 ${participant.displayName} 标记为${statusLabels[status]}，填写原因：`);if(reason)await post('/api/admin/participant',{participantId:participant.id,status,reason});}
async function post(url,body){actionError.textContent='';const response=await request(url,body);if(!response.ok){actionError.textContent=(await safeJson(response))?.error??`操作失败：HTTP ${response.status}`;return;}await refresh();}
function request(url,body){return fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});}
function actionButton(label,handler){const button=document.createElement('button');button.type='button';button.textContent=label;button.addEventListener('click',handler);return button;}
function cell(content,className=''){const td=document.createElement('td');td.className=className;td.textContent=content??'—';return td;}
function formatTime(seconds){if(seconds===null||seconds===undefined||!Number.isFinite(seconds)||seconds<=0)return'—';const minutes=Math.floor(seconds/60);return`${minutes}:${(seconds%60).toFixed(3).padStart(6,'0')}`;}
function formatGap(seconds){if(seconds===null||seconds===undefined||!Number.isFinite(seconds))return'—';return seconds<.0005?'LEADER':`+${seconds.toFixed(3)}`;}
function text(selector,valueToSet){$(selector).textContent=valueToSet;}function value(selector){return $(selector).value.trim();}function numberValue(selector){return Number.parseFloat($(selector).value);}async function safeJson(response){try{return await response.json();}catch{return null;}}
