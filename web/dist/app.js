import {openBook,typeOf} from './formats.js';

const $=id=>document.getElementById(id);
const stage=$('stage'), deck=$('deck'), stream=$('stream'), progress=$('progress');
const safe=(operation)=>operation.catch(error=>notify(error.message||String(error)));
const request=req=>new Promise((yes,no)=>{req.onsuccess=()=>yes(req.result);req.onerror=()=>no(req.error);});
const storage=new Promise((yes,no)=>{
  const db=indexedDB.open('komasnap-web',1);
  db.onupgradeneeded=()=>db.result.createObjectStore('books',{keyPath:'id'});
  db.onsuccess=()=>yes(db.result);
  db.onerror=()=>no(new Error('此浏览器无法打开本地书架。'));
});
async function read(method,...args) {
  const db=await storage;
  return request(db.transaction('books').objectStore('books')[method](...args));
}
async function write(operation) {
  const db=await storage, tx=db.transaction('books','readwrite'), store=tx.objectStore('books');
  return new Promise((yes,no)=>{
    tx.oncomplete=yes; tx.onerror=()=>no(tx.error); tx.onabort=()=>no(tx.error);
    operation(store);
  });
}
const patch=(id,data)=>write(store=>{
  const row=store.get(id);
  row.onsuccess=()=>{if(row.result)store.put({...row.result,...data});};
});
const cfg={mode:'slide',dual:true,rtl:true,fit:false};
try {
  const value=JSON.parse(localStorage.getItem('komasnap-options'))||{};
  for (const key of ['dual','rtl','fit']) if(typeof value[key]==='boolean')cfg[key]=value[key];
  if (['slide','curl','scroll'].includes(value.mode))cfg.mode=value.mode;
} catch {}
const saveOptions=()=>{try{localStorage.setItem('komasnap-options',JSON.stringify(cfg));}catch{}};
let rows=[], coverUrls=[], current=null, openToken=0, removal=null, noticeTimer, saveTimer, shelfVersion=0;
const nameSort=new Intl.Collator('zh-Hans',{numeric:true});

