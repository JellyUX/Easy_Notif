/*
 * Easy Notif - user settings panel and series bell.
 * Injected into Jellyfin's Settings screen (#/mypreferencesmenu): a contact address, one checkbox
 * per email category, the followed series, and a short data-handling notice. On a series detail page (#/details), a bell
 * in the action bar follows or stops following the series' new episodes, unless the script tag
 * carries data-series-bell="false". Talks to /EasyNotif/me/*.
 *
 * Design constraints (Synthese.md sections 4, 10):
 *  - Defensive and silent: a missing anchor -> skip. Never throw into Jellyfin Web; failures are
 *    console.warn only.
 *  - On demand only: /me/* is read once and cached for the session, and me/series/{id} once per
 *    detail page id. One guarded, debounced MutationObserver plus a hashchange re-arm. No polling.
 *  - Writes are optimistic: the checkbox / field reflects the change immediately and rolls back if
 *    the write fails.
 *
 * Pure helpers are exported for Vitest; the browser section (after the export) never runs under
 * jsdom because `module` is defined there and the IIFE returns immediately.
 */

(function () {
    'use strict';

    var PLUGIN_ID = '7a27339e-e774-4969-9755-8cd213dcc5e7';

    // Captured while the script runs: document.currentScript is null once it has finished.
    var SCRIPT_EL = typeof document !== 'undefined' ? document.currentScript : null;

    // Panel checkbox -> PUT me/preferences body field.
    var CAT_FIELDS = { news: 'News', recap: 'Recap', announcements: 'Announcements', episodes: 'Episodes' };

    var DICT = {
        en: {
            'panel.title': 'Email notifications',
            'panel.email.label': 'Contact email',
            'panel.email.help': 'The address your notifications are sent to. Leave blank to receive none.',
            'panel.email.rejected': 'This address was rejected by your mail server, so no email is sent to it. Please correct it, or save it again once it works.',
            'panel.cat.news': 'New media',
            'panel.cat.recap': 'Weekly recap',
            'panel.cat.announcements': 'Announcements',
            'panel.cat.episodes': 'New episodes',
            'series.title': 'Followed series',
            'series.empty': 'You follow no series. Use the bell on a series page to get its new episodes.',
            'series.remove': 'Remove',
            'panel.notice': 'Your address is stored by this plugin and sent to Resend (in the United States) to deliver the emails. It is deleted when the plugin is removed.',
            'panel.test': 'Send a test',
            'status.saved': 'Saved.',
            'status.invalid': 'Invalid email address.',
            'status.error': 'Could not save. Try again.',
            'status.testSent': 'Test email sent.',
            'status.testFailed': 'Could not send the test email.',
            'status.testNoEmail': 'Set a contact address first.',
            'bell.follow': 'Follow new episodes',
            'bell.unfollow': 'Following new episodes. Click to stop.',
            'bell.noEmail': 'No email will be sent: set a contact address in Settings, Email notifications.',
            'bell.optedOut': 'No email will be sent: New episodes is unticked in Settings, Email notifications.',
            'bell.rejected': 'No email will be sent: your contact address was rejected, correct it in Settings, Email notifications.'
        },
        fr: {
            'panel.title': 'Notifications par email',
            'panel.email.label': 'Email de contact',
            'panel.email.help': "L'adresse à laquelle vos notifications sont envoyées. Laissez vide pour n'en recevoir aucune.",
            'panel.email.rejected': "Cette adresse a été rejetée par votre serveur de messagerie : aucun email ne lui est envoyé. Merci de la corriger, ou de l'enregistrer à nouveau une fois réparée.",
            'panel.cat.news': 'Nouveautés',
            'panel.cat.recap': 'Résumé de la semaine',
            'panel.cat.announcements': 'Annonces',
            'panel.cat.episodes': 'Nouveaux épisodes',
            'series.title': 'Mes séries suivies',
            'series.empty': "Vous ne suivez aucune série. Utilisez la cloche sur la page d'une série pour recevoir ses nouveaux épisodes.",
            'series.remove': 'Retirer',
            'panel.notice': "Votre adresse est stockée par ce plugin et transmise à Resend (aux États-Unis) pour l'envoi des emails. Elle est supprimée à la désinstallation du plugin.",
            'panel.test': 'Envoyer un test',
            'status.saved': 'Enregistré.',
            'status.invalid': 'Adresse email invalide.',
            'status.error': 'Enregistrement impossible. Réessayez.',
            'status.testSent': 'Email de test envoyé.',
            'status.testFailed': "Impossible d'envoyer l'email de test.",
            'status.testNoEmail': "Renseignez d'abord une adresse de contact.",
            'bell.follow': 'Suivre les nouveaux épisodes',
            'bell.unfollow': 'Nouveaux épisodes suivis. Cliquez pour arrêter.',
            'bell.noEmail': 'Aucun mail ne sera envoyé : renseignez une adresse de contact dans Paramètres, Notifications par email.',
            'bell.optedOut': 'Aucun mail ne sera envoyé : « Nouveaux épisodes » est décoché dans Paramètres, Notifications par email.',
            'bell.rejected': 'Aucun mail ne sera envoyé : votre adresse de contact a été rejetée, corrigez-la dans Paramètres, Notifications par email.'
        }
    };

    function _isPrefsMenu(hash) {
        return /^#\/mypreferencesmenu([/?]|$)/i.test(hash || '');
    }

    // The item id of a detail page route (#/details?id=...&serverId=...), or null.
    function _detailsItemId(hash) {
        var match = /^#\/details\?(.*)$/i.exec(hash || '');
        if (!match) {
            return null;
        }
        var id = null;
        match[1].split('&').forEach(function (pair) {
            var eq = pair.indexOf('=');
            if (eq > 0 && pair.slice(0, eq) === 'id') {
                id = decodeURIComponent(pair.slice(eq + 1));
            }
        });
        return id && /^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.test(id) ? id : null;
    }

    // The bell is on unless the injected script tag says data-series-bell="false" (DEC-43).
    function _bellEnabled(scriptEl) {
        return !(scriptEl && scriptEl.getAttribute && scriptEl.getAttribute('data-series-bell') === 'false');
    }

    // Icon and tooltip of the bell. A followed series whose emails cannot go out (no address, or
    // the New episodes category unticked) says so in the tooltip (DEC-47).
    function _bellView(locale, following, data) {
        var warn = null;
        if (following && data) {
            if (!data.email) {
                warn = 'bell.noEmail';
            } else if (data.emailRejected) {
                warn = 'bell.rejected';
            } else if (data.episodes === false) {
                warn = 'bell.optedOut';
            }
        }
        var title = _t(locale, following ? 'bell.unfollow' : 'bell.follow');
        return {
            icon: following ? 'notifications_active' : 'notifications_none',
            title: warn ? title + ' ' + _t(locale, warn) : title,
            active: !!following,
            warn: !!warn
        };
    }

    function _applyBell(button, view) {
        button.title = view.title;
        button.setAttribute('aria-label', view.title);
        button.setAttribute('aria-pressed', view.active ? 'true' : 'false');
        button.classList.toggle('enotif-series-bell-active', view.active);
        button.classList.toggle('enotif-series-bell-warn', view.warn);
        button.querySelector('.detailButton-icon').className = 'material-icons detailButton-icon ' + view.icon;
    }

    // Same markup as the native action bar buttons, so it inherits their look.
    function _buildBell(itemId, view) {
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'button-flat detailButton emby-button enotif-series-bell';
        button.setAttribute('data-id', itemId);

        var content = document.createElement('div');
        content.className = 'detailButton-content';
        var icon = document.createElement('span');
        icon.className = 'material-icons detailButton-icon';
        icon.setAttribute('aria-hidden', 'true');
        content.appendChild(icon);
        button.appendChild(content);

        _applyBell(button, view);
        return button;
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

        // Hard bounce or suppression reported by Resend (PRIV-05); hidden again once saved.
        var rejected = document.createElement('div');
        rejected.className = 'enotif-email-rejected';
        rejected.setAttribute('role', 'alert');
        rejected.textContent = _t(locale, 'panel.email.rejected');
        rejected.hidden = !(data && data.emailRejected);
        emailContainer.appendChild(rejected);

        var status = document.createElement('div');
        status.className = 'enotif-status';
        status.setAttribute('role', 'status');
        emailContainer.appendChild(status);

        section.appendChild(emailContainer);

        [
            { cat: 'news', on: !!(data && data.news) },
            { cat: 'recap', on: !!(data && data.recap) },
            { cat: 'announcements', on: !!(data && data.announcements) },
            { cat: 'episodes', on: !!(data && data.episodes) }
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

        // Filled once me/series answers; stays empty if it fails.
        var seriesSlot = document.createElement('div');
        seriesSlot.className = 'enotif-series-slot';
        section.appendChild(seriesSlot);

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

    // The followed series list (DEC-46): name and a Remove button per series, or an empty hint.
    function _buildSeriesList(locale, series) {
        var wrap = document.createElement('div');
        wrap.className = 'enotif-series';

        var title = document.createElement('h3');
        title.className = 'enotif-series-title';
        title.textContent = _t(locale, 'series.title');
        wrap.appendChild(title);

        var empty = document.createElement('p');
        empty.className = 'fieldDescription enotif-series-empty';
        empty.textContent = _t(locale, 'series.empty');
        empty.hidden = !!(series && series.length);
        wrap.appendChild(empty);

        var list = document.createElement('ul');
        list.className = 'enotif-series-list';
        (series || []).forEach(function (s) {
            var item = document.createElement('li');
            item.className = 'enotif-series-item';
            item.setAttribute('data-id', s.id);

            var name = document.createElement('span');
            name.className = 'enotif-series-name';
            name.textContent = s.name;
            item.appendChild(name);

            var remove = document.createElement('button');
            remove.setAttribute('is', 'emby-button');
            remove.type = 'button';
            remove.className = 'raised enotif-series-remove';
            remove.textContent = _t(locale, 'series.remove');
            item.appendChild(remove);

            list.appendChild(item);
        });
        wrap.appendChild(list);

        return wrap;
    }

    var api = {
        _isPrefsMenu: _isPrefsMenu,
        _pickLocale: _pickLocale,
        _t: _t,
        _escHtml: _escHtml,
        _validEmail: _validEmail,
        _buildPanel: _buildPanel,
        _buildSeriesList: _buildSeriesList,
        _detailsItemId: _detailsItemId,
        _bellEnabled: _bellEnabled,
        _bellView: _bellView,
        _buildBell: _buildBell,
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

    // Per detail page id: { following, busy, ready } once me/series/{id} answered 200, null when it
    // answered 404 (not a series, or not visible) or failed: no bell for that id this session.
    var bell = { enabled: _bellEnabled(SCRIPT_EL), byId: {} };

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

    function _send(type, path) {
        return window.ApiClient.ajax({ type: type, url: _url(path) });
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
                // Announcements are opt-out: only an explicit false unticks the box.
                announcements: results[0].announcements !== false,
                // New episodes are opt-out too (DEC-40).
                episodes: results[0].episodes !== false,
                emailRejected: results[0].emailRejected === true,
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
                    // Any save lifts the rejected mark server-side (DEC-50).
                    state.data.emailRejected = false;
                }
                panel.querySelector('.enotif-email-rejected').hidden = true;
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
                body[CAT_FIELDS[cat]] = box.checked;
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

    // A removed row is hidden at once and comes back if the call fails.
    function _bindSeriesList(list) {
        var empty = list.querySelector('.enotif-series-empty');
        function _refreshEmpty() {
            empty.hidden = !!list.querySelector('.enotif-series-item:not([hidden])');
        }
        Array.prototype.forEach.call(list.querySelectorAll('.enotif-series-item'), function (item) {
            var id = item.getAttribute('data-id');
            item.querySelector('.enotif-series-remove').addEventListener('click', function () {
                item.hidden = true;
                _refreshEmpty();
                _send('DELETE', 'me/series/' + id).then(function () {
                    if (bell.byId[id]) {
                        bell.byId[id].following = false;
                    }
                    item.parentNode.removeChild(item);
                }).catch(function (err) {
                    console.warn('[EasyNotif] could not stop following the series:', err);
                    item.hidden = false;
                    _refreshEmpty();
                });
            });
        });
    }

    // Read each time the panel is built, so a bell clicked earlier in the session shows up.
    function _renderSeries(panel, locale) {
        window.ApiClient.getJSON(_url('me/series')).then(function (series) {
            var slot = panel.querySelector('.enotif-series-slot');
            if (!slot || !panel.isConnected) {
                return;
            }
            var list = _buildSeriesList(locale, series);
            slot.appendChild(list);
            _bindSeriesList(list);
        }).catch(function (err) {
            console.warn('[EasyNotif] could not load your followed series:', err);
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
            _renderSeries(panel, locale);
        });
    }

    function _seriesEntry(id) {
        if (Object.prototype.hasOwnProperty.call(bell.byId, id)) {
            return bell.byId[id];
        }
        var entry = { following: false, busy: false, ready: null };
        entry.ready = window.ApiClient.getJSON(_url('me/series/' + id)).then(function (res) {
            entry.following = !!(res && res.following);
            return entry;
        }).catch(function () {
            // 404 for anything but a visible series; any other failure also means no bell.
            bell.byId[id] = null;
            return null;
        });
        bell.byId[id] = entry;
        return entry;
    }

    function _bindBell(button, id, entry, locale) {
        button.addEventListener('click', function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            if (entry.busy) {
                return;
            }
            entry.busy = true;
            var next = !entry.following;
            entry.following = next;
            _applyBell(button, _bellView(locale, next, state.data));
            var request = next ? _send('PUT', 'me/series/' + id + '?source=bell') : _send('DELETE', 'me/series/' + id);
            request.catch(function (err) {
                console.warn('[EasyNotif] could not change the series subscription:', err);
                entry.following = !next;
                _applyBell(button, _bellView(locale, entry.following, state.data));
            }).then(function () {
                entry.busy = false;
            });
        });
    }

    // Detail pages of anything but a visible series get no bell: me/series/{id} answers 404.
    function _tickBell() {
        if (!bell.enabled) {
            return;
        }
        var id = _detailsItemId(location.hash);
        if (!id || bell.byId[id] === null) {
            return;
        }
        var bar = document.querySelector('.page:not(.hide) .mainDetailButtons');
        if (!bar) {
            return;
        }
        var existing = bar.querySelector('.enotif-series-bell');
        if (existing && existing.getAttribute('data-id') === id) {
            // Jellyfin keeps a visited detail page: refresh the tooltip when a preference changed.
            var known = bell.byId[id];
            if (known && !known.busy) {
                var view = _bellView(_locale(), known.following, state.data);
                if (existing.title !== view.title) {
                    _applyBell(existing, view);
                }
            }
            return;
        }
        if (existing) {
            existing.parentNode.removeChild(existing);
        }
        var entry = _seriesEntry(id);
        if (!entry) {
            return;
        }
        Promise.all([entry.ready, _load()]).then(function (results) {
            if (!results[0] || !bar.isConnected || bar.querySelector('.enotif-series-bell')
                || _detailsItemId(location.hash) !== id) {
                return;
            }
            var locale = _locale();
            var button = _buildBell(id, _bellView(locale, entry.following, state.data));
            bar.insertBefore(button, bar.querySelector('.btnMoreCommands'));
            _bindBell(button, id, entry, locale);
        });
    }

    // Cheap and idempotent: a route check, one querySelector, and an early return when the panel is
    // already there. Safe to run on every DOM mutation (the settings menu is a React page that
    // renders Loading first, then the real menu, and may re-render and drop our node).
    function _tick() {
        try {
            if (typeof location === 'undefined') {
                return;
            }
            if (!_isPrefsMenu(location.hash)) {
                _tickBell();
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
