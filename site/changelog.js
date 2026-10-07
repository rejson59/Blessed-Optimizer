(() => {
  'use strict';

  document.documentElement.classList.add('js-ready');

  const year = document.getElementById('current-year');
  if (year) year.textContent = String(new Date().getFullYear());

  const backToTop = document.getElementById('back-to-top');
  if (backToTop) {
    backToTop.addEventListener('click', () => {
      const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
      window.scrollTo({ top: 0, behavior: reduceMotion ? 'auto' : 'smooth' });
    });
  }

  // Reveal sections as they enter the viewport, mirroring index.html behavior.
  const revealItems = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window) {
    const revealObserver = new IntersectionObserver((entries, observer) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          observer.unobserve(entry.target);
        }
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -28px 0px' });
    revealItems.forEach((item) => revealObserver.observe(item));
  } else {
    revealItems.forEach((item) => item.classList.add('is-visible'));
  }

  // The changelog is rendered from the public GitHub Releases API. Release notes are
  // attacker-controlled text in theory, so every value goes through textContent —
  // no HTML is ever built from the response.
  const list = document.getElementById('changelog-list');
  const status = document.getElementById('changelog-status');
  if (!list || !status) return;

  const releasesApiUrl = 'https://api.github.com/repos/rejson59/Blessed-Optimizer/releases?per_page=20';
  const releasesPageUrl = 'https://github.com/rejson59/Blessed-Optimizer/releases';

  function setStatus(message, isError) {
    status.textContent = message;
    status.classList.toggle('is-error', Boolean(isError));
  }

  function formatDate(iso) {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '';
    return date.toLocaleDateString('pl-PL', { day: 'numeric', month: 'long', year: 'numeric' });
  }

  function renderRelease(release) {
    const article = document.createElement('article');
    article.className = 'changelog-entry glass-panel';

    const header = document.createElement('header');
    header.className = 'changelog-entry-header';

    const heading = document.createElement('div');
    heading.className = 'changelog-entry-heading';
    const title = document.createElement('h3');
    const releaseName = typeof release.name === 'string' ? release.name.trim() : '';
    title.textContent = releaseName || release.tag_name || 'Wydanie';
    const tag = document.createElement('span');
    tag.className = 'changelog-tag';
    tag.textContent = release.tag_name || '';
    heading.append(title, tag);

    const meta = document.createElement('div');
    meta.className = 'changelog-entry-meta';
    if (release.published_at) {
      const date = document.createElement('time');
      date.dateTime = release.published_at;
      date.textContent = formatDate(release.published_at);
      meta.appendChild(date);
    }
    if (release.prerelease) {
      const badge = document.createElement('span');
      badge.className = 'changelog-prerelease';
      badge.textContent = 'wersja testowa';
      meta.appendChild(badge);
    }

    header.append(heading, meta);
    article.appendChild(header);

    const body = document.createElement('div');
    body.className = 'changelog-body';
    const text = typeof release.body === 'string' ? release.body.trim() : '';
    if (text) {
      text.split(/\n{2,}/).forEach((block) => {
        const trimmed = block.trim();
        if (!trimmed) return;
        if (/^#{1,6}\s/.test(trimmed)) {
          const subheading = document.createElement('h4');
          subheading.textContent = trimmed.replace(/^#{1,6}\s*/, '');
          body.appendChild(subheading);
          return;
        }
        const paragraph = document.createElement('p');
        paragraph.textContent = trimmed.replace(/\s*\n\s*/g, ' ');
        body.appendChild(paragraph);
      });
    } else {
      const paragraph = document.createElement('p');
      paragraph.textContent = 'To wydanie nie ma opisu zmian.';
      body.appendChild(paragraph);
    }
    article.appendChild(body);
    return article;
  }

  fetch(releasesApiUrl, {
    headers: { Accept: 'application/vnd.github+json' },
    cache: 'no-store'
  })
    .then((response) => {
      if (!response.ok) throw new Error(`http-${response.status}`);
      return response.json();
    })
    .then((releases) => {
      if (!Array.isArray(releases) || releases.length === 0) {
        list.hidden = true;
        setStatus('Nie ma jeszcze opublikowanych wydań. Blessed dopiero startuje — wróć tu wkrótce.', false);
        return;
      }
      list.replaceChildren(...releases.map(renderRelease));
      setStatus('Lista wydań pobrana z GitHub — od najnowszego.', false);
    })
    .catch(() => {
      list.hidden = true;
      setStatus('Nie udało się pobrać listy wydań. ', true);
      const link = document.createElement('a');
      link.href = releasesPageUrl;
      link.textContent = 'Otwórz GitHub Releases ↗';
      status.appendChild(link);
    });
})();