function notify(text) {
  $('notice').textContent=text; $('notice').hidden=!text; clearTimeout(noticeTimer);
  if(text)noticeTimer=setTimeout(()=>{$('notice').hidden=true;},4400);
}
function wait(text) { $('waiting').textContent=text; $('waiting').hidden=!text; }
const element=(tag,className,text)=>Object.assign(document.createElement(tag),{className,textContent:text??''});
function drawShelf() {
  for(const url of coverUrls) URL.revokeObjectURL(url);
  coverUrls=[];
  const query=$('search').value.trim().toLowerCase(), order=$('sort').value;
  const list=rows.filter(row=>row.name.toLowerCase().includes(query)).sort((a,b)=>
    order==='name'?nameSort.compare(a.name,b.name):order==='added'?b.added-a.added:
      (b.recent||0)-(a.recent||0)||b.added-a.added);
  $('count').textContent=rows.length+' 本书';
  $('shelf-tools').hidden=!rows.length;
  $('empty').hidden=!!rows.length;
  $('no-match').hidden=!rows.length||!!list.length;
  $('books').replaceChildren(...list.map(row=>{
    const card=element('article','card'), button=element('button','open-card');
    button.type='button';button.title=row.name;
    const cover=element('div','cover');
    if(row.cover){
      const img=new Image();img.alt='';img.loading='lazy';
      img.src=URL.createObjectURL(row.cover);coverUrls.push(img.src);cover.append(img);
    }else cover.textContent=row.type.toUpperCase();
    button.append(cover,element('span','card-title',row.name),
      element('span','card-meta',row.count?('第 '+((row.page??0)+1)+' / '+row.count+' '+(row.fixed===false?'章':'页')):'未读 · '+row.type.toUpperCase()));
    button.onclick=()=>safe(start(row));
    const menu=element('button','card-remove','···');menu.type='button';menu.setAttribute('aria-label','移出 '+row.name);
    menu.onclick=()=>{removal=row.id;$('remove-label').textContent='将《'+row.name+'》从此浏览器的书架移出。';$('remove').returnValue='';$('remove').showModal();};
    card.append(button,menu);return card;
  }));
}
async function refreshShelf() {
  const version=++shelfVersion;
  const result=await read('getAll');
  if(version===shelfVersion){rows=result;drawShelf();}
}
async function importFiles(files) {
  const known=new Set((await read('getAll')).map(row=>row.id));
  let added=0, rejected=0;
  const fresh=[];
  for(const file of files) {
    const type=typeOf(file.name);
    if(!type||!file.size){rejected++;continue;}
    const id=JSON.stringify([file.name,file.size,file.lastModified]);
    if(known.has(id))continue;
    known.add(id);
    fresh.push({id,type,name:file.name.replace(/\.[^.]+$/,''),blob:file,added:Date.now()+(added++),page:0});
  }
  if(fresh.length)await write(store=>fresh.forEach(row=>store.put(row)));
  await refreshShelf();
  notify(added?'已添加 '+added+' 本书'+(rejected?'，其余文件格式不受支持。':'。'):
    rejected?'请选择 PDF、EPUB、CBZ 或图片 ZIP。':'所选书籍已在书架中。');
  for(const row of fresh)queueCover(row);
}
let covers=Promise.resolve(), coverCount=0;
function queueCover(row) {
  if(coverCount>=24)return;
  ++coverCount;
  covers=covers.then(async()=>{
    let model;
    try {
      model=await openBook(row.blob,row.type);
      const surface=await model.render(0,240);
      const cover=surface instanceof HTMLCanvasElement?await new Promise(yes=>surface.toBlob(yes,'image/jpeg',.82)):null;
      await patch(row.id,{count:model.count,fixed:model.fixed,cover});
      if(!current)await refreshShelf();
    }catch{}finally{--coverCount;await model?.close();}
  }).catch(()=>{});
}

$('add').onclick=$('empty-add').onclick=()=>$('files').click();
$('files').onchange=()=>{const files=[...$('files').files];$('files').value='';if(files.length)safe(importFiles(files));};
$('search').oninput=drawShelf;$('sort').onchange=drawShelf;
$('remove').onclose=()=>{if($('remove').returnValue==='remove'&&removal)safe(write(store=>store.delete(removal)).then(refreshShelf));};
document.addEventListener('dragover',event=>{
  if(!event.dataTransfer.types.includes('Files'))return;
  event.preventDefault();document.body.classList.add('dropping');
});
document.addEventListener('dragleave',event=>{if(!event.relatedTarget)document.body.classList.remove('dropping');});
document.addEventListener('drop',event=>{
  if(!event.dataTransfer.files.length)return;
  event.preventDefault();document.body.classList.remove('dropping');safe(importFiles([...event.dataTransfer.files]));
});

function flush() {
  clearTimeout(saveTimer);
  if(!current?.model)return Promise.resolve();
  return patch(current.row.id,{page:current.page,recent:Date.now(),count:current.model.count,fixed:current.model.fixed});
}
function remember() {clearTimeout(saveTimer);saveTimer=setTimeout(()=>safe(flush()),220);}
window.addEventListener('pagehide',()=>{void flush().catch(()=>{});});
document.addEventListener('visibilitychange',()=>{if(document.hidden)void flush().catch(()=>{});});

