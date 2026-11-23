const imageName = /\.(jpe?g|png|webp|gif|avif|bmp)$/i;
const numberSort = new Intl.Collator('zh-Hans', {numeric:true, sensitivity:'base'});
const xml = text => new DOMParser().parseFromString(text, 'application/xml');
const limits = {image:64*1024*1024, text:8*1024*1024};
const safeTags = new Set('h1 h2 h3 h4 p div blockquote pre br ul ol li strong b em i ruby rt rp sup sub hr figure figcaption'.split(' '));
const skipped = new Set(['script','style','link','iframe','object','video','audio','nav','head','title','noscript']);
const vendor = new URL('./vendor/', import.meta.url);
export const typeOf = name => name.match(/\.(pdf|epub|cbz|zip)$/i)?.[1].toLowerCase();

async function pageBlob(entry) {
  const blob = await entry.async('blob');
  if (blob.size > limits.image) throw new Error('书内有一张过大的图片。');
  return blob;
}

async function readText(entry) {
  if (!entry) throw new Error('EPUB 缺少必要文件。');
  const bytes = await entry.async('uint8array');
  if (bytes.length > limits.text) throw new Error('EPUB 内有一个过大的章节。');
  return new TextDecoder().decode(bytes);
}

async function loadImage(blob) {
  const url = URL.createObjectURL(blob);
  const image = new Image();
  image.src = url;
  try { await image.decode(); return image; } finally { URL.revokeObjectURL(url); }
}

function localPath(base, href) {
  if (!href) return '';
  try {
    const url = new URL(href, new URL(base, 'https://epub.invalid/'));
    return url.origin === 'https://epub.invalid' ? decodeURIComponent(url.pathname.slice(1)) : '';
  } catch { return ''; }
}

function imageCanvas(image, width) {
  const ratio = image.naturalHeight / image.naturalWidth;
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.min(image.naturalWidth, width, Math.floor(2688 / Math.max(1, ratio))));
  canvas.height = Math.max(1, Math.round(canvas.width * ratio));
  canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas;
}

