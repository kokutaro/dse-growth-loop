const labels = ['課題投稿', 'AIマッチング・通知', '許可・拒否', 'セッション参加', 'チャットで共創', 'クローズ・評価', '知識として蓄積'];
const experts = [
  { alias: 'Expert-A17', category: 'ソフトウェア・不具合調査', reason: '切り分け手順を整理し、調査を引き継いだ経験が課題に近い候補です。', score: 92 },
  { alias: 'Expert-C08', category: '設計・品質改善', reason: '再現条件と検証結果を共有し、設計の見直しにつなげた経験がある候補です。', score: 86 },
  { alias: 'Expert-D24', category: '開発プロセス・知識共有', reason: 'チームで調査記録を残し、同じ課題の再発を減らした経験がある候補です。', score: 81 }
];
const initial = () => ({ step: 0, title: '制御ソフトの不具合調査を、別チームへ引き継ぎたい', detail: '再現条件や試したことが担当者ごとに分かれています。調査の観点と記録方法を整理したいです。', category: 'ソフトウェア', posted: false, selected: 0, decision: '', joined: false, messages: [], result: '再現条件・試したこと・次の確認事項を一つの記録にまとめ、調査を引き継げた。', resolution: '解決', rating: '役立った', publish: false, reuse: false, analysis: false, role: '投稿者A', approvals: {}, closed: false });
let state = initial();
const stage = document.querySelector('#stage');
const esc = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const expert = () => experts[state.selected];
const privacy = text => `<div class="privacy"><strong>実名は双方が同意するまで開示されません</strong>${text}</div>`;
const actions = (next, label, disabled = false) => `<div class="actions">${state.step > 0 ? '<button data-back>前の段階へ</button>' : ''}<button class="primary" data-next="${next}" ${disabled ? 'disabled' : ''}>${label}</button></div>`;
function goto(step) { state.step = step; render(); stage.focus(); }
function heading(lead) { return `<p class="eyebrow">STEP ${String(state.step + 1).padStart(2, '0')} / 07</p><h1 id="stage-title">${labels[state.step]}</h1><p class="lead">${lead}</p>`; }
function requirePosted() { return `${heading('課題を起点に、経験のある人との接点を探します。')}<div class="empty"><h2>まず、困っていることを教えてください</h2><p>課題を投稿すると、この段階の操作を試せます。</p><button class="primary" data-next="0">課題投稿へ</button></div>`; }
function requireJoined() { return `${heading('会話に参加した人だけで、相談を進めます。')}<div class="empty"><h2>匿名での参加を確認してください</h2><p>参加への同意は、実名開示への同意ではありません。</p><button class="primary" data-next="${state.decision === 'accepted' ? 3 : 2}">参加の確認へ</button></div>`; }
function content() {
  if (state.step > 0 && !state.posted) return requirePosted();
  if (state.step >= 4 && !state.joined) return requireJoined();
  switch (state.step) {
    case 0: return `${heading('小さな困りごとからで大丈夫です。経験を持つ人に、匿名で相談を届けましょう。')}
      <form id="post-form" class="form-width"><label for="title">課題のタイトル</label><input id="title" name="title" required maxlength="150" value="${esc(state.title)}"><label for="detail">困っていること・相談したいこと</label><textarea id="detail" name="detail" required maxlength="2000">${esc(state.detail)}</textarea><small>氏名・部署名・顧客名・機密情報は書かず、状況と相談したい点を記入してください。</small><label for="category">課題の分野</label><select id="category"><option>ソフトウェア</option><option>機械設計</option><option>製造・品質</option><option>業務改善</option></select>
      <fieldset><legend>投稿者の表示</legend><label class="option"><input type="radio" name="identity" checked><span>匿名で投稿 <strong>「投稿者A」</strong></span></label><label class="option"><input type="radio" name="identity" disabled><span>実名で投稿 <small>相互同意の仕様は未確定のため、このデモでは利用できません。</small></span></label></fieldset>
      ${privacy('候補者に届く通知にも、あなたの実名は表示しません。このデモでは最後まで匿名を維持します。')}
      <div class="actions"><button class="primary" type="submit">匿名で課題を投稿する（デモ）</button><small>実際の投稿やAI処理は行いません。次の段階では架空のマッチング結果を表示します。</small></div></form>`;
    case 1: return `${heading('課題に近い経験を持つ候補へ、相談のきっかけを届けます。')}<span class="state-label">投稿者としての表示</span><h2>${esc(state.title)}</h2><small>以下は固定の架空候補・参考スコアです。入力に対するAIの実判定ではありません。</small><div class="candidates">${experts.map((e, i) => `<article class="candidate"><div><h3>${e.alias}</h3><div class="tags"><span class="tag">${e.category}</span></div><p>${e.reason}</p><button data-notify="${i}">この候補への通知を確認（デモ）</button></div><div class="score"><b>${e.score}</b> / 100<small>デモ参考値</small></div></article>`).join('')}</div>${privacy('実名・部署・メール・社員ID・個人プロフィールへのリンクは表示しません。参加するかは候補者本人が選びます。')}`;
    case 2: return `${heading('依頼内容を確認し、協力できるときに参加を選べます。')}<span class="state-label">${expert().alias} 本人への通知（デモ）</span><div class="notification"><h2>あなたの経験が役立つかもしれません</h2><dl><dt>投稿者</dt><dd>投稿者A（匿名）</dd><dt>課題</dt><dd>${esc(state.title)}</dd><dt>相談内容</dt><dd>${esc(state.detail)}</dd></dl><details><summary>あなたに届いた理由（本人向け）</summary><p>${expert().reason}</p><small>本人向けの架空の根拠です。原本文書や所属情報は含めていません。</small></details></div>${privacy('参加を許可しても実名は公開されません。匿名のまま参加できます。辞退理由は相手へ送りません。')}
      ${state.decision === 'declined' ? '<p class="result-note" role="status">この依頼を辞退しました（デモ）。会話には参加していません。</p><button id="retry-decision">参加可否を選び直す（デモ）</button>' : `<div class="actions"><button class="primary" id="accept">匿名での参加を許可する</button><button class="danger" id="decline">今回は辞退する</button></div>`}`;
    case 3: return `${heading('この相談で使う表示名と、参加範囲を確認します。')}<div class="notification"><h2>${esc(state.title)}</h2><dl><dt>あなたの表示</dt><dd>${expert().alias}</dd><dt>相談相手</dt><dd>投稿者A</dd><dt>会話の範囲</dt><dd>このセッションの参加者のみ</dd></dl></div>${privacy('ケース内で同じ匿名名を使います。実名への切替や、他の相談と結びつけるプロフィールはこのデモにはありません。')}${state.decision !== 'accepted' ? '<p>まだ参加を許可していません。</p><button data-next="2">参加可否を確認する</button>' : `<div class="actions"><button class="primary" id="join">この匿名名でセッションに参加</button><button id="leave">参加を取り消す</button></div>`}`;
    case 4: return `${heading('役職や所属ではなく、課題と経験をもとに一緒に考えます。')}<div class="chat-header"><div><h2>${esc(state.title)}</h2><small>参加者のみの会話 · デモ内の表示</small></div><button id="leave">セッションから退出</button></div><div class="participants"><span>投稿者A</span><span>${expert().alias}</span></div><ol class="chat-messages" aria-label="匿名の会話"><li><strong>投稿者A</strong><p>調査を引き継ぐとき、まず何をそろえるとよいでしょうか？</p></li><li><strong>${expert().alias}</strong><p>再現条件、試したこと、次に確認することを分けて残すと、途中からでも参加しやすくなります。</p></li><li class="case"><strong>AIによる事例紹介（固定デモ）</strong><p>「不具合調査の引き継ぎ項目をそろえる」</p><details><summary>匿名化された事例の要点</summary><p>再現条件と検証結果を同じ書式で記録し、未確認の項目を明確にした事例です。個人や原会話の情報は含みません。</p></details></li>${state.messages.map(m => `<li class="mine"><strong>${expert().alias} · デモ入力</strong><p>${esc(m)}</p></li>`).join('')}</ol><form id="message-form" class="chat-compose"><label for="message">${expert().alias}としてメッセージを入力<textarea id="message" required maxlength="2000" placeholder="気づいたことや、試せそうなことを書いてみましょう"></textarea></label><button class="primary" type="submit">デモ内に追加</button></form><small>相手への送信や永続保存は行いません。ページを再読み込みすると消えます。</small>${actions(5, '相談をクローズ・評価へ')}`;
    case 5: return `${heading('解決の結果を振り返り、共有する内容と利用目的を参加者で確かめます。')}<div class="form-width"><label for="resolution">解決の状況</label><select id="resolution"><option>解決</option><option>一部解決</option><option>未解決</option></select><label for="rating">相談は役立ちましたか？</label><select id="rating"><option>役立った</option><option>どちらともいえない</option><option>役立たなかった</option></select><label for="result">共有する解決の要約</label><textarea id="result" maxlength="2000">${esc(state.result)}</textarea><small>実名・部署名・顧客名・機密情報を除いた内容にしてください。実際の匿名化検査は未接続です。</small><fieldset><legend>要約の利用目的（デモ）</legend>${[['publish','匿名化した要約の社内公開'],['reuse','将来の課題検索での再利用'],['analysis','事業分析への利用']].map(([key,label])=>`<label class="option"><input type="checkbox" id="${key}" ${state[key]?'checked':''}><span>${label}</span></label>`).join('')}</fieldset>${privacy('参加の許可は、事例公開や事業分析への同意ではありません。ここでの選択と承認はデモ内だけのもので、本番の公開処理は実行しません。')}
      <div class="review"><h2>参加者による公開内容の確認</h2><small>表示している版と選択した利用目的を、それぞれの参加者が確認する操作例です。要約・利用目的を変更すると確認を取り消します。</small><label for="review-role">デモで操作する参加者</label><select id="review-role"><option>投稿者A</option><option>${expert().alias}</option></select><button id="approve">選択した参加者として確認する（デモ）</button><div id="approval-status" role="status">${approvalText()}</div></div></div><div class="actions"><button data-back>会話へ戻る</button><button class="primary" id="close">${state.publish?'承認を確認して蓄積へ（デモ）':'非公開でクローズする（デモ）'}</button></div><p id="close-error" role="alert" hidden></p>`;
    case 6: {
      const published=state.closed&&state.publish&&allApproved();
      return `${heading('個人を特定しない知見として、次の課題解決へつなげます。')}<span class="state-label ${published?'teal':''}">${published?'社内公開を想定したデモ表示':'非公開・確認中のデモ表示'}</span><article class="case-summary"><p class="eyebrow">SOLUTION / 匿名化された解決の要約</p><h2>不具合調査の引き継ぎ項目をそろえる</h2><p>${esc(state.result)}</p><dl class="summary-grid"><dt>解決状況</dt><dd>${esc(state.resolution)}</dd><dt>公開対象</dt><dd>${published?'全社員を想定（デモ）':'公開していません'}</dd><dt>検索での再利用</dt><dd>${published&&state.reuse?'同意した利用目的（デモ）':'利用しない'}</dd><dt>事業分析</dt><dd>${published&&state.analysis?'別途同意した利用目的（デモ）':'利用しない'}</dd></dl><small>氏名・所属・個人プロフィール・原会話へのリンクは表示しません。</small></article><p class="metric">このデモで蓄積を確認した事例 <b>${published?1:0}</b> 件</p><p class="sample">事例件数も画面内の架空の値です。実際の登録・全社員への公開・検索反映・分析は行っていません。</p>${privacy('実名はこの段階でも開示しません。協力の実績を匿名プロフィールに残す構想はありますが、ケースをまたぐ識別規則は未確定です。')}${actions(0,'別の課題を考える')}`;
    }
  }
}
function allApproved(){return Boolean(state.approvals['投稿者A']&&state.approvals[expert().alias]);}
function approvalText(){return ['投稿者A',expert().alias].map(name=>`<p>${name}：${state.approvals[name]?'表示中の要約・利用目的を確認済み（デモ）':'未確認'}</p>`).join('');}
function resetApprovals(){state.approvals={};state.closed=false;const el=document.querySelector('#approval-status');if(el)el.innerHTML=approvalText();}
function render(){
  document.querySelector('#steps').innerHTML=labels.map((label,i)=>`<button data-step="${i}" aria-label="${i+1}. ${label}" ${state.step===i?'aria-current="step"':''}><span>${i+1}</span><span>${label}</span></button>`).join('');
  stage.innerHTML=content();
  document.querySelectorAll('[data-step]').forEach(b=>b.onclick=()=>goto(Number(b.dataset.step)));
  stage.querySelectorAll('[data-next]').forEach(b=>b.onclick=()=>goto(Number(b.dataset.next)));
  stage.querySelector('[data-back]')?.addEventListener('click',()=>goto(state.step-1));
  const form=document.querySelector('#post-form');
  if(form){document.querySelector('#category').value=state.category;form.onsubmit=e=>{e.preventDefault();state.title=document.querySelector('#title').value.trim();state.detail=document.querySelector('#detail').value.trim();if(!state.title||!state.detail)return;const category=document.querySelector('#category').value;state={...initial(),title:state.title,detail:state.detail,category,posted:true};goto(1);};}
  stage.querySelectorAll('[data-notify]').forEach(b=>b.onclick=()=>{const index=Number(b.dataset.notify);if(state.selected!==index){state.decision='';state.joined=false;state.messages=[];resetApprovals();}state.selected=index;goto(2);});
  document.querySelector('#accept')?.addEventListener('click',()=>{state.decision='accepted';goto(3);});
  document.querySelector('#decline')?.addEventListener('click',()=>{state.decision='declined';state.joined=false;render();});
  document.querySelector('#retry-decision')?.addEventListener('click',()=>{state.decision='';render();});
  document.querySelector('#join')?.addEventListener('click',()=>{state.joined=true;goto(4);});
  document.querySelector('#leave')?.addEventListener('click',()=>{state.joined=false;state.decision='';state.messages=[];resetApprovals();goto(2);});
  document.querySelector('#message-form')?.addEventListener('submit',e=>{e.preventDefault();const input=document.querySelector('#message');if(!input.value.trim())return;state.messages.push(input.value.trim());render();document.querySelector('#message').focus();const list=document.querySelector('.chat-messages');list.scrollTop=list.scrollHeight;});
  for(const key of ['resolution','rating','review-role']){const el=document.getElementById(key);if(el){el.value=key==='review-role'?state.role:state[key];if(!el.value)el.value='投稿者A';el.onchange=()=>{if(key==='review-role')state.role=el.value;else{state[key]=el.value;resetApprovals();}};}}
  document.querySelector('#result')?.addEventListener('input',e=>{state.result=e.target.value;resetApprovals();});
  for(const key of ['publish','reuse','analysis'])document.getElementById(key)?.addEventListener('change',e=>{state[key]=e.target.checked;resetApprovals();document.querySelector('#close').textContent=state.publish?'承認を確認して蓄積へ（デモ）':'非公開でクローズする（デモ）';});
  document.querySelector('#approve')?.addEventListener('click',()=>{state.role=document.querySelector('#review-role').value;state.approvals[state.role]=true;document.querySelector('#approval-status').innerHTML=approvalText();});
  document.querySelector('#close')?.addEventListener('click',()=>{const error=document.querySelector('#close-error');if(state.publish&&!allApproved()){error.hidden=false;error.textContent='公開するには、投稿者と協力者の両方が現在の要約・利用目的を確認してください（デモ）。';return;}if(!state.publish&&(state.reuse||state.analysis)){error.hidden=false;error.textContent='このデモでは、非公開のまま検索再利用・事業分析を行いません。利用目的の選択を外してください。';return;}state.closed=true;goto(6);});
}
document.querySelector('#reset').onclick=()=>{state=initial();goto(0);};
render();
