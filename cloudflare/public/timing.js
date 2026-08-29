const $=selector=>document.querySelector(selector);
const raceI18n=window.RaceI18n??{t:value=>value,locale:'zh-CN',isEnglish:false};
const tr=value=>raceI18n.t(value),raceLocale=raceI18n.locale;
const phaseLabels={lobby:'大厅',practice:'练习赛',qualifying:'排位赛',grid:'发车区',outLap:'出场圈',formationLap:'暖胎圈',countdown:'五盏红灯',race:'正赛',suspended:'比赛暂停',finished:'比赛结束'};
const statusLabels={connected:'已连接',ready:'已准备',onTrack:'赛道上',inPitLane:'维修区通道',inService:'换胎区内',finished:'已完赛',didNotFinish:'退赛',disqualified:'取消资格',disconnected:'已掉线'};
const penaltyLabels={warning:'警告',time:'罚时',driveThrough:'通过维修区',stopAndGo:'停车罚时',gridDrop:'发车位罚退',disqualification:'取消资格'};
const flagLabels={green:'GREEN',yellow:'YELLOW',red:'RED',chequered:'CHEQUERED'};
const hash=new URLSearchParams(location.hash.slice(1));
const token=hash.get('token')??(!location.hash.includes('=')?location.hash.slice(1):'');
const overlay=new URLSearchParams(location.search).get('overlay')==='1';
let polling=null,refreshing=false,lastResultKey='';

if(overlay)document.body.classList.add('overlay');
initialize();

function initialize(){
  if(!token){showAccessError('链接缺少只读计时令牌。请向赛事主办方获取新的公开链接。');return;}
  refresh();
  polling=window.setInterval(refresh,1000);
  window.addEventListener('online',refresh);
}

async function refresh(){
  if(refreshing)return;refreshing=true;
  try{
    const response=await fetch('/api/public/timing',{cache:'no-store',headers:{Authorization:`Bearer ${token}`}});
    if(response.status===401){if(polling)clearInterval(polling);polling=null;showAccessError('只读计时链接无效或已被赛事主办方停用。');return;}
    if(!response.ok)throw new Error();
    const payload=await response.json(),state=payload.state??{},results=Array.isArray(payload.results)?payload.results:[];
    hideAccessError();setConnection('online','实时数据已连接');
    const resultKey=results.map(item=>`${item.completedAt}:${item.participants?.length??0}`).join('|');
    renderState(state);
    if(resultKey!==lastResultKey){renderResults(results);lastResultKey=resultKey;}
    $('#lastUpdated').textContent=`${tr('更新于')} ${new Date().toLocaleTimeString(raceLocale)}`;
  }catch{setConnection('error','连接暂时中断，正在重试');}
  finally{refreshing=false;}
}

function renderState(state){
  const activePhase=state.phase==='suspended'?state.suspendedFromPhase:state.phase;
  const phase=sessionPhaseLabel(state,activePhase);
  $('#sessionName').textContent=state.sessionName??tr('公开实时计时');
  $('#trackName').textContent=state.trackName??tr('未设置赛道名称');
  $('#phaseValue').textContent=phase;
  $('#phaseDetail').textContent=phaseDetail(state,activePhase);
  const flag=state.flag??'green',flagSummary=$('#flagSummary');
  flagSummary.className=`summary flag ${flag}`;$('#flagValue').textContent=flagLabels[flag]??String(flag).toUpperCase();
  $('#flagMessage').textContent=state.flagMessage||flagDefault(flag,state.yellowZones??[]);
  $('#fastestLap').textContent=formatLap(state.fastestLapSeconds);
  $('#fastestDriver').textContent=state.fastestDriverName??tr('尚无有效圈');
  const participants=Array.isArray(state.participants)?state.participants:[];
  $('#driverCount').textContent=String(participants.length);
  $('#leaderDetail').textContent=participants[0]?`${tr('领先者')} · ${participants[0].displayName}`:tr('等待排名');
  renderTiming(participants,state.minimumRequiredPitStops??1);
}

function sessionPhaseLabel(state,activePhase){
  if(activePhase==='practice'&&(state.practiceSessionCount??1)>1)return`FP${state.practiceSessionNumber}/${state.practiceSessionCount}`;
  if(activePhase==='qualifying'&&(state.qualifyingSessionCount??1)>1)return`Q${state.qualifyingSessionNumber}/${state.qualifyingSessionCount}`;
  const active=tr(phaseLabels[activePhase]??activePhase??'—');
  return state.phase==='suspended'?`${tr('比赛暂停')} · ${active}`:active;
}

