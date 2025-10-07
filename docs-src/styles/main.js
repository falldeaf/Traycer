(function () {
  const config = (window.traycerDocsConfig || {});
  const repoUrl = config.repoUrl || 'https://github.com/SimX/traycer';

  const nav = document.querySelector('.navbar .container');
  if (!nav) {
    return;
  }

  const brand = nav.querySelector('.navbar-brand');
  if (brand && !brand.classList.contains('traycer-brand')) {
    const logoPath = (config.logoPath || (window.docfx && window.docfx.baseUrl ? window.docfx.baseUrl + 'images/traycer-logo.svg' : 'images/traycer-logo.svg'));
    const titleText = brand.textContent.trim();
    brand.innerHTML = '<img src="' + logoPath + '" alt="Traycer logo" />' + titleText;
    brand.classList.add('traycer-brand');
  }

  const actions = document.createElement('div');
  actions.className = 'traycer-nav-actions';

  const githubLink = document.createElement('a');
  githubLink.className = 'traycer-icon traycer-github';
  githubLink.href = repoUrl;
  githubLink.target = '_blank';
  githubLink.rel = 'noopener';
  githubLink.setAttribute('aria-label', 'Traycer GitHub repository');
  githubLink.innerHTML = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.01.08-2.11 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.91.08 2.11.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.19 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8z"></path></svg>';
  actions.appendChild(githubLink);

  const themeBtn = document.createElement('button');
  themeBtn.type = 'button';
  themeBtn.className = 'traycer-icon traycer-theme-toggle';
  themeBtn.setAttribute('aria-label', 'Toggle theme');
  themeBtn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3a1 1 0 0 1 1 1v2a1 1 0 0 1-2 0V4a1 1 0 0 1 1-1zm0 14a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm9-4a1 1 0 0 1-1 1h-2a1 1 0 0 1 0-2h2a1 1 0 0 1 1 1zM7 14a1 1 0 0 1-1 1H4a1 1 0 0 1 0-2h2a1 1 0 0 1 1 1zm10.95 6.364a1 1 0 0 1-1.414 0l-1.414-1.414a1 1 0 0 1 1.414-1.414l1.414 1.414a1 1 0 0 1 0 1.414zM7.88 8.293l-1.415-1.415a1 1 0 0 1 1.415-1.414l1.414 1.414A1 1 0 0 1 7.88 8.293zm8.485-1.414a1 1 0 0 1 0-1.414l1.414-1.414a1 1 0 1 1 1.414 1.414l-1.414 1.414a1 1 0 0 1-1.414 0zM7.88 15.707a1 1 0 0 1 0 1.414l-1.414 1.414a1 1 0 0 1-1.415-1.414l1.415-1.414a1 1 0 0 1 1.414 0z"></path></svg>';
  actions.appendChild(themeBtn);

  const searchForm = nav.querySelector('.navbar-form');
  if (searchForm) {
    searchForm.classList.add('traycer-search');
    actions.appendChild(searchForm);
  }

  nav.appendChild(actions);

  const storageKey = 'traycer-docs-theme';
  const preferred = localStorage.getItem(storageKey);
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)');

  function applyTheme(mode) {
    document.documentElement.setAttribute('data-traycer-theme', mode);
    localStorage.setItem(storageKey, mode);
  }

  const initial = preferred || (prefersDark.matches ? 'dark' : 'light');
  applyTheme(initial);

  themeBtn.addEventListener('click', function () {
    const current = document.documentElement.getAttribute('data-traycer-theme');
    const next = current === 'dark' ? 'light' : 'dark';
    applyTheme(next);
  });

  prefersDark.addEventListener('change', function (event) {
    if (!localStorage.getItem(storageKey)) {
      applyTheme(event.matches ? 'dark' : 'light');
    }
  });
})();
