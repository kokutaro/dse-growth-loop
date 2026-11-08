// Keep selection explicit through normal pages and APIs. Invalid selections never
// disappear into shared storage, and credentials/context never go to another origin.
export function workflowId(){const params=new URLSearchParams(location.search);return params.has('workflow')?(params.get('workflow')||'invalid'):undefined;}
const nativeFetch=globalThis.fetch.bind(globalThis);
export function apiFetch(input:RequestInfo|URL,init?:RequestInit){
 const url=new URL(input instanceof Request?input.url:String(input),location.href);const id=workflowId();
 if(id===undefined||url.origin!==location.origin||!url.pathname.startsWith('/api/')||url.pathname==='/api/config'||url.pathname==='/api/v1/demo-workflows/start')return nativeFetch(input,init);
 const headers=new Headers(input instanceof Request?input.headers:undefined);new Headers(init?.headers).forEach((value,key)=>headers.set(key,value));headers.set('X-Dse-Workflow',id);
 return nativeFetch(input,{...init,headers});
}
export function workflowHref(path:string){const id=workflowId();if(id===undefined)return path;const url=new URL(path,location.href);if(url.origin!==location.origin)return path;url.searchParams.set('workflow',id);return url.pathname+url.search+url.hash;}
export function workflowStorageKey(key:string){const id=workflowId();return key+(id===undefined?'':':workflow:'+id);}