function phaseDetail(state,activePhase){
  const endsAt=activePhase==='practice'?state.practiceEndsAt:activePhase==='qualifying'?state.qualifyingEndsAt:null;
  if(endsAt){const seconds=Math.max(0,Math.ceil((Date.parse(endsAt)-Date.now())/1000));return`${tr('剩余')} ${Math.floor(seconds/60)}:${String(seconds%60).padStart(2,'0')}`;}
  if(activePhase==='race'||state.phase==='finished')return`${state.totalRaceLaps??0} ${tr('圈')} · ${formatRaceTime(state.raceElapsedSeconds)}`;
  return`${state.totalRaceLaps??0} ${tr('圈')}`;
}

function flagDefault(flag,zones){
  if(flag==='green')return tr('赛道正常');
  if(flag==='red')return tr('比赛已暂停');
  if(flag==='chequered')return tr('方格旗');
  return zones.length?`${zones.length} ${tr('个黄旗区域')}`:tr('黄旗');
}

function renderTiming(participants,requiredPitStops){
  const rows=$('#timingRows'),cards=$('#timingCards');rows.replaceChildren();cards.replaceChildren();
  if(!participants.length){const row=document.createElement('tr'),cell=document.createElement('td');cell.colSpan=8;cell.className='empty';cell.textContent=tr('等待参赛车手');row.append(cell);rows.append(row);cards.append(emptyElement('等待参赛车手'));return;}
  const leader=participants[0];
  for(const participant of participants){rows.append(timingRow(participant,leader,requiredPitStops));cards.append(timingCard(participant,leader,requiredPitStops));}
}

function timingRow(item,leader,requiredPitStops){
  const row=document.createElement('tr');row.style.setProperty('--driver-color',safeColor(item.themeColor));
  row.append(cell(String(item.position)),driverCell(item),cell(String(item.completedLaps)),cell(deltaText(item,leader),'delta'),cell(formatLap(item.currentLapSeconds)),cell(formatLap(item.bestLapSeconds),'best'),cell(pitText(item,requiredPitStops),pitActive(item)?'pit-active':''),cell(penaltyText(item),hasPenalty(item)?'penalty-active':''));
  return row;
}

function timingCard(item,leader,requiredPitStops){
  const card=document.createElement('article');card.className='driver-card';card.style.setProperty('--driver-color',safeColor(item.themeColor));
  const head=document.createElement('div');head.className='driver-card-head';const position=document.createElement('span');position.className='driver-position';position.textContent=String(item.position);const name=driverName(item),delta=document.createElement('span');delta.className='delta';delta.textContent=deltaText(item,leader);head.append(position,name,delta);
  const stats=document.createElement('div');stats.className='driver-card-stats';[['圈数',String(item.completedLaps)],['当前圈',formatLap(item.currentLapSeconds)],['最佳圈',formatLap(item.bestLapSeconds)],['维修',pitText(item,requiredPitStops)],['处罚',penaltyText(item)]].forEach(([label,value])=>{const block=document.createElement('span'),strong=document.createElement('strong');block.append(document.createTextNode(tr(label)));strong.textContent=value;block.append(strong);stats.append(block);});
  const note=document.createElement('p');note.className='driver-card-note';note.textContent=tr(statusLabels[item.status]??item.status??'—');card.append(head,stats,note);return card;
}

function driverCell(item){const wrapper=document.createElement('td');wrapper.append(driverName(item));return wrapper;}
function driverName(item){const wrapper=document.createElement('span');wrapper.className='driver-name';const strong=document.createElement('strong'),small=document.createElement('small');strong.textContent=item.displayName;small.textContent=[item.teamName,tr(statusLabels[item.status]??item.status)].filter(Boolean).join(' · ');wrapper.append(strong,small);return wrapper;}
function cell(value,className=''){const element=document.createElement('td');element.textContent=value;if(className)element.className=className;return element;}