async function start(row) {
  if(current) await leave();
  const serial=++openToken;
  const session={row, page:row.page??0,target:row.page??0,zoom:1,sequence:0,cache:new Map(),active:0,jobs:[],cacheBytes:0};
  current=session;
  $('shelf').hidden=true;$('reader').hidden=false;
  $('title').textContent=row.name;progress.hidden=true;
  notify('');wait('正在打开…');
  try {
    const model=await openBook(row.blob,row.type);
    if(serial!==openToken){await model.close();return;}
    session.model=model;session.rtl=model.rtl??cfg.rtl;
    session.page=session.target=Math.max(0,Math.min(model.count-1,session.page));
    updateControls();
    await showMode();
    if(current===session)remember();
    stage.focus({preventScroll:true});
  }catch(error){if(current===session)wait('无法打开文件。'+(error.message||''));}
}
async function leave() {
  ++openToken;
  const owner=current;
  const saving=flush();
  current=null;
  if(owner){owner.observer?.disconnect();owner.jobs.splice(0).forEach(job=>job.reject(new Error('阅读已结束。')));owner.cache.clear();}
  stopMotion();deck.replaceChildren();stream.replaceChildren();
  if(document.fullscreenElement)await document.exitFullscreen().catch(()=>{});
  $('reader').hidden=true;$('shelf').hidden=false;
  await saving;await owner?.model?.close();await refreshShelf();
}
function group(session,page) {
  if(!session.model.fixed||!cfg.dual||page===0)return [page];
  const first=1+2*Math.floor((page-1)/2);
  return first+1<session.model.count?[first,first+1]:[first];
}
const clamp=(p,n)=>Math.max(0,Math.min(n-1,p));
const bucket=w=>Math.min(2560,Math.max(128,Math.ceil(w/128)*128));
const density=()=>Math.min(devicePixelRatio||1,1.8);

