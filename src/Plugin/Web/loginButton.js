(function () {
    'use strict';

    var BUTTON_ID = 'ssoLoginButton';
    var cfg = window.__ssoLoginButton || {};

    if (cfg.enabled === false) {
        return;
    }

    var label = (typeof cfg.label === 'string' && cfg.label.length > 0)
        ? cfg.label
        : 'Sign in with SSO';

    function createButton() {
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.setAttribute('type', 'button');
        btn.id = BUTTON_ID;
        btn.className = 'raised block emby-button';
        btn.style.marginTop = '0.5em';

        var span = document.createElement('span');
        span.textContent = label;
        btn.appendChild(span);

        btn.addEventListener('click', function () {
            // Full navigation (not fetch) so the server-side redirect flow to Keycloak runs.
            window.location.href = '/sso/authorize';
        });

        return btn;
    }

    function tryInject() {
        var loginPage = document.querySelector('#loginPage');
        if (!loginPage || loginPage.querySelector('#' + BUTTON_ID)) {
            return;
        }

        var container = loginPage.querySelector('.readOnlyContent');
        if (!container) {
            return;
        }

        container.appendChild(createButton());
    }

    // The login view is rendered and re-rendered client-side by the SPA router, so watch the DOM
    // and (re)inject the button whenever the login page appears.
    var observer = new MutationObserver(tryInject);
    observer.observe(document.body, { childList: true, subtree: true });

    // Handle the case where the login page is already present when this script runs.
    tryInject();
})();