function deltaText(item,leader){if(item.position===leader.position)return tr('领先');const lapDifference=(leader.completedLaps??0)-(item.completedLaps??0);if(lapDifference>0)return`+${lapDifference} ${tr('圈')}`;const value=item.gapToLeaderSeconds??item.intervalSeconds;return Number.isFinite(value)?`+${Number(value).toFixed(3)}`:'—';}
function pitActive(item){return item.isInPitLane||item.isInServiceZone||item.isServingTimePenalty||item.isServingDriveThrough;}
function pitText(item,required){if(item.isServingDriveThrough)return tr('执行通过维修区');if(item.isServingTimePenalty)return`${tr('执行罚停')} ${Number(item.pitServiceElapsedSeconds??0).toFixed(1)}s`;if(item.isInServiceZone)return`${tr('维修中')} ${Number(item.pitServiceElapsedSeconds??0).toFixed(1)}s`;if(item.isInPitLane)return tr('维修区内');return`${item.completedPitServices??0}/${required}`;}
function hasPenalty(item){return activePenalties(item).length>0||(item.pendingTimePenaltySeconds??0)>0||item.hasPendingDriveThrough||item.driveThroughOverdue;}
function penaltyText(item){const active=activePenalties(item);if(item.driveThroughOverdue)return tr('通过维修区已逾期');if(item.hasPendingDriveThrough)return tr('待执行通过维修区');if((item.pendingTimePenaltySeconds??0)>0)return`+${Number(item.pendingTimePenaltySeconds).toFixed(0)}s ${tr('待执行')}`;if(active.length){const first=active[0];return[first.valueSeconds?`+${Number(first.valueSeconds).toFixed(0)}s`:tr(penaltyLabels[first.kind]??first.kind),first.reason].filter(Boolean).join(' · ');}return tr('无');}
function activePenalties(item){return(Array.isArray(item.penalties)?item.penalties:[]).filter(penalty=>!penalty.isRevoked&&!penalty.isServed);}

function renderResults(results){
  const root=$('#resultHistory');root.replaceChildren();$('#resultCount').textContent=`${results.length} ${tr('份赛果')}`;
  if(!results.length){root.append(emptyElement('尚无阶段赛果'));return;}
  for(const result of [...results].sort((left,right)=>Date.parse(right.completedAt)-Date.parse(left.completedAt))){
    const card=document.createElement('article');card.className='result-card';const header=document.createElement('header'),title=document.createElement('strong'),meta=document.createElement('span');title.textContent=result.label||tr(phaseLabels[result.phase]??result.phase);meta.textContent=`${new Date(result.completedAt).toLocaleString(raceLocale,{hour12:false})}${result.fastestDriverName?` · ${tr('最快圈')} ${result.fastestDriverName} ${formatLap(result.fastestLapSeconds)}`:''}`;header.append(title,meta);
    const table=document.createElement('table');table.className='result-table';const head=document.createElement('thead');head.innerHTML=`<tr><th>${tr('排名')}</th><th>${tr('车手')}</th><th>${tr('圈数')}</th><th>${tr('最佳圈')}</th><th>${tr('总时间')}</th><th>${tr('罚时')}</th></tr>`;table.append(head);const body=document.createElement('tbody');for(const item of result.participants??[]){const row=document.createElement('tr');row.append(resultCell(String(item.position),'排名'),resultCell(item.displayName,'车手'),resultCell(String(item.completedLaps),'圈数'),resultCell(formatLap(item.bestLapSeconds),'最佳圈'),resultCell(formatRaceTime(item.adjustedRaceTotalSeconds??item.raceTotalSeconds),'总时间'),resultCell(item.timePenaltySeconds>0?`+${Number(item.timePenaltySeconds).toFixed(0)}s`:'—','罚时'));body.append(row);}table.append(body);card.append(header,table);root.append(card);
  }
}

function resultCell(value,label){const element=cell(value);element.dataset.label=tr(label);return element;}
function emptyElement(message){const element=document.createElement('p');element.className='empty';element.textContent=tr(message);return element;}
function formatLap(seconds){if(!Number.isFinite(seconds)||seconds<=0)return'—';const minutes=Math.floor(seconds/60);return`${minutes}:${(seconds-minutes*60).toFixed(3).padStart(6,'0')}`;}
function formatRaceTime(seconds){if(!Number.isFinite(seconds)||seconds<0)return'—';const whole=Math.floor(seconds),hours=Math.floor(whole/3600),minutes=Math.floor((whole%3600)/60),rest=whole%60,milliseconds=Math.round((seconds-whole)*1000);return hours>0?`${hours}:${String(minutes).padStart(2,'0')}:${String(rest).padStart(2,'0')}.${String(milliseconds).padStart(3,'0')}`:`${minutes}:${String(rest).padStart(2,'0')}.${String(milliseconds).padStart(3,'0')}`;}
function safeColor(value){return/^#[0-9a-f]{6}$/i.test(value??'')?value:'#42D7E8';}
function setConnection(kind,message){const state=$('#connectionState');state.className=`connection-state ${kind}`;state.querySelector('span').textContent=tr(message);}
function showAccessError(message){$('#accessError').textContent=tr(message);$('#accessError').classList.remove('hidden');setConnection('error','无法读取计时');}
function hideAccessError(){$('#accessError').classList.add('hidden');}