export async function openBook(blob, type) {
  if (type === 'pdf') {
    const pdf = await import('./vendor/pdf.min.mjs');
    pdf.GlobalWorkerOptions.workerSrc = new URL('pdf.worker.min.mjs', vendor).href;
    const load = pdf.getDocument({data:new Uint8Array(await blob.arrayBuffer()), isEvalSupported:false,
      cMapUrl:new URL('cmaps/', vendor).href, cMapPacked:true, standardFontDataUrl:new URL('standard_fonts/', vendor).href});
    try {
      const doc = await load.promise;
      return {
        count:doc.numPages, fixed:true, close:()=>load.destroy(),
        async ratio(i) { const v=(await doc.getPage(i+1)).getViewport({scale:1}); return v.width/v.height; },
        async render(i,width) {
          const page=await doc.getPage(i+1), natural=page.getViewport({scale:1});
          const view=page.getViewport({scale:Math.min(width/natural.width,2688/Math.max(natural.width,natural.height))});
          const canvas=document.createElement('canvas');
          canvas.width=Math.ceil(view.width); canvas.height=Math.ceil(view.height);
          await page.render({canvasContext:canvas.getContext('2d',{alpha:false}), viewport:view}).promise;
          return canvas;
        }
      };
    } catch(error) {
      await load.destroy().catch(()=>{});
      if (error.name === 'PasswordException') throw new Error('请使用可直接打开的 PDF 文件。');
      throw error;
    }
  }
  if (!globalThis.JSZip) throw new Error('图片包阅读组件未加载。');
  const zip = await globalThis.JSZip.loadAsync(blob);
  const imagePage = path => ({image:path, async load(){return loadImage(await pageBlob(zip.file(path)));}});
  if (type !== 'epub') {
    return fromPages(Object.values(zip.files).filter(x=>!x.dir && imageName.test(x.name) &&
      !x.name.split('/').some(x=>x.startsWith('.')||x==='__MACOSX'))
      .sort((a,b)=>numberSort.compare(a.name,b.name)).map(x=>imagePage(x.name)));
  }
  const container=xml(await readText(zip.file('META-INF/container.xml')));
  const packagePath=container.getElementsByTagNameNS('*','rootfile')[0]?.getAttribute('full-path');
  const pkg=xml(await readText(zip.file(packagePath)));
  const spine=pkg.getElementsByTagNameNS('*','spine')[0];
  if (!spine) throw new Error('EPUB 缺少阅读顺序。');
  const enc=zip.file('META-INF/encryption.xml');
  if (enc && Array.from(xml(await readText(enc)).getElementsByTagNameNS('*','EncryptionMethod'))
    .some(el=>!['http://www.idpf.org/2008/embedding','http://ns.adobe.com/pdf/enc#RC'].includes(el.getAttribute('Algorithm')))) {
    throw new Error('请使用无 DRM 的 EPUB。');
  }
  const paths=new Map(Array.from(pkg.getElementsByTagNameNS('*','item'), item=>[
    item.getAttribute('id'),localPath(packagePath,item.getAttribute('href'))]));
  const pages=[];
  for (const ref of spine.getElementsByTagNameNS('*','itemref')) {
    if (ref.getAttribute('linear')==='no') continue;
    const path=paths.get(ref.getAttribute('idref'));
    if (!path || !zip.file(path)) continue;
    if (imageName.test(path)) { pages.push(imagePage(path)); continue; }
    if (!/\.(x?html?|svg)$/i.test(path)) continue;
    const source=await readText(zip.file(path));
    const parsed=xml(source);
    let body=parsed.getElementsByTagNameNS('*','body')[0];
    if(!body){
      if(path.endsWith('.svg'))body=parsed.documentElement;
      else {
        const template=document.createElement('template');template.innerHTML=source;body=template.content;
      }
    }
    const visibleText = Array.from(body.childNodes).some(n=>n.nodeType===3?n.textContent.trim():
      n.nodeType===1&&!skipped.has(n.localName)&&n.textContent.trim());
    const pictures=Array.from(body.querySelectorAll('img,image'), el=>
      localPath(path,el.getAttribute('src')||el.getAttribute('href')||el.getAttribute('xlink:href')))
      .filter(p=>imageName.test(p)&&zip.file(p));
    if (!visibleText && pictures.length) { pages.push(...pictures.map(imagePage)); continue; }
    if (!visibleText && !pictures.length) continue;
    pages.push({async load() {
      const article=document.createElement('article'); article.className='text-page';
      async function append(node, target) {
        if (node.nodeType===3) {target.append(document.createTextNode(node.textContent));return;}
        if (node.nodeType!==1||skipped.has(node.localName)) return;
        const tag=node.localName;
        if (tag==='img'||tag==='image') {
          const p=localPath(path,node.getAttribute('src')||node.getAttribute('href')||node.getAttribute('xlink:href'));
          if (imageName.test(p)&&zip.file(p)) {
            try {const img=await loadImage(await pageBlob(zip.file(p)));img.alt=node.getAttribute('alt')||'';target.append(img);} catch {}
          }
          return;
        }
        const next=safeTags.has(tag)?document.createElement(tag):target;
        if (next!==target) target.append(next);
        for (const child of node.childNodes) await append(child,next);
      }
      for (const child of body.childNodes) await append(child,article);
      return article;
    }});
  }
  const book=fromPages(pages);
  const direction=spine.getAttribute('page-progression-direction');
  book.rtl=direction==='rtl'?true:direction==='ltr'||!book.fixed?false:undefined;
  return book;
}

function fromPages(pages) {
  if (!pages.length) throw new Error('文件中没有可读的页面。');
  return {count:pages.length, fixed:pages.every(p=>p.image), close(){},
    async ratio(i) {
      const page=pages[i]; if (!page.image) return 0;
      if (!page.aspect) {
        page.decoding??=page.load();
        try {const img=await page.decoding;page.aspect=img.naturalWidth/img.naturalHeight;}
        catch(error){delete page.decoding;throw error;}
      }
      return page.aspect;
    },
    async render(i,width) {
      const page=pages[i], value=await (page.decoding??page.load());
      delete page.decoding;
      if (!page.image) return value;
      page.aspect=value.naturalWidth/value.naturalHeight;
      return imageCanvas(value,width);
    }
  };
}
