import {test} from 'node:test';
import assert from 'node:assert/strict';

test('workflow context is explicit, same-origin only, and preserves request headers and storage boundaries',async()=>{
 globalThis.location={href:'https://demo.example/validation.html?workflow=wf-example',origin:'https://demo.example',search:'?workflow=wf-example'};
 const calls=[];globalThis.fetch=async(input,init)=>{calls.push({input,init});return {ok:true};};
 const {apiFetch,workflowHref,workflowStorageKey}=await import('../src/workflow-context.ts');
 await apiFetch('/api/search',{headers:{Authorization:'Bearer fixture'},method:'POST',body:'{}'});
 assert.equal(calls[0].init.headers.get('X-Dse-Workflow'),'wf-example');assert.equal(calls[0].init.headers.get('Authorization'),'Bearer fixture');
 await apiFetch('https://outside.example/api/search');assert.equal(calls[1].init?.headers?.get('X-Dse-Workflow')??null,null);
 await apiFetch('/api/config');assert.equal(calls[2].init?.headers?.get('X-Dse-Workflow')??null,null);
 assert.equal(workflowHref('/validation.html?skillVersion=2'),'/validation.html?skillVersion=2&workflow=wf-example');
 assert.equal(workflowStorageKey('dse-upload-job:owner'),'dse-upload-job:owner:workflow:wf-example');
 location.search='?workflow=';await apiFetch('/api/search');assert.equal(calls[3].init.headers.get('X-Dse-Workflow'),'invalid');
 location.search='';assert.equal(workflowStorageKey('key'),'key');assert.equal(workflowHref('/validation.html'),'/validation.html');
});
