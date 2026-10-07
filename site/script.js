(() => {
  'use strict';

  document.documentElement.classList.add('js-ready');

  const toast = document.getElementById('site-toast');
  let toastTimer = 0;
  function showToast(message) {
    if (!toast) return;
    toast.textContent = message;
    toast.classList.add('is-visible');
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => toast.classList.remove('is-visible'), 3600);
  }

  const year = document.getElementById('current-year');
  if (year) year.textContent = String(new Date().getFullYear());

  // Only offer a direct download when GitHub confirms the latest release has the installer.
  // The releases page is a safe fallback, so an unpublished release never produces a 404.
  const downloadCta = document.getElementById('download-cta');
  const downloadLabel = document.getElementById('download-label');
  const downloadNote = document.getElementById('download-note');
  const releasesPageUrl = 'https://github.com/rejson59/Blessed-Optimizer/releases';
  const latestReleaseApiUrl = 'https://api.github.com/repos/rejson59/Blessed-Optimizer/releases/latest';
  const installerAssetName = 'BlessedOptimizer-Setup.exe';

  function showReleasePage(message) {
    if (downloadCta) downloadCta.href = releasesPageUrl;
    if (downloadLabel) downloadLabel.textContent = 'Sprawdź wydania';
    if (downloadNote) downloadNote.textContent = message;
  }

  if (downloadCta && downloadLabel && downloadNote) {
    showReleasePage('Sprawdzam dostępność instalatora. Jeśli go nie ma, przycisk otworzy stronę GitHub Releases.');
    const releaseCheckController = new AbortController();
    const releaseCheckTimeout = window.setTimeout(() => releaseCheckController.abort(), 7000);
    fetch(latestReleaseApiUrl, {
      headers: { Accept: 'application/vnd.github+json' },
      cache: 'no-store',
      signal: releaseCheckController.signal
    })
      .then((response) => {
        if (!response.ok) {
          const error = new Error(response.status === 404 ? 'no-release' : 'release-check-failed');
          throw error;
        }
        return response.json();
      })
      .then((release) => {
        const installer = Array.isArray(release.assets)
          ? release.assets.find((asset) => asset.name === installerAssetName)
          : null;
        let assetUrl;
        try {
          assetUrl = new URL(installer?.browser_download_url);
        } catch (_error) {
          assetUrl = null;
        }

        const isTrustedInstallerUrl = assetUrl
          && assetUrl.protocol === 'https:'
          && assetUrl.hostname === 'github.com'
          && assetUrl.pathname.startsWith('/rejson59/Blessed-Optimizer/releases/download/')
          && assetUrl.pathname.endsWith(`/${installerAssetName}`);

        if (!isTrustedInstallerUrl) {
          showReleasePage('Nie znaleziono instalatora w najnowszym wydaniu. Otwórz GitHub Releases, aby sprawdzić dostępne pliki.');
          return;
        }

        downloadCta.href = assetUrl.href;
        downloadLabel.textContent = 'Pobierz Blessed Optimizer';
        const versionTag = typeof release.tag_name === 'string' && release.tag_name ? release.tag_name : 'najnowsza';
        downloadNote.textContent = `Windows 10/11 x64 · samodzielny plik .exe · wersja ${versionTag} — zawsze najnowsze wydanie.`;
      })
      .catch((error) => {
        const message = error?.message === 'no-release'
          ? 'Nie ma jeszcze publicznego wydania z instalatorem. Przycisk otworzy stronę GitHub Releases.'
          : 'Nie udało się potwierdzić dostępności instalatora. Przycisk otworzy stronę GitHub Releases.';
        showReleasePage(message);
      })
      .finally(() => window.clearTimeout(releaseCheckTimeout));
  }

  // The two top-level sections behave as accessible tabs and can be linked directly.
  const siteTabButtons = Array.from(document.querySelectorAll('[data-site-tab]'));
  const tabPanels = {
    about: document.getElementById('panel-about'),
    simulator: document.getElementById('panel-simulator')
  };
  let activeSiteTab = '';

  function selectSiteTab(name, options = {}) {
    const panel = tabPanels[name];
    if (!panel || !siteTabButtons.length) return;
    const changed = activeSiteTab !== name;
    activeSiteTab = name;

    siteTabButtons.forEach((button) => {
      const selected = button.dataset.siteTab === name;
      button.classList.toggle('is-active', selected);
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
    });
    Object.entries(tabPanels).forEach(([key, item]) => {
      if (!item) return;
      const selected = key === name;
      item.hidden = !selected;
      item.classList.toggle('is-active', selected);
      if (selected && changed) {
        item.classList.remove('is-entering');
        window.requestAnimationFrame(() => item.classList.add('is-entering'));
      }
    });

    if (options.updateUrl && window.history && window.history.replaceState) {
      const hash = name === 'simulator' ? '#symulator' : '#program';
      window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`);
    }
    if ((changed || options.forceScroll) && options.scroll !== false) {
      window.scrollTo({ top: 0, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    }
    if (changed) {
      const firstVisible = panel.querySelector('.reveal');
      if (firstVisible) firstVisible.classList.add('is-visible');
    }
  }

  siteTabButtons.forEach((button) => {
    button.addEventListener('click', () => selectSiteTab(button.dataset.siteTab, { updateUrl: true }));
    button.addEventListener('keydown', (event) => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = siteTabButtons.indexOf(button);
      const nextIndex = event.key === 'Home' ? 0
        : event.key === 'End' ? siteTabButtons.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : -1) + siteTabButtons.length) % siteTabButtons.length;
      siteTabButtons[nextIndex].focus();
      selectSiteTab(siteTabButtons[nextIndex].dataset.siteTab, { updateUrl: true });
    });
  });
  document.querySelectorAll('[data-open-tab]').forEach((button) => {
    button.addEventListener('click', (event) => {
      event.preventDefault();
      const forceScroll = button.classList.contains('brand') || button.classList.contains('back-to-top');
      selectSiteTab(button.dataset.openTab, { updateUrl: true, forceScroll });
    });
  });
  selectSiteTab(window.location.hash === '#symulator' ? 'simulator' : 'about', { scroll: false });

  // Keep the native operating-system pointer visible and update the wing position in the same event.
  // The decorative wings are available only for a fine, hover-capable pointer; touch and reduced-motion
  // users keep the site's unchanged behavior and never get an animated cursor overlay.
  const cursorWings = document.getElementById('cursor-wings');
  const finePointer = window.matchMedia?.('(hover: hover) and (pointer: fine)');
  const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  if (cursorWings && finePointer?.matches && !reducedMotion) {
    let blessingTimer = 0;

    document.addEventListener('pointermove', (event) => {
      if (event.pointerType && event.pointerType !== 'mouse') return;
      // No interpolation or animation frame: anchor directly to the latest pointer coordinates.
      cursorWings.style.transform = `translate3d(${event.clientX}px, ${event.clientY}px, 0)`;
      cursorWings.classList.add('is-visible');
      const target = event.target instanceof Element ? event.target : null;
      const isInteractive = Boolean(target?.closest('a,button,[role="button"],[role="switch"],summary,input,select,textarea'));
      cursorWings.classList.toggle('is-hovering', isInteractive);
    });

    document.addEventListener('pointerdown', (event) => {
      if (event.pointerType && event.pointerType !== 'mouse') return;
      cursorWings.classList.add('is-pressing');
      cursorWings.classList.remove('is-blessing');
      // Restart only the decorative click sparkle; pointer placement never waits for a frame.
      void cursorWings.offsetWidth;
      cursorWings.classList.add('is-blessing');
      window.clearTimeout(blessingTimer);
      blessingTimer = window.setTimeout(() => cursorWings.classList.remove('is-blessing'), 700);
    });
    document.addEventListener('pointerup', () => cursorWings.classList.remove('is-pressing'));
    document.addEventListener('pointercancel', () => cursorWings.classList.remove('is-pressing'));
    document.addEventListener('pointerout', (event) => {
      if (event.relatedTarget) return;
      cursorWings.classList.remove('is-visible', 'is-hovering', 'is-pressing');
    });
    window.addEventListener('blur', () => {
      cursorWings.classList.remove('is-visible', 'is-hovering', 'is-pressing', 'is-blessing');
    });
  }

  // On a phone, share the preview instead of offering a desktop installer.
  const shareButton = document.getElementById('share-link');
  if (shareButton) {
    shareButton.addEventListener('click', async () => {
      const shareData = { title: 'Blessed Optimizer — podgląd', url: window.location.href };
      if (navigator.share) {
        try {
          await navigator.share(shareData);
          showToast('Link do podglądu jest gotowy do otwarcia na komputerze.');
          return;
        } catch (error) {
          if (error && error.name === 'AbortError') return;
        }
      }
      try {
        await navigator.clipboard.writeText(window.location.href);
        showToast('Skopiowano link. Otwórz go później na komputerze.');
      } catch (_error) {
        const input = document.createElement('textarea');
        input.value = window.location.href;
        input.setAttribute('readonly', '');
        input.style.position = 'fixed';
        input.style.opacity = '0';
        document.body.appendChild(input);
        input.select();
        try { document.execCommand('copy'); } catch (_copyError) { /* Clipboard access may be blocked. */ }
        input.remove();
        showToast('Skopiuj adres tej strony i otwórz go na komputerze.');
      }
    });
  }

  // Faithful 1:1 mock of the Blessed Optimizer app window inside the Simulator tab.
  // Every page mirrors the real app: same titles, same flows, demo data only.
  const mockWindow = document.getElementById('mock-window');
  const mockPage = document.getElementById('mock-page');
  const mockViewTitle = document.getElementById('mock-view-title');
  const mockViewCrumb = document.getElementById('mock-view-crumb');
  const mockGuideMessage = document.getElementById('mock-guide-message');
  const mockNavButtons = Array.from(document.querySelectorAll('[data-mock-view]'));
  const mockCareBadge = document.getElementById('mock-care-badge');
  const mockStatusPill = document.getElementById('mock-status-pill');
  const mockStatusPillText = document.getElementById('mock-status-pill-text');
  const mockViews = {
    care: { title: 'Blessed czuwa', crumb: 'BLESSED CZUWA', template: 'mock-template-care', message: 'Znalazłem 2 rzeczy warte zajęcia się.' },
    gaming: { title: 'Strefa gracza', crumb: 'STREFA GRACZA', template: 'mock-template-gaming', message: 'Włącz czuwanie, aby obserwować użycie procesora i pamięci podczas gry.' },
    processes: { title: 'Procesy', crumb: 'PROCESY', template: 'mock-template-processes', message: 'Pokażę zużycie CPU i pamięci przez każdy proces — czytelnie i na żywo.' },
    connections: { title: 'Połączenia', crumb: 'POŁĄCZENIA', template: 'mock-template-connections', message: 'Sprawdzę stan kart sieciowych i wykonam test ping. Każdą zmianę zatwierdzasz Ty.' },
    proposals: { title: 'Propozycje', crumb: 'PROPOZYCJE', template: 'mock-template-proposals', message: 'Podpowiem, co warto sprawdzić, i zaprowadzę Cię prosto do właściwych ustawień Windows.' },
    history: { title: 'Historia', crumb: 'HISTORIA', template: 'mock-template-history', message: 'Ostatnie przeglądy są zapisywane wyłącznie na tym komputerze.' },
    startup: { title: 'Autostart', crumb: 'AUTOSTART', template: 'mock-template-startup', message: 'Przejrzyj wpisy autostartu swojego konta. Każda zmiana ma zapisaną kopię do przywrócenia.' },
    cleanup: { title: 'Porządki', crumb: 'PORZĄDKI', template: 'mock-template-cleanup', message: 'Przejrzyj aplikacje swojego konta i odinstaluj te, których nie używasz. Aplikacje wracają przez Microsoft Store.' },
    power: { title: 'Zasilanie', crumb: 'ZASILANIE', template: 'mock-template-power', message: 'Dostrój plan zasilania. Każdą zmianę potwierdzasz Ty i zawsze możesz ją cofnąć.' },
    personalization: { title: 'Wygląd', crumb: 'WYGLĄD', template: 'mock-template-personalization', message: 'Dopasuj motyw i kolor Blessed. Ustawienia systemowe Windows otworzysz osobno.' },
    settings: { title: 'Ustawienia Blessed', crumb: 'USTAWIENIA', template: 'mock-template-settings', message: 'Wybierz swój priorytet i zdecyduj, które zadania Blessed może wykonywać automatycznie.' }
  };
  const mockPriorities = {
    gaming: { label: 'Granie i maksymalna płynność', promise: 'Pilnuję, żeby procesor i pamięć były gotowe na grę, zanim ją odpalisz.' },
    work: { label: 'Praca i skupienie', promise: 'Pilnuję, żeby nic nie zwalniało Ci pracy i żeby komputer startował szybko.' },
    battery: { label: 'Długa praca na baterii', promise: 'Pilnuję zużycia energii i podpowiadam, co niepotrzebnie zjada baterię.' },
    quiet: { label: 'Cisza i chłód', promise: 'Pilnuję obciążenia, żeby wentylatory nie miały powodu do pracy.' }
  };
  let mockWatchTimer = 0;

  function mockHeadline(count) {
    if (count === 0) return 'Wszystko gra — nic nie wymaga Twojej uwagi.';
    if (count === 1) return 'Znalazłem 1 rzecz wartą zajęcia się.';
    return `Znalazłem ${count} rzeczy warte zajęcia się.`;
  }

  function mockProblemCount() {
    if (!mockPage) return 0;
    return Array.from(mockPage.querySelectorAll('.mock-finding:not(.is-done)'))
      .filter((card) => card.dataset.severity === 'warning' || card.dataset.severity === 'critical').length;
  }

  function mockRefreshCareState() {
    const count = mockProblemCount();
    const careActive = Boolean(mockPage?.querySelector('#mock-findings'));
    const time = new Date().toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' });
    if (mockCareBadge) {
      mockCareBadge.textContent = String(count);
      mockCareBadge.style.display = count > 0 ? '' : 'none';
    }
    if (mockStatusPillText) mockStatusPillText.textContent = count === 0 ? 'Blessed czuwa · czysto' : `Blessed znalazł ${count}`;
    const oneClickButton = document.getElementById('mock-oneclick-button');
    if (oneClickButton && careActive) oneClickButton.style.display = count > 0 ? '' : 'none';
    if (careActive) {
      const careStatus = mockPage.querySelector('#mock-care-status');
      const specTime = mockPage.querySelector('#mock-spec-time');
      if (careStatus) careStatus.textContent = `${mockHeadline(count)} · przegląd o ${time}`;
      if (specTime) specTime.textContent = time;
      if (mockGuideMessage) mockGuideMessage.textContent = mockHeadline(count);
    }
  }

  function setMockView(viewName) {
    const view = mockViews[viewName];
    const template = view && document.getElementById(view.template);
    const selectedButton = mockNavButtons.find((button) => button.dataset.mockView === viewName);
    if (!mockPage || !view || !template || !selectedButton) return;
    window.clearInterval(mockWatchTimer);
    mockWatchTimer = 0;
    mockPage.classList.remove('view-switch');
    mockPage.replaceChildren(template.content.cloneNode(true));
    window.requestAnimationFrame(() => mockPage.classList.add('view-switch'));
    mockPage.setAttribute('aria-labelledby', selectedButton.id);
    mockPage.querySelectorAll('.mock-pill').forEach((button) => {
      button.setAttribute('aria-pressed', String(button.classList.contains('is-selected')));
    });
    if (mockViewTitle) mockViewTitle.textContent = view.title;
    if (mockViewCrumb) mockViewCrumb.textContent = view.crumb;
    if (mockGuideMessage) mockGuideMessage.textContent = view.message;
    mockNavButtons.forEach((button) => {
      const selected = button === selectedButton;
      button.classList.toggle('is-active', selected);
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
      if (selected) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
    if (viewName === 'care') mockRefreshCareState();
  }

  mockNavButtons.forEach((button) => {
    button.addEventListener('click', () => setMockView(button.dataset.mockView));
    button.addEventListener('keydown', (event) => {
      if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = mockNavButtons.indexOf(button);
      const next = event.key === 'Home' ? 0
        : event.key === 'End' ? mockNavButtons.length - 1
          : (index + (['ArrowRight', 'ArrowDown'].includes(event.key) ? 1 : -1) + mockNavButtons.length) % mockNavButtons.length;
      mockNavButtons[next].focus();
      setMockView(mockNavButtons[next].dataset.mockView);
    });
  });
  setMockView('care');

  function mockRunScan() {
    const careStatus = mockPage ? mockPage.querySelector('#mock-care-status') : null;
    if (careStatus) {
      careStatus.textContent = 'Sprawdzam Twój komputer…';
      careStatus.classList.add('is-scanning');
    }
    if (mockStatusPill) mockStatusPill.classList.add('is-scanning');
    window.setTimeout(() => {
      const liveCareStatus = mockPage ? mockPage.querySelector('#mock-care-status') : null;
      if (liveCareStatus) liveCareStatus.classList.remove('is-scanning');
      if (mockStatusPill) mockStatusPill.classList.remove('is-scanning');
      mockRefreshCareState();
      showToast('Przegląd gotowy — to makieta, więc wyniki są przykładowe.');
    }, 1200);
  }

  function mockToggleWatch(button) {
    const enabled = button.getAttribute('aria-checked') !== 'true';
    button.setAttribute('aria-checked', String(enabled));
    button.classList.toggle('is-on', enabled);
    const state = button.closest('.mock-setting-row')?.querySelector('.mock-setting-state');
    if (state) state.textContent = enabled ? 'Włączone w makiecie' : 'Wyłączone w makiecie';
    const cpuValue = mockPage.querySelector('[data-meter="cpu"]');
    const ramValue = mockPage.querySelector('[data-meter="ram"]');
    const cpuBar = mockPage.querySelector('[data-meter-bar="cpu"]');
    const ramBar = mockPage.querySelector('[data-meter-bar="ram"]');
    window.clearInterval(mockWatchTimer);
    mockWatchTimer = 0;
    if (!enabled) {
      if (cpuValue) cpuValue.textContent = '—';
      if (ramValue) ramValue.textContent = '—';
      if (cpuBar) cpuBar.style.width = '0%';
      if (ramBar) ramBar.style.width = '0%';
      showToast('Czuwanie wyłączone — makieta przestała „mierzyć”.');
      return;
    }
    const tick = () => {
      const cpu = 28 + Math.round(Math.random() * 22);
      const ram = 52 + Math.round(Math.random() * 14);
      if (cpuValue) cpuValue.textContent = `${cpu}%`;
      if (ramValue) ramValue.textContent = `${ram}%`;
      if (cpuBar) cpuBar.style.width = `${cpu}%`;
      if (ramBar) ramBar.style.width = `${ram}%`;
    };
    tick();
    mockWatchTimer = window.setInterval(tick, 2000);
    showToast('Czuwanie włączone — makieta pokazuje przykładowe odczyty co 2 sekundy.');
  }

  function mockRunPing() {
    const result = document.getElementById('mock-ping-result');
    const button = document.getElementById('mock-ping');
    if (!result || !button || button.disabled) return;
    button.disabled = true;
    result.textContent = 'Wysyłam 10 prób do 1.1.1.1…';
    window.setTimeout(() => {
      const average = 18 + Math.round(Math.random() * 14);
      const jitter = 2 + Math.round(Math.random() * 4);
      result.textContent = `Średnia ${average} ms · wahania ±${jitter} ms · utracone 0%`;
      button.disabled = false;
    }, 1100);
  }

  function mockApplyPower() {
    const ac = document.getElementById('mock-power-ac');
    const dc = document.getElementById('mock-power-dc');
    const nowAc = document.getElementById('mock-power-now-ac');
    const nowDc = document.getElementById('mock-power-now-dc');
    const feedback = document.getElementById('mock-power-feedback');
    if (!ac || !nowAc || !feedback) return;
    const acValue = ac.value.replace(' · bieżąca wartość', '');
    const dcValue = dc ? dc.value.replace(' · bieżąca wartość', '') : '';
    nowAc.textContent = acValue;
    if (nowDc && dcValue) nowDc.textContent = dcValue;
    feedback.textContent = 'W aplikacji Windows poprosiłby teraz o zgodę (UAC), a oryginalne wartości zapisałyby się do przywrócenia.';
  }

  function mockRunOneClick() {
    const plan = document.getElementById('mock-oneclick-plan');
    const progress = document.getElementById('mock-oneclick-progress');
    const results = document.getElementById('mock-oneclick-results');
    if (!plan || !progress || !results) return;
    plan.hidden = true;
    progress.hidden = false;
    const steps = Array.from(progress.querySelectorAll('li'));
    steps.forEach((step) => step.classList.remove('is-active', 'is-done'));
    let index = 0;
    const tick = () => {
      if (index > 0) steps[index - 1].classList.replace('is-active', 'is-done');
      if (index >= steps.length) {
        progress.hidden = true;
        results.hidden = false;
        return;
      }
      steps[index].classList.add('is-active');
      index += 1;
      window.setTimeout(tick, 750);
    };
    tick();
  }

  function mockFinishOneClick() {
    const panel = document.getElementById('mock-oneclick');
    if (panel) panel.hidden = true;
    mockPage.querySelectorAll('.mock-finding').forEach((card) => {
      if (!['temp-files', 'recycle-bin', 'power-cpu-limit'].includes(card.dataset.finding)) return;
      card.classList.add('is-done');
      const tag = card.querySelector('.mock-finding-tag');
      if (tag) {
        tag.textContent = 'W PORZĄDKU';
        tag.style.setProperty('--sev', '#78D7B5');
      }
      const severity = card.querySelector('.mock-severity');
      if (severity) severity.classList.add('mock-severity-good');
      const action = card.querySelector('.mock-finding-body .mock-button');
      if (action) {
        action.disabled = true;
        action.textContent = '✓ Załatwione';
        action.classList.remove('mock-button-primary', 'mock-button-secondary');
        action.classList.add('mock-button-done');
      }
    });
    const specFreed = mockPage.querySelector('#mock-spec-freed');
    if (specFreed) specFreed.textContent = '13,5 GB';
    mockRefreshCareState();
    showToast('Jednym kliknięciem — w aplikacji dokładnie tak wygląda podsumowanie.');
  }

  function mockAppxBoxes() {
    return Array.from(mockPage.querySelectorAll('[data-mock-appx]'));
  }

  function mockRefreshAppxCounter() {
    const counter = document.getElementById('mock-cleanup-counter');
    if (!counter) return;
    const boxes = mockAppxBoxes().filter((box) => !box.closest('.mock-appx').classList.contains('is-removed'));
    const checked = boxes.filter((box) => box.checked).length;
    counter.textContent = `${boxes.length} aplikacje · ${checked} zaznaczonych`;
  }

  function mockSetAllAppx(checked) {
    mockAppxBoxes().forEach((box) => {
      if (!box.closest('.mock-appx').classList.contains('is-removed')) box.checked = checked;
    });
    mockRefreshAppxCounter();
  }

  function mockShowUninstallConfirm() {
    const confirm = document.getElementById('mock-cleanup-confirm');
    const text = document.getElementById('mock-cleanup-confirm-text');
    if (!confirm) return;
    const boxes = mockAppxBoxes().filter((box) => box.checked && !box.closest('.mock-appx').classList.contains('is-removed'));
    if (boxes.length === 0) {
      showToast('Zaznacz najpierw aplikacje, które chcesz odinstalować.');
      return;
    }
    if (text) {
      const names = boxes.slice(0, 3).map((box) => box.closest('.mock-appx').querySelector('strong')?.textContent || '');
      const suffix = boxes.length > 3 ? ` i jeszcze ${boxes.length - 3}` : '';
      text.textContent = `Odinstalować ${boxes.length} aplikacji bieżącego konta? ${names.join(', ')}${suffix}. Aplikacje wgrasz z powrotem przez Microsoft Store.`;
    }
    confirm.hidden = false;
  }

  function mockRunUninstall() {
    const confirm = document.getElementById('mock-cleanup-confirm');
    const summary = document.getElementById('mock-cleanup-summary');
    if (confirm) confirm.hidden = true;
    const boxes = mockAppxBoxes().filter((box) => box.checked && !box.closest('.mock-appx').classList.contains('is-removed'));
    boxes.forEach((box) => {
      const row = box.closest('.mock-appx');
      if (row) row.classList.add('is-removed');
      box.checked = false;
      box.disabled = true;
    });
    mockRefreshAppxCounter();
    if (summary) {
      summary.hidden = false;
      summary.textContent = `Gotowe. Odinstalowano ${boxes.length} aplikacji z Twojego konta — wgrasz je z powrotem przez Microsoft Store, kiedy zechcesz.`;
    }
    showToast('Makieta: aplikacje „odinstalowane”. W aplikacji wykona to Remove-AppxPackage dla Twojego konta.');
  }

  function mockHandleClick(event) {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;

    const toastElement = target.closest('[data-toast]');
    if (toastElement) {
      showToast(toastElement.dataset.toast);
      return;
    }

    const gotoElement = target.closest('[data-mock-goto]');
    if (gotoElement) {
      setMockView(gotoElement.dataset.mockGoto);
      return;
    }

    const muteButton = target.closest('[data-mock-mute]');
    if (muteButton) {
      const card = muteButton.closest('.mock-finding');
      if (card) {
        const wasProblem = card.dataset.severity === 'warning' || card.dataset.severity === 'critical';
        card.remove();
        if (wasProblem) mockRefreshCareState();
        showToast('Sprawa wyciszona — w aplikacji znika z przeglądu na stałe.');
      }
      return;
    }

    if (target.closest('#mock-scan')) {
      mockRunScan();
      return;
    }
    if (target.closest('#mock-oneclick-button')) {
      const panel = document.getElementById('mock-oneclick');
      if (panel) panel.hidden = false;
      return;
    }
    if (target.closest('#mock-plan-run')) {
      mockRunOneClick();
      return;
    }
    if (target.closest('#mock-plan-cancel')) {
      const panel = document.getElementById('mock-oneclick');
      if (panel) panel.hidden = true;
      return;
    }
    if (target.closest('#mock-plan-done')) {
      mockFinishOneClick();
      return;
    }

    const watchSwitch = target.closest('[data-mock-watch]');
    if (watchSwitch) {
      mockToggleWatch(watchSwitch);
      return;
    }

    if (target.closest('#mock-ping')) {
      mockRunPing();
      return;
    }
    if (target.closest('#mock-apply-power')) {
      mockApplyPower();
      return;
    }

    if (target.closest('#mock-uninstall')) {
      mockShowUninstallConfirm();
      return;
    }
    if (target.closest('#mock-cleanup-run')) {
      mockRunUninstall();
      return;
    }
    if (target.closest('#mock-cleanup-cancel')) {
      const confirm = document.getElementById('mock-cleanup-confirm');
      if (confirm) confirm.hidden = true;
      return;
    }
    if (target.closest('[data-mock-check-all]')) {
      mockSetAllAppx(true);
      return;
    }
    if (target.closest('[data-mock-check-none]')) {
      mockSetAllAppx(false);
      return;
    }

    const themePill = target.closest('button[data-mock-theme]');
    if (themePill && mockWindow) {
      mockWindow.dataset.mockTheme = themePill.dataset.mockTheme;
      mockPage.querySelectorAll('[data-mock-theme]').forEach((button) => {
        const selected = button === themePill;
        button.classList.toggle('is-selected', selected);
        button.setAttribute('aria-pressed', String(selected));
      });
      return;
    }

    const accentPill = target.closest('button[data-mock-accent]');
    if (accentPill && mockWindow) {
      mockWindow.dataset.mockAccent = accentPill.dataset.mockAccent;
      mockPage.querySelectorAll('[data-mock-accent]').forEach((button) => {
        const selected = button === accentPill;
        button.classList.toggle('is-selected', selected);
        button.setAttribute('aria-pressed', String(selected));
      });
      return;
    }

    const priorityPill = target.closest('button[data-mock-priority]');
    if (priorityPill) {
      const priority = mockPriorities[priorityPill.dataset.mockPriority];
      if (priority) {
        const label = document.getElementById('mock-priority-label');
        const promise = document.getElementById('mock-priority-promise');
        if (label) label.textContent = priority.label;
        if (promise) promise.textContent = priority.promise;
      }
      mockPage.querySelectorAll('[data-mock-priority]').forEach((button) => {
        const selected = button === priorityPill;
        button.classList.toggle('is-selected', selected);
        button.setAttribute('aria-pressed', String(selected));
      });
      return;
    }

    const genericSwitch = target.closest('.mock-switch:not([disabled])');
    if (genericSwitch && !genericSwitch.hasAttribute('data-mock-watch')) {
      const enabled = genericSwitch.getAttribute('aria-checked') !== 'true';
      genericSwitch.setAttribute('aria-checked', String(enabled));
      genericSwitch.classList.toggle('is-on', enabled);
      const state = genericSwitch.closest('.mock-setting-row')?.querySelector('.mock-setting-state');
      if (state) state.textContent = enabled ? 'Włączone w makiecie' : 'Wyłączone w makiecie';
      if (genericSwitch.closest('.mock-startup-row')) {
        showToast(enabled
          ? 'Wpis włączony — w aplikacji Blessed najpierw zapisuje kopię do przywrócenia.'
          : 'Wpis wyłączony — kopia pozwoli go przywrócić jednym kliknięciem.');
      }
    }
  }

  if (mockPage) {
    mockPage.addEventListener('click', mockHandleClick);
    mockPage.addEventListener('change', (event) => {
      if (event.target instanceof HTMLInputElement && event.target.hasAttribute('data-mock-appx')) {
        mockRefreshAppxCounter();
      }
    });
  }

  document.querySelectorAll('.mock-try-item[data-mock-goto]').forEach((button) => {
    button.addEventListener('click', () => setMockView(button.dataset.mockGoto));
  });
  document.querySelectorAll('.mock-title-check[data-toast]').forEach((button) => {
    button.addEventListener('click', () => showToast(button.dataset.toast));
  });

  // Reveal sections as they enter the viewport. Hidden-tab sections reveal when shown.
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

  // Small interactive app preview in the About tab.
  const appPanel = document.getElementById('app-panel');
  const appTitle = document.getElementById('app-view-title');
  const appNavButtons = Array.from(document.querySelectorAll('[data-app-view]'));
  const appViews = {
    care: { title: 'Blessed czuwa', template: 'panel-care' },
    overview: { title: 'Twój przegląd', template: 'panel-overview' },
    gaming: { title: 'Gotowość do gry', template: 'panel-gaming' },
    diagnostics: { title: 'Odkryj możliwości', template: 'panel-diagnostics' },
    settings: { title: 'Twoje ustawienia', template: 'panel-settings' }
  };

  function setAppView(viewName) {
    const view = appViews[viewName];
    const template = view && document.getElementById(view.template);
    if (!appPanel || !view || !template) return;
    appPanel.classList.remove('view-switch');
    appPanel.replaceChildren(template.content.cloneNode(true));
    window.requestAnimationFrame(() => appPanel.classList.add('view-switch'));
    if (appTitle) appTitle.textContent = view.title;
    appNavButtons.forEach((button) => {
      const active = button.dataset.appView === viewName;
      button.classList.toggle('is-active', active);
      if (active) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
    const feedback = document.getElementById('scan-feedback');
    if (feedback) {
      feedback.textContent = '';
      feedback.classList.remove('is-visible');
    }
  }

  appNavButtons.forEach((button) => button.addEventListener('click', () => setAppView(button.dataset.appView)));
  setAppView('care');

  if (appPanel) {
    appPanel.addEventListener('click', (event) => {
      const toastButton = event.target.closest('[data-toast]');
      if (toastButton) {
        showToast(toastButton.dataset.toast);
        return;
      }
      const switchButton = event.target.closest('.mock-switch');
      if (!switchButton) return;
      const nextValue = switchButton.getAttribute('aria-checked') !== 'true';
      switchButton.setAttribute('aria-checked', String(nextValue));
      switchButton.classList.toggle('is-on', nextValue);
      const status = switchButton.closest('.app-setting-row')?.querySelector('.setting-state-text');
      if (status) status.textContent = nextValue ? 'Włączony w demo' : 'Wyłączony w demo';
      showToast('Przełącznik zmieniony w podglądzie.');
    });
  }

  const auditButton = document.getElementById('audit-button');
  const scanFeedback = document.getElementById('scan-feedback');
  if (auditButton && scanFeedback) {
    auditButton.addEventListener('click', () => {
      if (auditButton.disabled) return;
      const label = auditButton.querySelector('span');
      auditButton.disabled = true;
      auditButton.classList.add('is-scanning');
      if (label) label.textContent = 'Sprawdzam…';
      scanFeedback.textContent = 'Sprawdzam dysk, pamięć, ekran, autostart i zasilanie…';
      scanFeedback.classList.add('is-visible');
      window.setTimeout(() => {
        auditButton.disabled = false;
        auditButton.classList.remove('is-scanning');
        if (label) label.textContent = 'Sprawdź ponownie';
        scanFeedback.textContent = 'Gotowe — Blessed znalazł 3 rzeczy i może zająć się nimi od razu.';
      }, 900);
    });
  }

})();