function pendingJobs(owner){
  while(owner.active<2&&owner.jobs.length){
    const job=owner.jobs.shift();
    if(current!==owner||!job.valid()){job.reject(new Error('页面请求已更新。'));continue;}
    ++owner.active;
    Promise.resolve().then(job.run).then(job.resolve,job.reject).finally(()=>{--owner.active;pendingJobs(owner);});
  }
}
function surface(owner,page,width,priority=true,valid=()=>true) {
  const key=page+'/'+width;
  const existing=owner.cache.get(key);
  if(existing){
    owner.cache.delete(key);owner.cache.set(key,existing);
    if(priority&&existing.job&&owner.jobs.includes(existing.job)){
      const i=owner.jobs.indexOf(existing.job);owner.jobs.splice(i,1);
      existing.job.valid=()=>true;owner.jobs.unshift(existing.job);
    }
    return existing.promise;
  }
  const entry={cost:0};
  entry.promise=new Promise((resolve,reject)=>{
    const job={run:()=>owner.model.render(page,width),resolve,reject,valid};
    entry.job=job;
    priority?owner.jobs.unshift(job):owner.jobs.push(job);
  }).then(value=>{
    if(current!==owner)return value;
    entry.job=null;
    entry.cost=value instanceof HTMLCanvasElement?value.width*value.height*4:512000;
    owner.cacheBytes+=entry.cost;
    for(const [oldKey,old] of owner.cache){
      if(owner.cacheBytes<=96*1024*1024&&owner.cache.size<=20)break;
      if(oldKey===key||old.job)continue;
      owner.cache.delete(oldKey);owner.cacheBytes-=old.cost;
    }
    return value;
  },error=>{owner.cache.delete(key);throw error;});
  owner.cache.set(key,entry);pendingJobs(owner);
  return entry.promise;
}
function cloneSurface(value,page) {
  if(!(value instanceof HTMLCanvasElement))return value.cloneNode(true);
  const canvas=document.createElement('canvas');canvas.className='paper';canvas.width=value.width;canvas.height=value.height;
  canvas.setAttribute('role','img');canvas.setAttribute('aria-label','第 '+(page+1)+' 页');
  canvas.getContext('2d').drawImage(value,0,0);
  return canvas;
}
async function spreadData(owner,page,priority=true,valid=()=>true) {
  const pages=group(owner,page);
  const ratios=await Promise.all(pages.map(p=>owner.model.ratio(p)));
  if(!valid()&&!priority)throw new Error('页面请求已更新。');
  const sum=ratios.reduce((a,b)=>a+b,0)||1;
  const fitHeight=Math.max(100,stage.clientHeight-26);
  const size=(cfg.fit?Math.max(160,stage.clientWidth-28)/sum:
    Math.min(fitHeight,Math.max(160,stage.clientWidth-28)/sum))*owner.zoom;
  const sizes=ratios.map(r=>r?Math.max(32,size*r):640);
  const values=await Promise.all(pages.map((p,i)=>surface(owner,p,bucket(sizes[i]*density()),priority,valid)));
  return {pages,ratios,sizes,values};
}
const reduced=()=>matchMedia('(prefers-reduced-motion: reduce)').matches;
let motions=[];
function stopMotion() {
  for(const motion of motions)motion.cancel();
  motions=[];deck.querySelectorAll('.outgoing').forEach(el=>el.remove());
  const active=deck.querySelector('.spread');if(active)active.style.transform='';
}
function animateSpread(old,next,direction,owner,dragX) {
  if(!old||!direction||reduced()||!next.animate)return;
  const physical=direction*(owner.rtl?1:-1);
  const travel=Math.max(200,Math.min(stage.clientWidth,(old.scrollWidth+next.scrollWidth)/2));
  old.classList.add('outgoing');
  const curl=cfg.mode==='curl';
  const ease={duration:curl?310:225,easing:'cubic-bezier(.18,.74,.25,1)',fill:'both'};
  const curlOrigin=physical>0?'right center':'left center';
  const outgoing=curl?[
    {transform:'perspective(1400px) rotateY(0deg)',transformOrigin:curlOrigin,opacity:1},
    {transform:`perspective(1400px) rotateY(${physical*-83}deg) translateX(${physical*18}px)`,transformOrigin:curlOrigin,opacity:0}
  ]:[{transform:`translate3d(${dragX}px,0,0)`},{transform:`translate3d(${physical*travel}px,0,0)`,opacity:.45}];
  const incoming=curl?[{opacity:.7},{opacity:1}]:[
    {transform:`translate3d(${dragX-physical*travel}px,0,0)`},{transform:'translate3d(0,0,0)'}];
  const pair=[old.animate(outgoing,ease),next.animate(incoming,ease)];motions=pair;
  Promise.allSettled(pair.map(a=>a.finished)).then(()=>{
    if(motions!==pair)return;
    old.remove();pair.forEach(a=>a.cancel());motions=[];
  });
}
async function showPage(owner,p,direction=0,dragX=0) {
  const page=clamp(p,owner.model.count),seq=++owner.sequence;
  owner.target=page;
  owner.viewWidth=stage.clientWidth;owner.viewHeight=stage.clientHeight;
  const valid=()=>current===owner&&owner.sequence===seq&&cfg.mode!=='scroll';
  const timer=setTimeout(()=>{if(valid())wait('正在加载页面…');},160);
  try {
    const data=await spreadData(owner,page,true,valid);
    if(!valid())return;
    stopMotion();
    const old=deck.querySelector('.spread');
    const next=element('div','spread');
    const order=data.pages.map((_,i)=>i);
    if(owner.rtl&&data.pages.length>1)order.reverse();
    for(const i of order){
      const node=cloneSurface(data.values[i],data.pages[i]);
      if(data.ratios[i])node.style.width=data.sizes[i]+'px';
      next.append(node);
    }
    deck.append(next);deck.scrollTop=0;deck.scrollLeft=owner.rtl?deck.scrollWidth:0;
    if(old){
      if(direction&&!reduced()&&next.animate)animateSpread(old,next,direction,owner,dragX);
      else old.remove();
    }
    wait('');owner.page=page;updateProgress();remember();
    if(!owner.row.cover&&page===0&&data.values[0] instanceof HTMLCanvasElement) saveCover(owner,data.values[0]);
    const nextStep=direction||1;
    const neighbor=nextStep>0?data.pages.at(-1)+1:data.pages[0]-1;
    const opposite=nextStep>0?data.pages[0]-1:data.pages.at(-1)+1;
    const prefetch=async()=>{
      for(const index of [neighbor,opposite]){
        if(!valid())break;
        if(index>=0&&index<owner.model.count)await spreadData(owner,index,false,valid).catch(()=>{});
      }
    };
    setTimeout(prefetch,80);
  }catch(error){if(valid())wait('无法显示当前页面。'+(error.message||''));}
  finally{clearTimeout(timer);}
}
function saveCover(owner,value) {
  owner.row.cover=true;
  const thumb=document.createElement('canvas');thumb.width=200;thumb.height=Math.max(1,Math.round(200*value.height/value.width));
  thumb.getContext('2d').drawImage(value,0,0,thumb.width,thumb.height);
  thumb.toBlob(cover=>{if(cover)safe(patch(owner.row.id,{cover}));},'image/jpeg',.84);
}

