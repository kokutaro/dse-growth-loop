const image = document.querySelector('#screen-image');
const original = document.querySelector('#original');
const status = document.querySelector('#status');
document.querySelectorAll('input').forEach(input => input.addEventListener('change', () => {
  const device = document.querySelector('input[name="device"]:checked').value;
  const screen = document.querySelector('input[name="screen"]:checked').value;
  const title = `${device === '1440' ? 'PC' : 'スマホ'}：${screen === 'home' ? '検索前・全体' : '検索結果・根拠'}`;
  image.src = `${screen}-${device}.png`;
  image.width = Number(device);
  image.alt = title;
  original.href = image.src;
  status.textContent = title;
}));
