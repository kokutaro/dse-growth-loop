// Presence matters: an invalid/expired demo must never fall back to shared writes.
export function skillDemoId(){const p=new URLSearchParams(location.search);return p.has('skillDemo')?(p.get('skillDemo')||'invalid'):undefined;}
export function skillApi(path:string){const id=skillDemoId();return '/api/v1/'+(id===undefined?'':'skill-demo/'+encodeURIComponent(id)+'/')+path;}
export function skillSearchLink(version:number){const id=skillDemoId();return '/validation.html?skillVersion='+version+(id===undefined?'':'&skillDemo='+encodeURIComponent(id));}
