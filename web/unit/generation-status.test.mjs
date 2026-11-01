import {test} from 'node:test';
import assert from 'node:assert/strict';
import {generationCanStart,generationAcceptanceMessage} from '../src/generation-status.ts';

test('only an explicitly new estimate permits new spend',()=>{
 for(const availability of ['existing_success','existing_failed','existing_pending','already_applied',undefined])assert.equal(generationCanStart({availability}),false);
 assert.equal(generationCanStart({availability:'new'}),true);
 assert.equal(generationCanStart({availability:'new',alreadyApplied:true}),false);
});
test('existing terminal and pending receipts do not claim fresh acceptance',()=>{
 assert.match(generationAcceptanceMessage({acceptance:'new',generationId:'g'}),/新規受付/);
 assert.match(generationAcceptanceMessage({acceptance:'existing_failed',generationId:'g'}),/既存の失敗/);
 assert.match(generationAcceptanceMessage({acceptance:'existing_success',generationId:'g'}),/既存の完了/);
 assert.match(generationAcceptanceMessage({acceptance:'existing_pending',generationId:'g'}),/既存の処理/);
 assert.match(generationAcceptanceMessage({acceptance:'already_applied',generationId:'g'}),/適用済み/);
 assert.throws(()=>generationAcceptanceMessage({generationId:'g'}),/状態を確認/);
});
