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
        downloadNote.textContent = 'Windows 10/11 x64 · samodzielny plik .exe · bezpośrednie pobranie najnowszego wydania.';
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

  // Keep the operating-system pointer visible; Blessed's wings float beside it.
  const cursorWings = document.getElementById('cursor-wings');
  const finePointer = window.matchMedia?.('(hover: hover) and (pointer: fine)');
  const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  if (cursorWings && finePointer?.matches && !reducedMotion) {
    let targetX = 0;
    let targetY = 0;
    let currentX = 0;
    let currentY = 0;
    let hasPointerPosition = false;
    let cursorFrame = 0;
    let blessingTimer = 0;

    function animateCursorWings() {
      currentX += (targetX - currentX) * 0.32;
      currentY += (targetY - currentY) * 0.32;
      // Keep the wing pair centred on the pointer body, leaving the arrow tip clear.
      cursorWings.style.transform = `translate3d(${currentX}px, ${currentY}px, 0)`;
      if (Math.abs(targetX - currentX) > 0.15 || Math.abs(targetY - currentY) > 0.15) {
        cursorFrame = window.requestAnimationFrame(animateCursorWings);
      } else {
        cursorFrame = 0;
      }
    }

    document.addEventListener('pointermove', (event) => {
      if (event.pointerType && event.pointerType !== 'mouse') return;
      targetX = event.clientX;
      targetY = event.clientY;
      if (!hasPointerPosition) {
        currentX = targetX;
        currentY = targetY;
        hasPointerPosition = true;
      }
      cursorWings.classList.add('is-visible');
      const target = event.target instanceof Element ? event.target : null;
      const isInteractive = Boolean(target?.closest('a,button,[role="button"],[role="switch"],summary,input,select,textarea'));
      cursorWings.classList.toggle('is-hovering', isInteractive);
      if (!cursorFrame) cursorFrame = window.requestAnimationFrame(animateCursorWings);
    });

    document.addEventListener('pointerdown', (event) => {
      if (event.pointerType && event.pointerType !== 'mouse') return;
      cursorWings.classList.add('is-pressing');
      cursorWings.classList.remove('is-blessing');
      window.requestAnimationFrame(() => cursorWings.classList.add('is-blessing'));
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

  // Clickable Windows app-layout preview inside the Simulator tab.
  const programWindow = document.querySelector('.program-window');
  const programViewContent = document.getElementById('program-view-content');
  const programViewTitle = document.getElementById('program-view-title');
  const programCrumbTitle = document.getElementById('program-crumb-title');
  const programBlessedMessage = document.getElementById('program-blessed-message');
  const programNavButtons = Array.from(document.querySelectorAll('[data-program-view]'));
  const programViews = {
    gaming: { title: 'Strefa gracza', crumb: 'STREFA GRACZA', template: 'program-template-gaming', message: 'Polepszę działanie Twojego komputera — krok po kroku i tylko za Twoją zgodą. Zaczniemy od tego, co ma znaczenie podczas gry.' },
    connections: { title: 'Połączenia', crumb: 'POŁĄCZENIA', template: 'program-template-connections', message: 'Sprawdzę, co jest nie tak z połączeniem. Jeśli znajdziemy bezpieczną poprawkę, naprawię to z Tobą — po Twojej zgodzie.' },
    proposals: { title: 'Propozycje', crumb: 'PROPOZYCJE', template: 'program-template-proposals', message: 'Znalazłem kilka pomysłów na ulepszenia. Wybierz jeden, a opowiem Ci prostym językiem, co może dać i jak wrócić.' },
    personalization: { title: 'Personalizacja Windows', crumb: 'PERSONALIZACJA', template: 'program-template-personalization', message: 'Chcesz zmienić klimat? Pokażę Ci podgląd Windowsa i wyglądu programu, zanim cokolwiek zatwierdzisz.' },
    processes: { title: 'Procesy', crumb: 'PROCESY', template: 'program-template-processes', message: 'Pokażę lokalne zużycie procesora i pamięci. Aplikacja nie zamyka procesów ani nie zmienia ich priorytetów.' },
    startup: { title: 'Autostart', crumb: 'AUTOSTART', template: 'program-template-startup', message: 'Przejrzysz wpisy autostartu bieżącego konta. Przed zmianą program zapisuje kopię i pozwala ją przywrócić.' },
    power: { title: 'Zasilanie', crumb: 'ZASILANIE', template: 'program-template-power', message: 'Odczytasz ustawienia aktywnego planu. Każda zmiana wymaga osobnego potwierdzenia i może zostać cofnięta.' }
  };

  function setProgramView(viewName) {
    const view = programViews[viewName];
    const template = view && document.getElementById(view.template);
    const selectedButton = programNavButtons.find((button) => button.dataset.programView === viewName);
    if (!programViewContent || !view || !template || !selectedButton) return;
    programViewContent.classList.remove('view-switch');
    programViewContent.replaceChildren(template.content.cloneNode(true));
    window.requestAnimationFrame(() => programViewContent.classList.add('view-switch'));
    programViewContent.setAttribute('aria-labelledby', selectedButton.id);
    if (programViewTitle) programViewTitle.textContent = view.title;
    if (programCrumbTitle) programCrumbTitle.textContent = view.crumb;
    if (programBlessedMessage) programBlessedMessage.textContent = view.message;
    programNavButtons.forEach((button) => {
      const selected = button === selectedButton;
      button.classList.toggle('is-active', selected);
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
      if (selected) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
  }

  programNavButtons.forEach((button) => {
    button.addEventListener('click', () => setProgramView(button.dataset.programView));
    button.addEventListener('keydown', (event) => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = programNavButtons.indexOf(button);
      const next = event.key === 'Home' ? 0
        : event.key === 'End' ? programNavButtons.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : -1) + programNavButtons.length) % programNavButtons.length;
      programNavButtons[next].focus();
      const category = programNavButtons[next].dataset.programView;
      setProgramView(category);
      selectScenario(category);
    });
  });
  setProgramView('gaming');

  if (programWindow) {
    programWindow.addEventListener('click', (event) => {
      const target = event.target instanceof Element ? event.target : null;
      if (!target) return;
      const simulatorLink = target.closest('[data-preview-to-sim]');
      if (simulatorLink) {
        document.getElementById('symulator')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        return;
      }

      const connectionButton = target.closest('[data-connection-type]');
      if (connectionButton) {
        programWindow.querySelectorAll('[data-connection-type]').forEach((button) => {
          const selected = button === connectionButton;
          button.classList.toggle('is-selected', selected);
          button.setAttribute('aria-pressed', String(selected));
        });
        const name = document.getElementById('connection-name');
        const description = document.getElementById('connection-description');
        const state = document.getElementById('connection-state');
        if (name) name.textContent = connectionButton.dataset.connectionName;
        if (description) description.textContent = connectionButton.dataset.connectionDescription;
        if (state) state.textContent = connectionButton.dataset.connectionState;
        return;
      }

      const auditDemoButton = target.closest('[data-program-audit]');
      if (auditDemoButton) {
        if (auditDemoButton.disabled) return;
        const feedback = document.getElementById('program-audit-feedback');
        const progressMessage = auditDemoButton.dataset.progressMessage || 'Przeglądam przykładowe karty interfejsu…';
        const successMessage = auditDemoButton.dataset.successMessage || 'Gotowe — przykładowy raport. Nie odczytaliśmy żadnych danych z Windowsa.';
        const finishLabel = auditDemoButton.dataset.finishLabel || 'Uruchom ponownie';
        auditDemoButton.disabled = true;
        auditDemoButton.setAttribute('aria-busy', 'true');
        auditDemoButton.textContent = 'Przygotowuję pokaz…';
        if (feedback) {
          feedback.classList.remove('is-complete');
          feedback.textContent = progressMessage;
        }
        window.setTimeout(() => {
          auditDemoButton.disabled = false;
          auditDemoButton.removeAttribute('aria-busy');
          auditDemoButton.textContent = finishLabel;
          if (feedback) {
            feedback.textContent = successMessage;
            feedback.classList.add('is-complete');
          }
        }, 850);
        return;
      }

      const previewSwitch = target.closest('.program-switch');
      if (previewSwitch) {
        const enabled = previewSwitch.getAttribute('aria-checked') !== 'true';
        previewSwitch.setAttribute('aria-checked', String(enabled));
        previewSwitch.classList.toggle('is-on', enabled);
        const state = previewSwitch.closest('.program-setting-row')?.querySelector('.program-setting-state');
        if (state) state.textContent = enabled ? 'Włączone w makiecie' : 'Wyłączone w makiecie';
        if (previewSwitch.dataset.programSwitch === 'standby') {
          const activity = document.getElementById('standby-activity-card');
          const activityMessage = document.getElementById('standby-activity-message');
          const activityState = document.getElementById('standby-activity-state');
          if (activity) activity.dataset.state = enabled ? 'on' : 'off';
          if (activityMessage) activityMessage.textContent = enabled
            ? 'Sprawdzam przykładowe wskaźniki gry…'
            : 'Czuwanie wyłączone w makiecie; nic nie jest monitorowane.';
          if (activityState) activityState.textContent = enabled ? 'DEMO AKTYWNE' : 'OCZEKUJE';
          if (enabled) {
            window.setTimeout(() => {
              if (activity?.isConnected && previewSwitch.getAttribute('aria-checked') === 'true' && activityMessage) {
                activityMessage.textContent = 'Przykład: aplikacja w tle do sprawdzenia. Niczego nie zamykam — najpierw pytam Ciebie.';
              }
            }, 1100);
          }
          showToast('To tylko pokaz czuwania — komputer i procesy pozostały nietknięte.');
        } else {
          showToast('Zmieniono tylko makietę — Windows pozostał bez zmian.');
        }
        return;
      }

      const themeTarget = target.closest('[data-theme-target]');
      if (themeTarget) {
        const preview = document.getElementById('program-theme-preview');
        const isWindows = themeTarget.dataset.themeTarget === 'windows';
        programWindow.querySelectorAll('[data-theme-target]').forEach((button) => {
          const selected = button === themeTarget;
          button.classList.toggle('is-selected', selected);
          button.setAttribute('aria-pressed', String(selected));
        });
        if (preview) preview.dataset.target = themeTarget.dataset.themeTarget;
        const label = document.getElementById('program-theme-target-label');
        const description = document.getElementById('theme-target-description');
        if (label) label.textContent = isWindows ? 'Motyw systemu Windows' : 'Wygląd Blessed Optimizer';
        if (description) description.textContent = isWindows
          ? 'Zmiany systemowe wymagałyby osobnego potwierdzenia. Teraz oglądasz wyłącznie makietę.'
          : 'Ten styl dotyczyłby samego interfejsu Blessed — podgląd zmienia się od razu.';
        return;
      }

      const themeMode = target.closest('[data-theme-mode]');
      if (themeMode) {
        const preview = document.getElementById('program-theme-preview');
        programWindow.querySelectorAll('[data-theme-mode]').forEach((button) => {
          const selected = button === themeMode;
          button.classList.toggle('is-selected', selected);
          button.setAttribute('aria-pressed', String(selected));
        });
        if (preview) preview.dataset.theme = themeMode.dataset.themeMode;
        return;
      }

      const accent = target.closest('[data-accent-choice]');
      if (accent) {
        const preview = document.getElementById('program-theme-preview');
        programWindow.querySelectorAll('[data-accent-choice]').forEach((button) => {
          const selected = button === accent;
          button.classList.toggle('is-selected', selected);
          button.setAttribute('aria-pressed', String(selected));
        });
        if (preview) preview.dataset.accent = accent.dataset.accentChoice;
      }
    });
  }

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
  setAppView('overview');

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
      showToast('Przełączono wyłącznie element makiety — Windows nie został zmieniony.');
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
      if (label) label.textContent = 'Już zaglądam…';
      scanFeedback.textContent = 'Przeglądam przykładowe dane interfejsu…';
      scanFeedback.classList.add('is-visible');
      window.setTimeout(() => {
        auditButton.disabled = false;
        auditButton.classList.remove('is-scanning');
        if (label) label.textContent = 'Zajrzyj ponownie';
        scanFeedback.textContent = 'Gotowe — oto przykładowe wskazówki. Do komputera nie zaglądaliśmy.';
      }, 900);
    });
  }

  // These are made-up demo values, never local-device measurements.
  const scenarios = {
    gaming: {
      title: 'Strefa gracza',
      metrics: [
        { title: 'Aplikacje w tle', before: '8 pozycji', after: '5 · przykład', foot: 'Makieta nie odczytuje ani nie zamyka procesów. Działanie docelowe wymagałoby Twojej zgody.', progress: 58 },
        { title: 'FPS / ping', before: 'Nie mierzono', after: 'Wymaga testu', foot: 'Nie obiecujemy wzrostu; wynik zależy od gry, sprzętu i serwera.', progress: null }
      ]
    },
    connections: {
      title: 'Połączenia',
      metrics: [
        { title: 'Stan połączenia', before: 'Nie sprawdzono', after: 'Test wymagany', foot: 'Ta strona nie widzi Wi-Fi, Ethernetu ani Bluetooth.', progress: null },
        { title: 'Ping / utrata pakietów', before: 'Nie mierzono', after: 'Wymaga testu', foot: 'Sieć i serwer decydują o wyniku. Niczego tu nie mierzymy ani nie zmieniamy.', progress: null }
      ]
    },
    proposals: {
      title: 'Propozycje',
      metrics: [
        { title: 'Pozycje autostartu', before: '12 pozycji', after: '9 · przykład', foot: 'Ilustracja listy do przejrzenia — żadnej aplikacji nie wyłączamy.', progress: 70 },
        { title: 'Częstotliwość ekranu', before: '60 Hz · demo', after: 'Do sprawdzenia', foot: 'Wyższy tryb jest możliwy tylko, jeśli obsługuje go ekran i sprzęt.', progress: null }
      ]
    },
    personalization: {
      title: 'Personalizacja Windows',
      metrics: [
        { title: 'Motyw Windows', before: 'Systemowy', after: 'Ciemny · demo', foot: 'To tylko podgląd. Ustawienia systemu Windows pozostają bez zmian.', progress: 72 },
        { title: 'Wygląd Blessed', before: 'Błękitny', after: 'Wybrany akcent', foot: 'Personalizacja zmienia wyłącznie makietę interfejsu.', progress: 84 }
      ]
    }
  };

  const scenarioButtons = Array.from(document.querySelectorAll('[data-scenario]'));
  const resultsContainer = document.getElementById('simulation-results');
  const simulationTitle = document.getElementById('simulation-title');
  const simulationState = document.getElementById('simulation-state');
  const simulationFeedback = document.getElementById('simulation-feedback');
  const runButton = document.getElementById('run-simulation');
  const undoButton = document.getElementById('undo-simulation');
  const simulatorCard = document.querySelector('.simulator-card');
  let activeScenario = 'gaming';
  let simulationComplete = false;

  function createCompareCard(metric, completed) {
    const card = document.createElement('article');
    card.className = `compare-card${metric.progress === null ? ' no-change' : ''}`;

    const heading = document.createElement('div');
    heading.className = 'compare-title';
    const title = document.createElement('span');
    title.textContent = metric.title;
    const exampleLabel = document.createElement('span');
    exampleLabel.className = 'example-label';
    exampleLabel.textContent = 'DANE DEMO';
    heading.append(title, exampleLabel);

    const values = document.createElement('div');
    values.className = 'compare-values';
    const before = document.createElement('div');
    before.className = 'compare-value';
    const beforeLabel = document.createElement('small');
    beforeLabel.textContent = 'PRZED';
    const beforeValue = document.createElement('strong');
    beforeValue.textContent = metric.before;
    before.append(beforeLabel, beforeValue);

    const arrow = document.createElement('span');
    arrow.className = 'compare-arrow';
    arrow.setAttribute('aria-hidden', 'true');
    arrow.textContent = '→';

    const after = document.createElement('div');
    after.className = 'compare-value after';
    const afterLabel = document.createElement('small');
    afterLabel.textContent = 'PO · DEMO';
    const afterValue = document.createElement('strong');
    afterValue.textContent = completed ? metric.after : '—';
    after.append(afterLabel, afterValue);
    values.append(before, arrow, after);

    const track = document.createElement('div');
    track.className = `compare-track${metric.progress === null ? ' no-fill' : ''}`;
    track.setAttribute('aria-hidden', 'true');
    const fill = document.createElement('span');
    if (completed && metric.progress !== null) fill.style.width = `${metric.progress}%`;
    track.appendChild(fill);

    const foot = document.createElement('p');
    foot.className = 'compare-foot';
    foot.textContent = completed ? metric.foot : 'Wybierz „Pokaż przykład”, aby odsłonić wynik demo.';
    card.append(heading, values, track, foot);
    return card;
  }

  function renderScenario() {
    const scenario = scenarios[activeScenario];
    if (!scenario || !resultsContainer) return;
    if (simulationTitle) simulationTitle.textContent = scenario.title;
    resultsContainer.replaceChildren(...scenario.metrics.map((metric) => createCompareCard(metric, simulationComplete)));
    scenarioButtons.forEach((button) => {
      const selected = button.dataset.scenario === activeScenario;
      button.classList.toggle('is-selected', selected);
      button.setAttribute('aria-checked', String(selected));
      button.tabIndex = selected ? 0 : -1;
    });
    if (simulationState) {
      simulationState.classList.toggle('is-complete', simulationComplete);
      simulationState.replaceChildren();
      const dot = document.createElement('i');
      simulationState.append(dot, document.createTextNode(simulationComplete ? ' Gotowe · demo' : ' Gotowe'));
    }
    if (runButton) {
      runButton.innerHTML = simulationComplete
        ? '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 10a6 6 0 1 1-1.8-4.3M16 4v4h-4"/></svg> Pokaż ponownie'
        : '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="m7.5 4.5 8 5.5-8 5.5v-11Z" fill="currentColor" stroke="none"/></svg> Pokaż przykład';
    }
    if (undoButton) undoButton.disabled = !simulationComplete;
    if (simulationFeedback && !simulationComplete) {
      simulationFeedback.textContent = 'Wybierz kategorię, aby zobaczyć przykładowy raport.';
      simulationFeedback.classList.remove('is-success');
    }
  }

  function selectScenario(name) {
    if (!scenarios[name]) return;
    activeScenario = name;
    simulationComplete = false;
    if (simulatorCard) simulatorCard.classList.remove('just-ran');
    renderScenario();
  }

  scenarioButtons.forEach((button) => button.addEventListener('click', () => {
    const category = button.dataset.scenario;
    selectScenario(category);
    setProgramView(category);
  }));
  programNavButtons.forEach((button) => button.addEventListener('click', () => selectScenario(button.dataset.programView)));
  const scenarioList = document.querySelector('.scenario-list');
  if (scenarioList) {
    scenarioList.addEventListener('keydown', (event) => {
      if (!['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'].includes(event.key)) return;
      event.preventDefault();
      const currentIndex = scenarioButtons.indexOf(document.activeElement);
      const direction = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : -1;
      const nextIndex = (currentIndex + direction + scenarioButtons.length) % scenarioButtons.length;
      scenarioButtons[nextIndex].focus();
      const category = scenarioButtons[nextIndex].dataset.scenario;
      selectScenario(category);
      setProgramView(category);
    });
  }

  if (runButton) {
    runButton.addEventListener('click', () => {
      simulationComplete = true;
      renderScenario();
      if (simulationFeedback) {
        simulationFeedback.textContent = 'Gotowe — to raport przykładowy, nie wynik z Twojego PC. Windows pozostał bez zmian.';
        simulationFeedback.classList.add('is-success');
      }
      if (simulatorCard) {
        simulatorCard.classList.remove('just-ran');
        window.requestAnimationFrame(() => simulatorCard.classList.add('just-ran'));
      }
    });
  }
  if (undoButton) {
    undoButton.addEventListener('click', () => {
      simulationComplete = false;
      if (simulatorCard) simulatorCard.classList.remove('just-ran');
      renderScenario();
      if (simulationFeedback) {
        simulationFeedback.textContent = 'Podgląd cofnięty. To była demonstracja — Windows pozostał bez zmian.';
        simulationFeedback.classList.add('is-success');
      }
    });
  }
  renderScenario();
})();
