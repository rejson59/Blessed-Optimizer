(() => {
  'use strict';

  document.documentElement.classList.add('js-ready');

  const toast = document.getElementById('site-toast');
  let toastTimer = 0;
  function showToast(message, duration = 3600) {
    if (!toast) return;
    toast.textContent = message;
    toast.classList.add('is-visible');
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => toast.classList.remove('is-visible'), duration);
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
  const mockNavGroupButtons = Array.from(document.querySelectorAll('[data-mock-nav-toggle]'));
  const mockCareBadge = document.getElementById('mock-care-badge');
  const mockStatusPill = document.getElementById('mock-status-pill');
  const mockStatusPillText = document.getElementById('mock-status-pill-text');
  const mockViews = {
    blessing: { title: 'Czas na odnowę', crumb: 'BŁOGOSŁAWIEŃSTWO', template: 'mock-template-blessing', message: 'Wybierz swój rytm. Przygotuję plan, który doda komputerowi lekkości.' },
    care: { title: 'Blessed czuwa', crumb: 'BLESSED CZUWA', template: 'mock-template-care', message: 'Znalazłem 2 rzeczy warte zajęcia się.' },
    history: { title: 'Historia', crumb: 'HISTORIA', template: 'mock-template-history', message: 'Ostatnie przeglądy są zapisywane wyłącznie na tym komputerze.' },
    gaming: { title: 'Strefa gracza', crumb: 'STREFA GRACZA', template: 'mock-template-gaming', message: 'Włącz czuwanie, aby obserwować użycie procesora i pamięci podczas gry.' },
    processes: { title: 'Procesy', crumb: 'PROCESY', template: 'mock-template-processes', message: 'Pokażę zużycie CPU i pamięci przez każdy proces — czytelnie i na żywo.' },
    devices: { title: 'Urządzenia', crumb: 'URZĄDZENIA', template: 'mock-template-devices', message: 'Przykładowy spis sprzętu obecnego według Windows. Bez odczytu kamery lub wejścia.' },
    connections: { title: 'Połączenia', crumb: 'POŁĄCZENIA', template: 'mock-template-connections', message: 'Sprawdzę stan kart sieciowych i wykonam test ping. Każdą zmianę zatwierdzasz Ty.' },
    proposals: { title: 'Propozycje', crumb: 'PROPOZYCJE', template: 'mock-template-proposals', message: 'Podpowiem, co warto sprawdzić, i zaprowadzę Cię prosto do właściwych ustawień Windows.' },
    power: { title: 'Zasilanie', crumb: 'ZASILANIE', template: 'mock-template-power', message: 'Dostrój plan zasilania. Każdą zmianę potwierdzasz Ty i zawsze możesz ją cofnąć.' },
    startup: { title: 'Autostart', crumb: 'AUTOSTART', template: 'mock-template-startup', message: 'Przejrzyj wpisy autostartu swojego konta. Każda zmiana ma zapisaną kopię do przywrócenia.' },
    cleanup: { title: 'Porządki', crumb: 'PORZĄDKI', template: 'mock-template-cleanup', message: 'Przejrzyj aplikacje swojego konta i odinstaluj te, których nie używasz. Ponowna instalacja zależy od dostępności u wydawcy lub w Microsoft Store.' },
    personalization: { title: 'Wygląd', crumb: 'WYGLĄD', template: 'mock-template-personalization', message: 'Dopasuj motyw i kolor Blessed. Ustawienia systemowe Windows otworzysz osobno.' },
    settings: { title: 'Ustawienia Blessed', crumb: 'USTAWIENIA', template: 'mock-template-settings', message: 'Wybierz swój priorytet i zdecyduj, które zadania Blessed może wykonywać automatycznie.' }
  };
  const mockPriorities = {
    gaming: { label: 'Granie i maksymalna płynność', promise: 'Pilnuję, żeby procesor i pamięć były gotowe na grę, zanim ją odpalisz.' },
    work: { label: 'Praca i skupienie', promise: 'Pilnuję, żeby nic nie zwalniało Ci pracy i żeby komputer startował szybko.' },
    battery: { label: 'Długa praca na baterii', promise: 'Pilnuję zużycia energii i podpowiadam, co niepotrzebnie zjada baterię.' },
    quiet: { label: 'Cisza i chłód', promise: 'Pilnuję obciążenia, żeby wentylatory nie miały powodu do pracy.' }
  };
  const mockBlessingGoals = {
    gaming: { label: 'Granie', intention: 'Gotowy na rundę? Przygotuję komputer do gry.', prompt: 'Dam pierwszeństwo płynności i mocy.', powerHeading: 'Daj procesorowi więcej swobody', powerCopy: 'Zwiększ limit do 100% podczas zasilania z sieci — gotowy na grę.', powerValue: '70% → 100%', boost: true },
    work: { label: 'Praca', intention: 'Czas na skupienie i lżejszy start.', prompt: 'Ułożę plan pod produktywny dzień.', powerHeading: 'Przygotuj moc do pracy', powerCopy: 'Zwiększ limit do 100% przy zasilaniu z sieci, gdy liczy się wydajność.', powerValue: '70% → 100%', boost: true },
    quiet: { label: 'Cisza', intention: 'Zadbajmy o spokojniejszy komputer.', prompt: 'Postawię na łagodniejsze, cichsze działanie.', powerHeading: 'Zachowaj spokojny profil', powerCopy: 'Zostaw limit na 70% — wybierasz ciszę i chłodniejszą pracę.', powerValue: '70% → 70%', boost: false },
    battery: { label: 'Bateria', intention: 'Dziś liczy się dłuższy dzień.', prompt: 'Ułożę plan z myślą o energii na później.', powerHeading: 'Oszczędzaj energię', powerCopy: 'Zostaw limit na 70%, by zachować spokojniejszy pobór energii.', powerValue: '70% → 70%', boost: false }
  };
  let mockWatchTimer = 0;
  let mockBlessingTimer = 0;
  let mockRenewalTimer = 0;
  let mockBlessingGoal = 'gaming';
  let mockBlessingScanned = false;
  let mockRenewalSelection = new Set(['cleanup', 'power']);
  let mockRenewalCompletedItems = [];

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

  function mockSetNavGroup(groupName, expanded) {
    mockNavGroupButtons.forEach((button) => {
      const selected = button.dataset.mockNavToggle === groupName;
      const open = selected && expanded;
      button.setAttribute('aria-expanded', String(open));
      button.classList.toggle('is-open', open);
      const panel = document.getElementById(button.getAttribute('aria-controls'));
      if (panel) panel.hidden = !open;
      const chevron = button.querySelector('.mock-nav-chevron');
      if (chevron) chevron.textContent = open ? '⌄' : '›';
    });
  }

  function setMockView(viewName) {
    const view = mockViews[viewName];
    const template = view && document.getElementById(view.template);
    const selectedButton = mockNavButtons.find((button) => button.dataset.mockView === viewName);
    if (!mockPage || !view || !template || !selectedButton) return;
    const navItems = selectedButton.closest('[data-mock-nav-items]');
    if (navItems) mockSetNavGroup(navItems.dataset.mockNavItems, true);
    window.clearInterval(mockWatchTimer);
    mockWatchTimer = 0;
    window.clearTimeout(mockBlessingTimer);
    window.clearTimeout(mockRenewalTimer);
    mockBlessingTimer = 0;
    mockRenewalTimer = 0;
    if (mockWindow) mockWindow.classList.remove('is-blessing', 'is-renewing');
    if (mockStatusPill) mockStatusPill.classList.remove('is-scanning');
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
      if (selected) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
    if (viewName === 'care') mockRefreshCareState();
    if (viewName === 'processes') {
      mockRefreshProcessControls();
      mockFilterProcessRows('');
    }
    if (viewName === 'blessing') mockRestoreBlessingView();
  }

  function mockRenewalCountLabel(count) {
    if (count === 1) return '1 krok';
    if (count >= 2 && count <= 4) return `${count} kroki`;
    return `${count} kroków`;
  }

  function mockRefreshRenewalPlan() {
    if (!mockPage) return;
    const allItems = Array.from(mockPage.querySelectorAll('[data-renewal-item]'));
    allItems.forEach((input) => {
      const card = input.closest('[data-renewal-card]');
      const visible = Boolean(card && !card.hidden);
      const selected = visible && mockRenewalSelection.has(input.dataset.renewalItem);
      input.checked = selected;
      if (card) card.classList.toggle('is-selected', selected);
    });
    const items = allItems.filter((input) => {
      const card = input.closest('[data-renewal-card]');
      return Boolean(card && !card.hidden);
    });
    const count = items.filter((input) => mockRenewalSelection.has(input.dataset.renewalItem)).length;
    const countLabel = mockPage.querySelector('#mock-renewal-count');
    const applyButton = mockPage.querySelector('#mock-renewal-apply');
    if (countLabel) countLabel.textContent = count ? mockRenewalCountLabel(count) : 'Wybierz kroki';
    if (applyButton) {
      applyButton.disabled = count === 0;
      const label = applyButton.querySelector('[data-renewal-apply-label]');
      if (label) label.textContent = count ? `Odnowa jednym kliknięciem · ${mockRenewalCountLabel(count)}` : 'Wybierz krok odnowy';
    }
  }

  function mockBringIntoView(element) {
    if (!element?.scrollIntoView) return;
    const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    window.requestAnimationFrame(() => element.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'start' }));
  }

  function mockSetBlessingGoal(goalName, applyGoalDefaults = true) {
    const goal = mockBlessingGoals[goalName];
    if (!goal || !mockPage) return;
    mockBlessingGoal = goalName;
    mockPage.querySelectorAll('[data-blessing-goal]').forEach((button) => {
      const selected = button.dataset.blessingGoal === goalName;
      button.classList.toggle('is-selected', selected);
      button.setAttribute('aria-pressed', String(selected));
    });
    const setText = (selector, text) => {
      const element = mockPage.querySelector(selector);
      if (element) element.textContent = text;
    };
    setText('#mock-blessing-intention', goal.intention);
    setText('#mock-blessing-prompt', goal.prompt);
    setText('#mock-renewal-goal-name', goal.label);
    setText('#mock-sidebar-profile', `${goal.label} · gotowy`);
    setText('#mock-renewal-power-heading', goal.powerHeading);
    setText('#mock-renewal-power-copy', goal.powerCopy);
    setText('#mock-renewal-power-value', goal.powerValue);
    setText('#mock-renewal-power-metric', goal.boost ? '100%' : '70%');
    setText('#mock-renewal-power-metric-caption', goal.boost ? 'możliwy limit CPU przy zasilaniu' : 'obecny limit dla wybranego celu');
    const powerInput = mockPage.querySelector('[data-renewal-item="power"]');
    const powerCard = mockPage.querySelector('[data-renewal-card="power"]');
    if (powerCard) powerCard.hidden = !goal.boost;
    if (!goal.boost) mockRenewalSelection.delete('power');
    if (applyGoalDefaults && powerInput) {
      if (goal.boost) mockRenewalSelection.add('power');
      else mockRenewalSelection.delete('power');
      mockRenewalCompletedItems = [];
      const results = mockPage.querySelector('#mock-first-blessing-results');
      const complete = mockPage.querySelector('#mock-renewal-complete');
      if (mockBlessingScanned && results) results.hidden = false;
      if (complete) complete.hidden = true;
    }
    mockRefreshRenewalPlan();
  }

  function mockRestoreBlessingView() {
    mockSetBlessingGoal(mockBlessingGoal, false);
    const results = mockPage.querySelector('#mock-first-blessing-results');
    const complete = mockPage.querySelector('#mock-renewal-complete');
    const runButton = mockPage.querySelector('#mock-first-blessing-run');
    const status = mockPage.querySelector('#mock-first-blessing-status');
    if (results) results.hidden = !mockBlessingScanned || mockRenewalCompletedItems.length > 0;
    if (complete) complete.hidden = mockRenewalCompletedItems.length === 0;
    if (runButton) runButton.querySelector('#mock-first-blessing-label').textContent = mockBlessingScanned ? 'Odśwież przegląd' : 'Rozpocznij błogosławieństwo';
    if (status) status.textContent = mockBlessingScanned ? 'Plan odnowy gotowy · dane przykładowe' : 'Podgląd interaktywny · przykładowe odczyty';
    if (mockStatusPillText) mockStatusPillText.textContent = mockRenewalCompletedItems.length ? 'Odnowa zakończona' : mockBlessingScanned ? 'Plan odnowy gotowy' : 'Gotowy na odnowę';
    if (mockBlessingScanned && mockRenewalCompletedItems.length) mockShowRenewalComplete(mockRenewalCompletedItems);
    mockRefreshRenewalPlan();
  }

  function mockStartBlessingScan() {
    const runButton = mockPage?.querySelector('#mock-first-blessing-run');
    const progress = mockPage?.querySelector('#mock-blessing-progress');
    const results = mockPage?.querySelector('#mock-first-blessing-results');
    const status = mockPage?.querySelector('#mock-first-blessing-status');
    if (!runButton || runButton.disabled || !progress || !results) return;
    const label = runButton.querySelector('#mock-first-blessing-label');
    const steps = Array.from(progress.querySelectorAll('[data-blessing-step]'));
    steps.forEach((step) => step.classList.remove('is-active', 'is-done'));
    mockBlessingScanned = false;
    mockRenewalCompletedItems = [];
    runButton.disabled = true;
    if (label) label.textContent = 'Blessed już sprawdza…';
    progress.hidden = false;
    results.hidden = true;
    const complete = mockPage.querySelector('#mock-renewal-complete');
    if (complete) complete.hidden = true;
    if (status) status.textContent = 'Oglądam możliwości Twojego komputera…';
    if (mockWindow) mockWindow.classList.add('is-blessing');
    if (mockStatusPill) mockStatusPill.classList.add('is-scanning');
    if (mockStatusPillText) mockStatusPillText.textContent = 'Blessed szykuje odnowę';
    if (mockGuideMessage) mockGuideMessage.textContent = 'Lecę znaleźć, gdzie możemy dodać Twojemu komputerowi lekkości.';
    mockPage.querySelectorAll('[data-blessing-goal]').forEach((button) => { button.disabled = true; });
    let index = 0;
    const advance = () => {
      const previous = steps[index - 1];
      if (previous) previous.classList.replace('is-active', 'is-done');
      if (index >= steps.length) {
        mockBlessingTimer = 0;
        mockBlessingScanned = true;
        progress.hidden = true;
        results.hidden = false;
        mockBringIntoView(results);
        runButton.disabled = false;
        if (label) label.textContent = 'Odśwież przegląd';
        if (status) status.textContent = 'Mam gotowy plan odnowy · przykładowe odczyty';
        if (mockWindow) mockWindow.classList.remove('is-blessing');
        if (mockStatusPill) mockStatusPill.classList.remove('is-scanning');
        if (mockStatusPillText) mockStatusPillText.textContent = 'Plan odnowy gotowy';
        if (mockGuideMessage) mockGuideMessage.textContent = 'Mam dla Ciebie plan. Zobaczmy, które kroki pasują do Twojego dnia.';
        mockPage.querySelectorAll('[data-blessing-goal]').forEach((button) => { button.disabled = false; });
        mockRefreshRenewalPlan();
        showToast('Plan odnowy jest gotowy. Wybierz kroki, które chcesz wypróbować.');
        return;
      }
      steps[index].classList.add('is-active');
      index += 1;
      mockBlessingTimer = window.setTimeout(advance, 430);
    };
    advance();
  }

  function mockShowRenewalComplete(items) {
    if (!mockPage) return;
    const hasCleanup = items.includes('cleanup');
    const hasPower = items.includes('power');
    const freed = mockPage.querySelector('#mock-renewal-freed');
    const power = mockPage.querySelector('#mock-renewal-power-result');
    const copy = mockPage.querySelector('#mock-renewal-complete-copy');
    const cleanupLine = mockPage.querySelector('#mock-renewal-done-cleanup');
    const powerLine = mockPage.querySelector('#mock-renewal-done-power');
    if (freed) freed.textContent = hasCleanup ? '2,4 GB' : '—';
    if (power) power.textContent = hasPower ? '100%' : 'Bez zmian';
    if (cleanupLine) cleanupLine.hidden = !hasCleanup;
    if (powerLine) {
      powerLine.hidden = !hasPower;
      powerLine.textContent = `✓ Przygotowano profil energii dla ${mockBlessingGoals[mockBlessingGoal].label}`;
    }
    if (copy) {
      const outcomes = [];
      if (hasCleanup) outcomes.push('odzyskaliśmy 2,4 GB miejsca');
      if (hasPower) outcomes.push(`dostroiliśmy profil pod ${mockBlessingGoals[mockBlessingGoal].label.toLowerCase()}`);
      copy.textContent = outcomes.length ? `Pięknie! ${outcomes.join(' i ')}. Twój komputer jest gotowy na więcej.` : 'Gotowe. Twój komputer zachował obecne ustawienia.';
    }
  }

  function mockRunRenewal() {
    const selected = Array.from(mockRenewalSelection);
    const results = mockPage?.querySelector('#mock-first-blessing-results');
    const progress = mockPage?.querySelector('#mock-renewal-progress');
    const complete = mockPage?.querySelector('#mock-renewal-complete');
    if (!selected.length || !results || !progress || !complete) return;
    mockRenewalCompletedItems = [];
    results.hidden = true;
    complete.hidden = true;
    progress.hidden = false;
    mockBringIntoView(progress);
    const steps = Array.from(progress.querySelectorAll('[data-renewal-step]'));
    steps.forEach((step) => {
      step.hidden = !selected.includes(step.dataset.renewalStep);
      step.classList.remove('is-active', 'is-done');
    });
    if (mockWindow) mockWindow.classList.add('is-renewing');
    if (mockStatusPill) mockStatusPill.classList.add('is-scanning');
    if (mockStatusPillText) mockStatusPillText.textContent = 'Blessed odnawia komputer';
    if (mockGuideMessage) mockGuideMessage.textContent = 'Już działam. Za chwilę pokażę Ci świeży efekt.';
    const activeSteps = steps.filter((step) => selected.includes(step.dataset.renewalStep));
    let index = 0;
    const advance = () => {
      const previous = activeSteps[index - 1];
      if (previous) previous.classList.replace('is-active', 'is-done');
      if (index >= activeSteps.length) {
        mockRenewalTimer = 0;
        mockRenewalCompletedItems = selected;
        progress.hidden = true;
        complete.hidden = false;
        mockBringIntoView(complete);
        mockShowRenewalComplete(selected);
        if (mockWindow) mockWindow.classList.remove('is-renewing');
        if (mockStatusPill) mockStatusPill.classList.remove('is-scanning');
        if (mockStatusPillText) mockStatusPillText.textContent = 'Odnowa zakończona';
        if (mockGuideMessage) mockGuideMessage.textContent = 'Pięknie! Twój komputer odzyskał trochę oddechu.';
        showToast('Odnowa zakończona — taki efekt zobaczysz w demonstracji.');
        return;
      }
      activeSteps[index].classList.add('is-active');
      index += 1;
      mockRenewalTimer = window.setTimeout(advance, 760);
    };
    advance();
  }

  function mockReturnToRenewalPlan() {
    mockRenewalCompletedItems = [];
    const results = mockPage?.querySelector('#mock-first-blessing-results');
    const complete = mockPage?.querySelector('#mock-renewal-complete');
    if (results) results.hidden = false;
    if (complete) complete.hidden = true;
    if (mockStatusPillText) mockStatusPillText.textContent = 'Plan odnowy gotowy';
    if (mockGuideMessage) mockGuideMessage.textContent = 'Możesz zmienić swój plan albo wybrać kolejną rzecz do odnowienia.';
    mockRefreshRenewalPlan();
  }

  mockNavGroupButtons.forEach((button) => {
    button.addEventListener('click', () => {
      const open = button.getAttribute('aria-expanded') === 'true';
      mockSetNavGroup(button.dataset.mockNavToggle, !open);
    });
    button.addEventListener('keydown', (event) => {
      if (event.key === 'ArrowLeft' && button.getAttribute('aria-expanded') === 'true') {
        event.preventDefault();
        mockSetNavGroup(button.dataset.mockNavToggle, false);
      } else if (event.key === 'ArrowRight' && button.getAttribute('aria-expanded') !== 'true') {
        event.preventDefault();
        mockSetNavGroup(button.dataset.mockNavToggle, true);
      } else if (event.key === 'ArrowDown') {
        event.preventDefault();
        mockSetNavGroup(button.dataset.mockNavToggle, true);
        window.requestAnimationFrame(() => document.getElementById(button.getAttribute('aria-controls'))?.querySelector('[data-mock-view]')?.focus());
      }
    });
  });

  mockNavButtons.forEach((button) => {
    button.addEventListener('click', () => setMockView(button.dataset.mockView));
    button.addEventListener('keydown', (event) => {
      if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const visibleButtons = mockNavButtons.filter((navButton) => !navButton.closest('[data-mock-nav-items]')?.hidden);
      const index = visibleButtons.indexOf(button);
      const next = event.key === 'Home' ? 0
        : event.key === 'End' ? visibleButtons.length - 1
          : (index + (['ArrowRight', 'ArrowDown'].includes(event.key) ? 1 : -1) + visibleButtons.length) % visibleButtons.length;
      visibleButtons[next].focus();
      setMockView(visibleButtons[next].dataset.mockView);
    });
  });
  setMockView('blessing');

  function mockRunScan() {
    const firstBlessingButton = mockPage ? mockPage.querySelector('#mock-first-blessing-run') : null;
    if (firstBlessingButton) {
      firstBlessingButton.click();
      return;
    }
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
    const selectable = boxes.filter((box) => !box.disabled).length;
    const protectedCount = boxes.length - selectable;
    const checked = boxes.filter((box) => box.checked && !box.disabled).length;
    counter.textContent = `${boxes.length} przykładowych pakietów · ${selectable} do wyboru · ${protectedCount} chroniony · ${checked} zaznaczonych`;
  }

  function mockSetAllAppx(checked) {
    mockAppxBoxes().forEach((box) => {
      if (!box.disabled && !box.closest('.mock-appx').classList.contains('is-removed')) box.checked = checked;
    });
    mockRefreshAppxCounter();
  }

  function mockShowUninstallConfirm() {
    const confirm = document.getElementById('mock-cleanup-confirm');
    const text = document.getElementById('mock-cleanup-confirm-text');
    if (!confirm) return;
    const boxes = mockAppxBoxes().filter((box) => box.checked && !box.disabled && !box.closest('.mock-appx').classList.contains('is-removed'));
    if (boxes.length === 0) {
      showToast('Zaznacz najpierw aplikacje, które chcesz odinstalować.');
      return;
    }
    if (text) {
      const names = boxes.slice(0, 3).map((box) => box.closest('.mock-appx').querySelector('strong')?.textContent || '');
      const suffix = boxes.length > 3 ? ` i jeszcze ${boxes.length - 3}` : '';
      text.textContent = `Odinstalować ${boxes.length} aplikacji bieżącego konta? ${names.join(', ')}${suffix}. Prototyp niczego nie usuwa; ponowna instalacja zależy od dostępności u wydawcy lub w Microsoft Store.`;
    }
    confirm.hidden = false;
  }

  function mockShowProcessConfirm(forceTerminate) {
    const confirm = document.getElementById('mock-process-confirm');
    const text = document.getElementById('mock-process-confirm-text');
    const run = document.getElementById('mock-process-confirm-run');
    const selectedCount = Array.from(mockPage.querySelectorAll('[data-mock-process-row]')).filter((row) => {
      const checkbox = row.querySelector('[data-mock-process-select]');
      return checkbox && checkbox.checked && !checkbox.disabled && !row.hidden;
    }).length;
    if (selectedCount === 0) {
      showToast('Zaznacz aplikację z widocznym oknem. Procesy oznaczone jako ważne i składniki Windows są wyłączone z tej akcji.');
      return;
    }
    if (text) text.textContent = forceTerminate
      ? `Wymusić zakończenie ${selectedCount} zaznaczonych aplikacji? Niezapisane zmiany mogą zostać utracone.`
      : `Poprosić ${selectedCount} zaznaczonych aplikacji o zwykłe zamknięcie? Najpierw zapisz swoją pracę.`;
    if (run) {
      run.dataset.forceTerminate = String(forceTerminate);
      run.textContent = forceTerminate ? 'Tak, wymuś zakończenie' : 'Tak, poproś o zamknięcie';
    }
    if (confirm) confirm.hidden = false;
  }

  function mockRunProcessClose(forceTerminate) {
    const confirm = document.getElementById('mock-process-confirm');
    if (confirm) confirm.hidden = true;
    const rows = Array.from(mockPage.querySelectorAll('[data-mock-process-row]'));
    const selected = rows.filter((row) => {
      const checkbox = row.querySelector('[data-mock-process-select]');
      return checkbox && checkbox.checked && !checkbox.disabled && !row.hidden;
    });
    if (selected.length === 0) {
      showToast('Zaznacz aplikację z widocznym oknem. Procesy oznaczone jako ważne i składniki Windows są wyłączone z tej akcji.');
      return;
    }
    selected.forEach((row) => {
      row.classList.add('is-closed');
      const select = row.querySelector('[data-mock-process-select]');
      const keep = row.querySelector('[data-mock-process-important]');
      if (select) { select.checked = false; select.disabled = true; }
      if (keep) keep.disabled = true;
    });
    mockRefreshProcessControls();
    const result = document.getElementById('mock-process-result');
    if (result) result.textContent = `Podgląd: wybrano ${selected.length} ${selected.length === 1 ? 'aplikację' : 'aplikacje'} do ${forceTerminate ? 'wymuszonego zakończenia' : 'zwykłego zamknięcia'}.`;
    showToast(forceTerminate
      ? 'Makieta: w aplikacji wymuszenie wymaga osobnego potwierdzenia i może utracić niezapisane zmiany.'
      : 'Makieta: w aplikacji Blessed najpierw prosi aplikację o zapisanie pracy i zwykłe zamknięcie.', 6500);
  }

  function mockRefreshProcessControls() {
    if (!mockPage) return;
    const rows = Array.from(mockPage.querySelectorAll('[data-mock-process-row]'));
    const candidates = rows.filter((row) => {
      const select = row.querySelector('[data-mock-process-select]');
      return select && !select.disabled && !row.hidden;
    });
    const selectedCount = candidates.filter((row) => row.querySelector('[data-mock-process-select]').checked).length;
    const count = document.getElementById('mock-process-selection-count');
    const status = document.getElementById('mock-process-status');
    const close = document.getElementById('mock-close-selected');
    const force = document.getElementById('mock-force-close-selected');
    if (count) count.textContent = `${selectedCount} zaznaczone`;
    if (status) status.textContent = `${rows.filter((row) => !row.classList.contains('is-closed')).length} przykładowych pozycji · dane demonstracyjne`;
    if (close) close.disabled = selectedCount === 0;
    if (force) force.disabled = selectedCount === 0;
  }

  function mockFilterProcessRows(query) {
    if (!mockPage) return;
    const normalized = query.trim().toLocaleLowerCase();
    mockPage.querySelectorAll('[data-mock-process-row]').forEach((row) => {
      row.hidden = normalized.length > 0 && !row.textContent.toLocaleLowerCase().includes(normalized);
    });
    mockRefreshProcessControls();
  }

  function mockRunUninstall() {
    const confirm = document.getElementById('mock-cleanup-confirm');
    const summary = document.getElementById('mock-cleanup-summary');
    if (confirm) confirm.hidden = true;
    const boxes = mockAppxBoxes().filter((box) => box.checked && !box.disabled && !box.closest('.mock-appx').classList.contains('is-removed'));
    boxes.forEach((box) => {
      const row = box.closest('.mock-appx');
      if (row) row.classList.add('is-removed');
      box.checked = false;
      box.disabled = true;
    });
    mockRefreshAppxCounter();
    if (summary) {
      summary.hidden = false;
      summary.textContent = `Symulacja zakończona: ${boxes.length} przykładowych pozycji oznaczono jako usunięte. Niczego nie odinstalowano z Twojego komputera.`;
    }
    showToast('Prototyp: to tylko przykładowe pozycje. Aplikacja Windows wykonałaby rzeczywiste działanie dopiero po Twoim potwierdzeniu.');
  }

  function mockHandleClick(event) {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;

    const helpButton = target.closest('[data-mock-help]');
    if (helpButton) {
      showToast(helpButton.dataset.mockHelp, 6500);
      return;
    }

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

    const blessingGoal = target.closest('[data-blessing-goal]');
    if (blessingGoal) {
      mockSetBlessingGoal(blessingGoal.dataset.blessingGoal);
      return;
    }

    if (target.closest('#mock-first-blessing-run')) {
      mockStartBlessingScan();
      return;
    }
    if (target.closest('#mock-renewal-apply')) {
      mockRunRenewal();
      return;
    }
    if (target.closest('#mock-renewal-again')) {
      mockReturnToRenewalPlan();
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

    if (target.closest('#mock-close-selected')) {
      mockShowProcessConfirm(false);
      return;
    }
    if (target.closest('#mock-force-close-selected')) {
      mockShowProcessConfirm(true);
      return;
    }
    if (target.closest('#mock-process-confirm-run')) {
      const runButton = target.closest('#mock-process-confirm-run');
      mockRunProcessClose(runButton.dataset.forceTerminate === 'true');
      return;
    }
    if (target.closest('#mock-process-confirm-cancel')) {
      const confirm = document.getElementById('mock-process-confirm');
      if (confirm) confirm.hidden = true;
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
    mockPage.addEventListener('input', (event) => {
      if (!(event.target instanceof HTMLInputElement)) return;
      if (event.target.id === 'mock-process-search') mockFilterProcessRows(event.target.value);
      if (event.target.id === 'mock-cleanup-search') {
        const query = event.target.value.trim().toLocaleLowerCase();
        mockPage.querySelectorAll('.mock-appx').forEach((row) => {
          row.hidden = query.length > 0 && !row.textContent.toLocaleLowerCase().includes(query);
        });
      }
    });
    mockPage.addEventListener('change', (event) => {
      if (!(event.target instanceof HTMLInputElement)) return;
      if (event.target.hasAttribute('data-mock-appx')) mockRefreshAppxCounter();
      if (event.target.hasAttribute('data-mock-process-important')) {
        const row = event.target.closest('[data-mock-process-row]');
        const select = row?.querySelector('[data-mock-process-select]');
        const role = row?.querySelector('[data-mock-process-role]');
        if (row && select) {
          if (event.target.checked) select.checked = false;
          select.disabled = event.target.checked || row.dataset.processProtected === 'true' || row.classList.contains('is-closed');
          row.classList.toggle('is-important', event.target.checked);
        }
        if (role && row) role.textContent = event.target.checked ? 'Ważny dla Ciebie' : row.dataset.processRole;
        mockRefreshProcessControls();
      }
      if (event.target.hasAttribute('data-mock-process-select')) mockRefreshProcessControls();
      if (event.target.hasAttribute('data-renewal-item')) {
        const item = event.target.dataset.renewalItem;
        if (event.target.checked) mockRenewalSelection.add(item);
        else mockRenewalSelection.delete(item);
        mockRenewalCompletedItems = [];
        mockRefreshRenewalPlan();
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
      if (label) label.textContent = 'Blessed sprawdza…';
      scanFeedback.textContent = 'Blessed układa plan, który doda komputerowi lekkości…';
      scanFeedback.classList.add('is-visible');
      window.setTimeout(() => {
        auditButton.disabled = false;
        auditButton.classList.remove('is-scanning');
        if (label) label.textContent = 'Odśwież podgląd';
        scanFeedback.textContent = 'Gotowe — Blessed znalazł kilka dobrych kroków do Twojej decyzji.';
      }, 900);
    });
  }

})();
