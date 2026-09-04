/*
 * Easy Notif - user settings panel.
 * Injected into Jellyfin's Settings screen (#/mypreferencesmenu): a contact address, one checkbox
 * per email category, and a short data-handling notice. Talks to /EasyNotif/me/*.
 *
 * Design constraints (Synthese.md sections 4, 10):
 *  - Defensive and silent: a missing anchor -> skip. Never throw into Jellyfin Web; failures are
 *    console.warn only.
 *  - On demand only: /me/* is read once and cached for the session. One guarded, debounced
 *    MutationObserver plus a hashchange re-arm. No polling.
 *  - Writes are optimistic: the checkbox / field reflects the change immediately and rolls back if
 *    the write fails.
 *
 * Pure helpers are exported for Vitest; the browser section (after the export) never runs under
 * jsdom because `module` is defined there and the IIFE returns immediately.
 */

(function () {
    'use strict';

    var PLUGIN_ID = '7a27339e-e774-4969-9755-8cd213dcc5e7';

    var DICT = {
        en: {
            'panel.title': 'Email notifications',
            'panel.email.label': 'Contact email',
            'panel.email.help': 'The address your notifications are sent to. Leave blank to receive none.',
            'panel.cat.news': 'New media',
            'panel.cat.recap': 'Weekly recap',
            'panel.notice': 'Your address is stored by this plugin and sent to Resend (in the United States) to deliver the emails. It is deleted when the plugin is removed.',
            'panel.test': 'Send a test',
            'status.saved': 'Saved.',
            'status.invalid': 'Invalid email address.',
            'status.error': 'Could not save. Try again.',
            'status.testSent': 'Test email sent.',
            'status.testFailed': 'Could not send the test email.',
            'status.testNoEmail': 'Set a contact address first.'
        },
        fr: {
            'panel.title': 'Notifications par email',
            'panel.email.label': 'Email de contact',
            'panel.email.help': "L'adresse à laquelle vos notifications sont envoyées. Laissez vide pour n'en recevoir aucune.",
            'panel.cat.news': 'Nouveautés',
            'panel.cat.recap': 'Résumé de la semaine',
            'panel.notice': "Votre adresse est stockée par ce plugin et transmise à Resend (aux États-Unis) pour l'envoi des emails. Elle est supprimée à la désinstallation du plugin.",
            'panel.test': 'Envoyer un test',
            'status.saved': 'Enregistré.',
            'status.invalid': 'Adresse email invalide.',
            'status.error': 'Enregistrement impossible. Réessayez.',
            'status.testSent': 'Email de test envoyé.',
            'status.testFailed': "Impossible d'envoyer l'email de test.",
            'status.testNoEmail': "Renseignez d'abord une adresse de contact."
        }
    };

    function _isPrefsMenu(hash) {
        return /^#\/mypreferencesmenu([/?]|$)/i.test(hash || '');
    }

    // fr when either the <html lang> or the browser locale starts with "fr", else en (default).
    function _pickLocale(htmlLang, navLang) {
        var starts = function (v) { return String(v || '').toLowerCase().indexOf('fr') === 0; };
        return (starts(htmlLang) || starts(navLang)) ? 'fr' : 'en';
    }

    function _t(locale, key) {
        return (DICT[locale] && DICT[locale][key]) || DICT.en[key] || key;
    }

    function _escHtml(value) {
        if (value === null || value === undefined) {
            return '';
        }
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // Empty is valid (it clears the address). Otherwise a minimal shape check; the server does the
    // real validation with MailAddress.
    function _validEmail(value) {
        var trimmed = String(value == null ? '' : value).trim();
        return trimmed === '' || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmed);
    }

    // Detached DOM. Everything is set via textContent / value properties, so a hostile stored value
    // is data, never markup.
    function _buildPanel(locale, data) {
        var section = document.createElement('div');
        section.className = 'verticalSection verticalSection-extrabottompadding enotif-section';

        var title = document.createElement('h2');
        title.className = 'sectionTitle';
        title.textContent = _t(locale, 'panel.title');
        section.appendChild(title);

        var emailContainer = document.createElement('div');
        emailContainer.className = 'inputContainer';

        var emailLabel = document.createElement('label');
        emailLabel.className = 'inputLabel inputLabelUnfocused';
        emailLabel.setAttribute('for', 'enotifUserEmail');
        emailLabel.textContent = _t(locale, 'panel.email.label');
        emailContainer.appendChild(emailLabel);

        var emailInput = document.createElement('input');
        emailInput.id = 'enotifUserEmail';
        emailInput.type = 'text';
        emailInput.autocomplete = 'off';
        emailInput.className = 'emby-input enotif-user-email';
        emailInput.value = (data && data.email) || '';
        emailContainer.appendChild(emailInput);

        var emailHelp = document.createElement('div');
        emailHelp.className = 'fieldDescription';
        emailHelp.textContent = _t(locale, 'panel.email.help');
        emailContainer.appendChild(emailHelp);

        var status = document.createElement('div');
        status.className = 'enotif-status';
        status.setAttribute('role', 'status');
        emailContainer.appendChild(status);

        section.appendChild(emailContainer);

        [
            { cat: 'news', on: !!(data && data.news) },
            { cat: 'recap', on: !!(data && data.recap) }
        ].forEach(function (spec) {
            var wrap = document.createElement('div');
            wrap.className = 'checkboxContainer';

            var label = document.createElement('label');

            var box = document.createElement('input');
            box.type = 'checkbox';
            box.className = 'enotif-cat';
            box.setAttribute('data-cat', spec.cat);
            box.checked = spec.on;

            var text = document.createElement('span');
            text.textContent = _t(locale, 'panel.cat.' + spec.cat);

            label.appendChild(box);
            label.appendChild(text);
            wrap.appendChild(label);
            section.appendChild(wrap);
        });

        var testButton = document.createElement('button');
        testButton.setAttribute('is', 'emby-button');
        testButton.type = 'button';
        testButton.className = 'raised enotif-test';
        testButton.textContent = _t(locale, 'panel.test');
        testButton.disabled = !(data && data.email);
        section.appendChild(testButton);

        var notice = document.createElement('p');
        notice.className = 'fieldDescription enotif-notice';
        notice.textContent = _t(locale, 'panel.notice');
        section.appendChild(notice);

        return section;
    }

    var api = {
        _isPrefsMenu: _isPrefsMenu,
        _pickLocale: _pickLocale,
        _t: _t,
        _escHtml: _escHtml,
        _validEmail: _validEmail,
        _buildPanel: _buildPanel,
        DICT: DICT,
        PLUGIN_ID: PLUGIN_ID
    };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
        return;
    }

    if (typeof window === 'undefined' || typeof document === 'undefined') {
        return;
    }

    // ---------------------------------------------------------------------------
    // Browser section - runs only in a real Jellyfin Web page.
    // ---------------------------------------------------------------------------

    var state = { data: null, loadPromise: null, errCount: 0 };

    function _retry(probe, attemptsLeft, delayMs) {
        var value = probe();
        if (value) {
            return Promise.resolve(value);
        }
        if (attemptsLeft <= 0) {
            return Promise.resolve(null);
        }
        return new Promise(function (resolve) {
            setTimeout(function () { resolve(_retry(probe, attemptsLeft - 1, delayMs)); }, delayMs);
        });
    }

    function _url(path) {
        return window.ApiClient.getUrl('EasyNotif/' + path);
    }

    function _putJson(path, body) {
        return window.ApiClient.ajax({
            type: 'PUT',
            url: _url(path),
            data: JSON.stringify(body),
            contentType: 'application/json'
        });
    }

    function _postJson(path, body) {
        return window.ApiClient.ajax({
            type: 'POST',
            url: _url(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function _locale() {
        return _pickLocale(
            document.documentElement.getAttribute('lang'),
            window.navigator && window.navigator.language);
    }

    function _load() {
        if (state.data) {
            return Promise.resolve(state.data);
        }
        if (state.loadPromise) {
            return state.loadPromise;
        }
        if (state.errCount >= 3) {
            return Promise.resolve(null);
        }

        state.loadPromise = Promise.all([
            window.ApiClient.getJSON(_url('me/preferences')),
            window.ApiClient.getJSON(_url('me/contact-email'))
        ]).then(function (results) {
            state.data = {
                news: !!results[0].news,
                recap: !!results[0].recap,
                email: results[1].email || null
            };
            state.loadPromise = null;
            return state.data;
        }).catch(function (err) {
            state.errCount++;
            state.loadPromise = null;
            console.warn('[EasyNotif] could not load your notification settings:', err);
            return null;
        });

        return state.loadPromise;
    }

    function _setStatus(el, text, isError) {
        el.textContent = text;
        el.classList.toggle('enotif-status-error', !!isError);
    }

    function _bind(panel, locale) {
        var email = panel.querySelector('.enotif-user-email');
        var status = panel.querySelector('.enotif-status');
        var testButton = panel.querySelector('.enotif-test');

        function _refreshTestButton() {
            testButton.disabled = !(state.data && state.data.email);
        }

        testButton.addEventListener('click', function () {
            if (!(state.data && state.data.email)) {
                _setStatus(status, _t(locale, 'status.testNoEmail'), true);
                return;
            }
            testButton.disabled = true;
            _postJson('me/test', { Lang: locale }).then(function (res) {
                var ok = !res || res.ok !== false;
                _setStatus(status, _t(locale, ok ? 'status.testSent' : 'status.testFailed'), !ok);
            }).catch(function (err) {
                console.warn('[EasyNotif] could not send the test email:', err);
                _setStatus(status, _t(locale, 'status.testFailed'), true);
            }).then(function () {
                _refreshTestButton();
            });
        });

        email.addEventListener('blur', function () {
            var value = email.value.trim();
            if (!_validEmail(value)) {
                _setStatus(status, _t(locale, 'status.invalid'), true);
                return;
            }
            _putJson('me/contact-email', { Email: value }).then(function () {
                if (state.data) {
                    state.data.email = value || null;
                }
                _refreshTestButton();
                _setStatus(status, _t(locale, 'status.saved'), false);
            }).catch(function (err) {
                console.warn('[EasyNotif] could not save the contact address:', err);
                _setStatus(status, _t(locale, 'status.error'), true);
            });
        });

        Array.prototype.forEach.call(panel.querySelectorAll('.enotif-cat'), function (box) {
            box.addEventListener('change', function () {
                var cat = box.getAttribute('data-cat');
                var body = {};
                body[cat === 'news' ? 'News' : 'Recap'] = box.checked;
                if (state.data) {
                    state.data[cat] = box.checked;
                }
                _putJson('me/preferences', body).catch(function (err) {
                    console.warn('[EasyNotif] could not save the preference:', err);
                    box.checked = !box.checked;
                    if (state.data) {
                        state.data[cat] = box.checked;
                    }
                });
            });
        });
    }

    function _render(anchor) {
        if (anchor.querySelector('.enotif-section')) {
            return;
        }
        var locale = _locale();
        _load().then(function (data) {
            if (!data || anchor.querySelector('.enotif-section') || !anchor.isConnected) {
                return;
            }
            var panel = _buildPanel(locale, data);
            anchor.appendChild(panel);
            _bind(panel, locale);
        });
    }

    // Cheap and idempotent: a route check, one querySelector, and an early return when the panel is
    // already there. Safe to run on every DOM mutation (the settings menu is a React page that
    // renders Loading first, then the real menu, and may re-render and drop our node).
    function _tick() {
        try {
            if (typeof location === 'undefined' || !_isPrefsMenu(location.hash)) {
                return;
            }
            var anchor = document.querySelector('#myPreferencesMenuPage .readOnlyContent');
            if (anchor) {
                _render(anchor);
            }
        } catch (err) {
            console.warn('[EasyNotif] settings panel tick failed:', err);
        }
    }

    // The React settings menu renders <Loading/> first, then the real menu once its queries resolve,
    // so a single tick after navigation is usually too early. Retry over a couple of seconds; every
    // call is a fast no-op once the panel is in place.
    function _scheduleTicks() {
        _tick();
        [150, 400, 900, 1800, 3000].forEach(function (delay) { setTimeout(_tick, delay); });
    }

    function _start() {
        _retry(function () { return window.ApiClient; }, 10, 300).then(function (apiClient) {
            if (!apiClient) {
                return;
            }
            // The stable web client renders routes inside #reactRoot, not .mainAnimatedPages, so
            // observe document.body to catch every navigation.
            new MutationObserver(_tick).observe(document.body, { childList: true, subtree: true });
            window.addEventListener('hashchange', _scheduleTicks);
            _scheduleTicks();
        }).catch(function (err) {
            console.warn('[EasyNotif] settings panel bootstrap failed:', err);
        });
    }

    _start();
})();
