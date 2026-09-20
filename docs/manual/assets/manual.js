/* いろか マニュアル — 目次・検索・言語切替・実演デモ */
(() => {
  'use strict';

  const DATA = JSON.parse(document.getElementById('manual-data').textContent);
  const LANGS = ['ja', 'en'];

  const T = {
    ja: {
      manual: 'マニュアル', search: '検索', toc: '目次', other: 'EN',
      noHit: '「%s」に当てはまる項目はありません',
      footNote: 'この内容はリポジトリの MANUAL.md から生成しています。',
      releases: 'リリース', changelog: '変更履歴',
    },
    en: {
      manual: 'User Manual', search: 'Search', toc: 'Contents', other: '日本語',
      noHit: 'Nothing matches “%s”',
      footNote: 'Generated from MANUAL.md in the repository.',
      releases: 'Releases', changelog: 'Changelog',
    },
  };

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const store = {
    get(k, d) { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
  };

  const root = document.documentElement;
  const toc = $('#toc');
  const qInput = $('#q');
  const qClear = $('#q-clear');
  const noHit = $('#no-hit');
  const zoom = $('#zoom');

  let lang = pickLang();
  let query = '';
  const pristine = new Map();   // section element -> 元 HTML（ハイライト復元用）

  function pickLang() {
    const saved = store.get('iroca.manual.lang', null);
    if (LANGS.includes(saved)) return saved;
    return (navigator.language || 'ja').toLowerCase().startsWith('ja') ? 'ja' : 'en';
  }

  /* ── テーマ ─────────────────────────────── */
  function applyTheme(t) {
    root.dataset.theme = t;
    store.set('iroca.manual.theme', t);
  }
  applyTheme(store.get('iroca.manual.theme',
    matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'));
  $('#theme').addEventListener('click', () => {
    applyTheme(root.dataset.theme === 'dark' ? 'light' : 'dark');
  });

  /* ── 言語 ───────────────────────────────── */
  function applyLang(next) {
    lang = next;
    store.set('iroca.manual.lang', next);
    root.lang = next;
    $$('.doc').forEach(a => {
      const on = a.dataset.lang === next;
      a.hidden = !on;
      // id は表示中の言語にだけ置く（同じアンカーを日英で共有するため）
      $$('[data-id]', a).forEach(el => {
        if (on) el.id = el.dataset.id;
        else el.removeAttribute('id');
      });
    });
    $$('[data-t]').forEach(el => { el.textContent = T[next][el.dataset.t] ?? el.textContent; });
    $('#lang').textContent = T[next].other;
    qInput.placeholder = T[next].search;
    toc.setAttribute('aria-label', T[next].toc);
    buildToc();
    applyFilter();
    observeHeadings();
  }
  $('#lang').addEventListener('click', () => applyLang(lang === 'ja' ? 'en' : 'ja'));

  /* ── 目次 ───────────────────────────────── */
  function buildToc() {
    const ol = document.createElement('ol');
    DATA.nav[lang].forEach((sec, i) => {
      const li = document.createElement('li');
      const a = document.createElement('a');
      a.href = '#' + sec.id;
      a.dataset.sid = sec.id;
      a.innerHTML = `<span class="num">${String(i + 1).padStart(2, '0')}</span>${esc(sec.title)}`;
      li.append(a);
      if (sec.subs.length) {
        const subs = document.createElement('ol');
        subs.className = 'subs';
        sec.subs.forEach(s => {
          const sli = document.createElement('li');
          const sa = document.createElement('a');
          sa.href = '#' + s.a;
          sa.dataset.anchor = s.a;
          sa.textContent = s.t;
          sli.append(sa);
          subs.append(sli);
        });
        li.append(subs);
      }
      ol.append(li);
    });
    toc.replaceChildren(ol);
  }

  const esc = s => s.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

  /* ── 現在地 ─────────────────────────────── */
  // スクロール位置より上にある最後の見出しを「現在地」とする。デモや図版が大きくても
  // 追従が途切れない（帯に入った見出しを拾う方式は、見出し間が長いと外れる）。
  let heads = [];
  let lastCur = null;

  function observeHeadings() {
    const doc = $(`.doc[data-lang="${lang}"]`);
    heads = doc ? $$('.sec:not([hidden]) h2, .sec:not([hidden]) h3', doc) : [];
    lastCur = null;
    syncCurrent();
  }

  function syncCurrent() {
    if (!heads.length) return;
    const y = scrollY + ($('.top')?.offsetHeight || 56) + 72;
    let cur = heads[0];
    for (const h of heads) {
      if (h.getBoundingClientRect().top + scrollY <= y) cur = h; else break;
    }
    if (cur === lastCur) return;
    lastCur = cur;
    const sid = cur.closest('.sec').dataset.sid;
    $$('.toc a').forEach(a => a.classList.remove('cur'));
    const main = toc.querySelector(`a[data-sid="${sid}"]`);
    main?.classList.add('cur');
    if (cur.tagName === 'H3' && cur.dataset.id) {
      const sub = toc.querySelector(`a[data-anchor="${CSS.escape(cur.dataset.id)}"]`);
      sub?.classList.add('cur');
      if (sub) {
        // サイドバー内だけを送る。scrollIntoView は祖先（＝ページ本体）も動かしてしまう。
        const r = sub.getBoundingClientRect();
        const box = toc.getBoundingClientRect();
        if (r.top < box.top + 24 || r.bottom > box.bottom - 24) {
          toc.scrollTop += r.top - box.top - toc.clientHeight / 2;
        }
      }
    }
  }

  let ticking = false;
  addEventListener('scroll', () => {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(() => { ticking = false; syncCurrent(); });
  }, { passive: true });

  /* ── 検索 ───────────────────────────────── */
  function clearMarks(el) {
    $$('mark', el).forEach(m => {
      m.replaceWith(document.createTextNode(m.textContent));
    });
    el.normalize();
  }

  function highlight(el, needle) {
    const re = new RegExp(needle.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi');
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
      acceptNode(n) {
        if (!n.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
        if (n.parentElement.closest('script,style,mark,input,output')) return NodeFilter.FILTER_REJECT;
        return re.test(n.nodeValue) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
      },
    });
    const hits = [];
    while (walker.nextNode()) hits.push(walker.currentNode);
    hits.forEach(node => {
      const frag = document.createDocumentFragment();
      let last = 0;
      node.nodeValue.replace(re, (m, idx) => {
        if (idx > last) frag.append(node.nodeValue.slice(last, idx));
        const mk = document.createElement('mark');
        mk.textContent = m;
        frag.append(mk);
        last = idx + m.length;
        return m;
      });
      if (last < node.nodeValue.length) frag.append(node.nodeValue.slice(last));
      node.replaceWith(frag);
    });
  }

  function applyFilter() {
    const doc = $(`.doc[data-lang="${lang}"]`);
    if (!doc) return;
    const needle = query.trim().toLowerCase();
    const idx = DATA.index[lang];
    let shown = 0;

    $$('.sec', doc).forEach(sec => {
      if (!pristine.has(sec)) pristine.set(sec, sec.innerHTML);
      const rec = idx.find(r => r.id === sec.dataset.sid);
      const hit = !needle || (rec && rec.text.toLowerCase().includes(needle));
      sec.hidden = !hit;
      if (hit) shown++;
    });

    $$('.sec', doc).forEach(sec => {
      if (sec.hidden) return;
      clearMarks(sec);
      if (needle) highlight(sec, query.trim());
    });

    $$('.toc a[data-sid]').forEach(a => {
      const rec = idx.find(r => r.id === a.dataset.sid);
      const hit = !needle || (rec && rec.text.toLowerCase().includes(needle));
      a.closest('li').hidden = !hit;
    });

    noHit.hidden = shown > 0;
    if (!noHit.hidden) noHit.textContent = T[lang].noHit.replace('%s', query.trim());
    qClear.hidden = !query;
    bindDemos();
  }

  let qTimer = null;
  qInput.addEventListener('input', () => {
    query = qInput.value;
    clearTimeout(qTimer);
    qTimer = setTimeout(() => { applyFilter(); observeHeadings(); }, 140);
  });
  qClear.addEventListener('click', () => {
    qInput.value = ''; query = ''; applyFilter(); observeHeadings(); qInput.focus();
  });
  addEventListener('keydown', e => {
    if (e.key === '/' && !/^(INPUT|TEXTAREA)$/.test(document.activeElement.tagName)) {
      e.preventDefault(); qInput.focus(); qInput.select();
    }
    if (e.key === 'Escape') {
      if (!zoom.hidden) { zoom.hidden = true; return; }
      if (document.activeElement === qInput && query) {
        qInput.value = ''; query = ''; applyFilter();
      }
      toc.classList.remove('open');
    }
  });

  /* ── 実演デモ ───────────────────────────── */
  const json = (fig, key) => { try { return JSON.parse(fig.dataset[key] || 'null'); } catch { return null; } };

  function framesOf(fig) {
    const full = fig.dataset.view === 'full' ? json(fig, 'framesFull') : null;
    return full || json(fig, 'frames') || [];
  }

  function setFrame(fig, i) {
    const list = framesOf(fig);
    const img = $('.stage .fr', fig);
    if (img && list[i]) img.src = list[i];
    fig.dataset.cur = i;
    $$('.frame-tag .tag', fig).forEach(t => t.classList.toggle('on', +t.dataset.i === i));
    $$('.sw', fig).forEach(b => b.classList.toggle('on', +b.dataset.i === i));
    const range = $('input[type="range"]', fig);
    const out = $('output', fig);
    if (range) range.value = i;
    if (out) out.textContent = (json(fig, 'values') || [])[i] ?? i;
  }

  // デモが近づいたらコマ画像を先読みする（切り替えたときに白く飛ばないように）
  const preloadSeen = new WeakSet();
  const preloader = new IntersectionObserver(entries => {
    entries.forEach(e => {
      if (!e.isIntersecting || preloadSeen.has(e.target)) return;
      preloadSeen.add(e.target);
      [...(json(e.target, 'frames') || []), ...(json(e.target, 'framesFull') || [])]
        .forEach(src => { const im = new Image(); im.src = src; });
    });
  }, { rootMargin: '400px 0px' });

  function bindDemos() {
    $$('.demo').forEach(fig => {
      if (fig.dataset.bound) return;
      fig.dataset.bound = '1';
      preloader.observe(fig);

      const range = $('input[type="range"]', fig);
      if (range) range.addEventListener('input', () => setFrame(fig, +range.value));

      $$('.sw', fig).forEach(btn => {
        btn.addEventListener('click', () => setFrame(fig, +btn.dataset.i));
      });

      $$('.viewtog button', fig).forEach(btn => {
        btn.addEventListener('click', () => {
          $$('.viewtog button', fig).forEach(b => b.classList.toggle('on', b === btn));
          fig.dataset.view = btn.dataset.view;
          setFrame(fig, +(fig.dataset.cur ?? fig.dataset.start ?? 0));
        });
      });
    });
  }

  /* ── 変更前 / 変更後 ─────────────────────── */
  function bindBeforeAfter() {
    $$('.ba-stage').forEach(stage => {
      if (stage.dataset.bound) return;
      stage.dataset.bound = '1';
      const handle = $('.ba-handle', stage);
      const set = pct => {
        const p = Math.max(0, Math.min(100, pct));
        stage.style.setProperty('--p', p + '%');
        handle.setAttribute('aria-valuenow', Math.round(p));
      };
      const fromEvent = e => {
        const r = stage.getBoundingClientRect();
        set(((e.clientX - r.left) / r.width) * 100);
      };
      let dragging = false;
      stage.addEventListener('pointerdown', e => {
        dragging = true; stage.setPointerCapture(e.pointerId); fromEvent(e); e.preventDefault();
      });
      stage.addEventListener('pointermove', e => { if (dragging) fromEvent(e); });
      stage.addEventListener('pointerup', () => { dragging = false; });
      stage.addEventListener('pointercancel', () => { dragging = false; });
      handle.addEventListener('keydown', e => {
        const cur = parseFloat(handle.getAttribute('aria-valuenow')) || 50;
        if (e.key === 'ArrowLeft') { set(cur - 4); e.preventDefault(); }
        if (e.key === 'ArrowRight') { set(cur + 4); e.preventDefault(); }
      });
    });
  }

  /* ── 画像ズーム ─────────────────────────── */
  document.addEventListener('click', e => {
    const shot = e.target.closest('.shot');
    if (shot) {
      const img = $('img', zoom);
      img.src = shot.dataset.zoom;
      img.alt = $('img', shot)?.alt || '';
      zoom.hidden = false;
      return;
    }
    if (e.target.closest('.zoom')) zoom.hidden = true;
  });

  /* ── 狭い画面の目次 ─────────────────────── */
  $('#menu').addEventListener('click', () => toc.classList.toggle('open'));
  toc.addEventListener('click', e => {
    if (e.target.closest('a')) toc.classList.remove('open');
  });

  /* ── 起動 ───────────────────────────────── */
  applyLang(lang);
  bindDemos();
  bindBeforeAfter();
  if (location.hash) {
    const id = decodeURIComponent(location.hash.slice(1));
    const go = () => {
      const el = document.getElementById(id);
      if (!el) return;
      const y = el.getBoundingClientRect().top + scrollY - ($('.top')?.offsetHeight || 56) - 12;
      scrollTo({ top: y, behavior: 'instant' });
      syncCurrent();
    };
    requestAnimationFrame(go);
    addEventListener('load', go, { once: true });
  }
})();
