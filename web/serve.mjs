import {createServer} from 'node:http';
import {createReadStream,statSync} from 'node:fs';
import {extname,resolve,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
import {networkInterfaces} from 'node:os';

const root=resolve(fileURLToPath(new URL('./dist/',import.meta.url)));
const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8',
  '.js':'text/javascript; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.png':'image/png',
  '.bcmap':'application/octet-stream','.pdf':'application/pdf'};
const server=createServer((req,res)=>{
  if(req.method!=='GET'&&req.method!=='HEAD'){res.writeHead(405).end();return;}
  try{
    const name=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
    const file=resolve(root,'.'+(name==='/'?'/index.html':name));
    if(!file.startsWith(root+sep)||!statSync(file).isFile()){res.writeHead(404).end();return;}
    res.writeHead(200,{'Content-Type':types[extname(file)]||'application/octet-stream'});
    if(req.method==='HEAD')res.end();else createReadStream(file).pipe(res);
  }catch{res.writeHead(404).end();}
});
const lan=process.argv.includes('--lan');
server.listen(Number(process.env.KOMASNAP_WEB_PORT)||4173,lan?'0.0.0.0':'127.0.0.1',()=>{
  const port=server.address().port;
  const local='http://127.0.0.1:'+port;
  console.log(local);
  if(lan)for(const interfaces of Object.values(networkInterfaces()))
    for(const info of interfaces||[])if(info.family==='IPv4'&&!info.internal)console.log('http://'+info.address+':'+port);
  if(process.platform==='win32'&&process.argv.includes('--open'))
    spawn('explorer.exe',[local],{windowsHide:true,detached:true,stdio:'ignore'}).unref();
});