function scrollToPage(owner,page) {
  if(current!==owner||!owner.slots)return;
  stream.scrollTop=owner.slots[page].offsetTop-12;
  owner.page=owner.target=page;updateProgress();remember();
}
function startScroll(owner) {
  const seq=++owner.sequence;
  const valid=()=>current===owner&&owner.sequence===seq&&cfg.mode==='scroll';
  owner.viewWidth=stage.clientWidth;owner.viewHeight=stage.clientHeight;
  const slots=Array.from({length:owner.model.count},(_,page)=>{
    const slot=element('div','slot');slot.dataset.page=page;
    slot.style.width=`min(100%, ${Math.round((cfg.fit?1050:680)*owner.zoom)}px)`;
    slot.style.minHeight=owner.model.fixed?Math.max(180,Math.min(1100,stage.clientWidth*1.25))+'px':'260px';
    return slot;
  });
  owner.slots=slots;stream.replaceChildren(...slots);stream.scrollTop=0;
  const last=slots.at(-1);
  const fitTail=()=>{last.style.marginBottom=Math.max(12,stream.clientHeight-last.offsetHeight-12)+'px';};
  fitTail();
  const mounted=new Set(), near=new Set(), loading=new Set();
  async function mount(slot) {
    if(loading.has(slot)||mounted.has(slot))return;
    loading.add(slot);
    const page=Number(slot.dataset.page), shouldLoad=()=>valid()&&near.has(slot);
    try {
      const data=await surface(owner,page,bucket(slot.clientWidth*density()),false,shouldLoad);
      if(!shouldLoad())return;
      const above=slot.offsetTop+slot.offsetHeight<stream.scrollTop;
      const previous=slot.offsetHeight;
      slot.style.minHeight='0';
      slot.append(cloneSurface(data,page));
      if(slot===last)fitTail();
      if(above)stream.scrollTop+=slot.offsetHeight-previous;
      mounted.add(slot);
      if(!owner.row.cover&&page===0&&data instanceof HTMLCanvasElement)saveCover(owner,data);
    }catch(error){
      if(shouldLoad()){slot.textContent='此页无法显示';mounted.add(slot);}
    }finally{loading.delete(slot);}
  }
  owner.observer=new IntersectionObserver(entries=>{
    if(!valid())return;
    for(const entry of entries) {
      const slot=entry.target;
      if(entry.isIntersecting){near.add(slot);void mount(slot);}
      else{
        near.delete(slot);
        if(mounted.delete(slot)){
          slot.style.minHeight=slot.offsetHeight+'px';
          slot.replaceChildren();
        }
      }
    }
  },{root:stream,rootMargin:'100% 0px'});
  slots.forEach(slot=>owner.observer.observe(slot));
  let queued=false;
  stream.onscroll=()=>{
    if(queued)return;queued=true;
    requestAnimationFrame(()=>{
      queued=false;if(!valid())return;
      const limit=stream.scrollTop+Math.min(90,stream.clientHeight*.2);
      let lo=0,hi=slots.length-1;
      while(lo<hi){
        const i=(lo+hi+1)>>1;
        if(slots[i].offsetTop<=limit)lo=i;else hi=i-1;
      }
      if(lo!==owner.page){owner.page=owner.target=lo;updateProgress();remember();}
    });
  };
  scrollToPage(owner,owner.page);wait('');
}
async function showMode() {
  const owner=current;if(!owner?.model)return;
  ++owner.sequence;owner.observer?.disconnect();stream.onscroll=null;
  stopMotion();deck.replaceChildren();stream.replaceChildren();
  deck.hidden=cfg.mode==='scroll';deck.classList.toggle('gesture',cfg.mode==='slide');
  stream.hidden=cfg.mode!=='scroll';
  if(cfg.mode==='scroll')startScroll(owner);
  else await showPage(owner,owner.page);
}
function updateControls() {
  if(!current?.model)return;
  $('mode').value=cfg.mode;$('layout').textContent=cfg.dual?'双页':'单页';
  $('layout').disabled=!current.model.fixed||cfg.mode==='scroll';
  $('direction').textContent=current.rtl?'右到左':'左到右';
  $('fit').textContent=cfg.fit?'适宽':'适整页';
  $('smaller').disabled=current.zoom<=.5;$('larger').disabled=current.zoom>=2.6;
  updateProgress();
}
function updateProgress() {
  if(!current?.model)return;
  const n=current.model.count,p=current.page+1;
  $('page').max=$('slider').max=n;
  if(document.activeElement!==$('page'))$('page').value=p;
  $('slider').value=p;
  $('total').textContent='/ '+n+(current.model.fixed?'':' 章');
  const pages=cfg.mode==='scroll'?[p-1]:group(current,p-1);
  $('previous').disabled=pages[0]===0;$('next').disabled=pages.at(-1)===n-1;
}
function jump(value) {
  if(!current?.model||!Number.isFinite(value))return;
  const page=clamp(Math.round(value),current.model.count);
  if(cfg.mode==='scroll')scrollToPage(current,page);
  else void showPage(current,page);
}
function turn(step,dragX=0) {
  const owner=current;
  if(!owner?.model)return false;
  if(cfg.mode==='scroll'){
    stream.scrollBy({top:step*stage.clientHeight*.87,behavior:reduced()?'instant':'smooth'});return true;
  }
  const pages=group(owner,owner.target),p=step>0?pages.at(-1)+1:pages[0]-1;
  if(p<0||p>=owner.model.count)return false;
  void showPage(owner,p,step,dragX);return true;
}
function zoom(delta) {
  if(!current?.model)return;
  current.zoom=Math.min(2.6,Math.max(.5,Math.round((current.zoom+delta)*10)/10));
  updateControls();
  if(cfg.mode==='scroll')void showMode();else void showPage(current,current.page);
}
$('back').onclick=()=>safe(leave());
$('mode').onchange=()=>{if(!current?.model)return;cfg.mode=$('mode').value;saveOptions();updateControls();void showMode();stage.focus({preventScroll:true});};
$('layout').onclick=()=>{cfg.dual=!cfg.dual;saveOptions();updateControls();void showPage(current,current.page);};
$('direction').onclick=()=>{if(!current?.model)return;current.rtl=!current.rtl;cfg.rtl=current.rtl;saveOptions();updateControls();if(cfg.mode!=='scroll')void showPage(current,current.page);};
$('fit').onclick=()=>{if(!current?.model)return;cfg.fit=!cfg.fit;saveOptions();updateControls();if(cfg.mode==='scroll')void showMode();else void showPage(current,current.page);};
$('smaller').onclick=()=>zoom(-.2);$('larger').onclick=()=>zoom(.2);
$('previous').onclick=()=>turn(-1);$('next').onclick=()=>turn(1);
$('page').onchange=()=>{jump(Number($('page').value)-1);$('page').blur();};
$('slider').onchange=()=>jump(Number($('slider').value)-1);
$('full').onclick=()=>safe(document.fullscreenElement?document.exitFullscreen():$('reader').requestFullscreen?.()??Promise.reject(new Error('此浏览器暂不支持全屏。')));
$('show-progress').onclick=()=>{progress.hidden=!progress.hidden;};
function tap(x) {
  const r=stage.getBoundingClientRect(),part=(x-r.left)/r.width;
  if(part>.25&&part<.75)progress.hidden=!progress.hidden;
  else if(cfg.mode!=='scroll')turn((part<.25)==current.rtl?1:-1);
}
let gesture=null, ignoreTap=0;
stage.addEventListener('pointerdown',event=>{
  if(!current?.model||cfg.mode!=='slide'||event.button!==0||event.target.closest('.text-page')||
    deck.scrollWidth>deck.clientWidth+5)return;
  gesture={id:event.pointerId,x:event.clientX,y:event.clientY,surface:deck.querySelector('.spread'),dx:0,active:false};
});
stage.addEventListener('pointermove',event=>{
  const g=gesture;if(!g||g.id!==event.pointerId||!g.surface)return;
  const dx=event.clientX-g.x,dy=event.clientY-g.y;
  if(!g.active&&(Math.abs(dx)<12||Math.abs(dx)<Math.abs(dy)*1.35))return;
  if(!g.active){stopMotion();g.active=true;stage.setPointerCapture(event.pointerId);}
  g.dx=dx*.55;g.surface.style.transform=`translate3d(${g.dx}px,0,0)`;
});
function finishGesture(event,cancel){
  const g=gesture;if(!g||g.id!==event.pointerId)return;
  gesture=null;if(!g.active)return;
  ignoreTap=performance.now()+350;
  const direction=(g.dx>0)===current?.rtl?1:-1;
  if(cancel||Math.abs(g.dx)<40||!turn(direction,g.dx)){
    const a=g.surface.animate?.([{transform:g.surface.style.transform},{transform:'translate3d(0,0,0)'}],{duration:reduced()?1:145});
    g.surface.style.transform='';a?.finished.catch(()=>{});
  }
}
stage.addEventListener('pointerup',event=>finishGesture(event,false));
stage.addEventListener('pointercancel',event=>finishGesture(event,true));
stage.addEventListener('click',event=>{
  if(current?.model&&performance.now()>=ignoreTap&&!event.target.closest('.text-page'))tap(event.clientX);
});
let lastWheel=0;
stage.addEventListener('wheel',event=>{
  if(!current?.model||cfg.mode==='scroll'||(deck.scrollHeight>deck.clientHeight+5&&Math.abs(event.deltaY)>Math.abs(event.deltaX)))return;
  const delta=Math.abs(event.deltaX)>Math.abs(event.deltaY)?event.deltaX*(current.rtl?-1:1):event.deltaY;
  if(Math.abs(delta)<5)return;
  event.preventDefault();
  if(performance.now()-lastWheel<160)return;
  lastWheel=performance.now();turn(Math.sign(delta));
},{passive:false});
document.addEventListener('keydown',event=>{
  if(!current?.model||event.target.closest('input,select,button,dialog')||event.ctrlKey||event.altKey||event.metaKey)return;
  const key=event.key;
  if(key==='Escape'){if(!document.fullscreenElement)safe(leave());return;}
  const step={ArrowLeft:current.rtl?1:-1,ArrowRight:current.rtl?-1:1,PageDown:1,PageUp:-1,' ':1}[key];
  if(step){event.preventDefault();turn(step);}
  else if(key==='Home'||key==='End'){event.preventDefault();jump(key==='Home'?0:current.model.count-1);}
});
let resizing;
new ResizeObserver(()=>{
  clearTimeout(resizing);
  resizing=setTimeout(()=>{
    if(!current?.model)return;
    if(current.viewWidth===stage.clientWidth&&current.viewHeight===stage.clientHeight)return;
    if(cfg.mode==='scroll')void showMode();else void showPage(current,current.target);
  },160);
}).observe(stage);
safe(refreshShelf());
