import { describe, it, expect } from 'vitest';
import enotifUser from '../src/Jellyfin.Plugin.EasyNotif/Web/enotif-user.js';

const {
    _isPrefsMenu, _pickLocale, _t, _escHtml, _validEmail, _buildPanel,
    _detailsItemId, _bellEnabled, _bellView, _buildBell, DICT, PLUGIN_ID
} = enotifUser;

describe('_isPrefsMenu', () => {
    it('matches the settings menu route, with or without a query', () => {
        expect(_isPrefsMenu('#/mypreferencesmenu')).toBe(true);
        expect(_isPrefsMenu('#/mypreferencesmenu?userId=abc')).toBe(true);
        expect(_isPrefsMenu('#/mypreferencesmenu/')).toBe(true);
    });

    it('does not match other preference pages or the home page', () => {
        expect(_isPrefsMenu('#/mypreferencesdisplay')).toBe(false);
        expect(_isPrefsMenu('#/mypreferencesmenuextra')).toBe(false);
        expect(_isPrefsMenu('#/home')).toBe(false);
        expect(_isPrefsMenu('')).toBe(false);
        expect(_isPrefsMenu(null)).toBe(false);
    });
});

describe('_pickLocale', () => {
    it('picks fr when the html lang or the browser locale starts with fr', () => {
        expect(_pickLocale('fr', 'en-US')).toBe('fr');
        expect(_pickLocale('fr-FR', null)).toBe('fr');
        expect(_pickLocale(null, 'FR-ca')).toBe('fr');
    });

    it('defaults to en otherwise', () => {
        expect(_pickLocale('en-US', 'en')).toBe('en');
        expect(_pickLocale(null, null)).toBe('en');
        expect(_pickLocale('de', 'de-DE')).toBe('en');
    });
});

describe('_t', () => {
    it('returns the translation for a known key in each locale', () => {
        expect(_t('fr', 'panel.title')).toBe('Notifications par email');
        expect(_t('en', 'panel.title')).toBe('Email notifications');
    });

    it('falls back to English then to the key itself', () => {
        expect(_t('de', 'panel.title')).toBe('Email notifications');
        expect(_t('fr', 'no.such.key')).toBe('no.such.key');
    });
});

describe('_escHtml', () => {
    it('escapes HTML special characters and handles nullish', () => {
        expect(_escHtml('<b>& "x"</b>')).toBe('&lt;b&gt;&amp; &quot;x&quot;&lt;/b&gt;');
        expect(_escHtml(null)).toBe('');
    });
});

describe('_validEmail', () => {
    it('treats an empty value as valid (it clears the address)', () => {
        expect(_validEmail('')).toBe(true);
        expect(_validEmail('   ')).toBe(true);
        expect(_validEmail(null)).toBe(true);
    });

    it('accepts a plausible address and rejects a broken one', () => {
        expect(_validEmail('alice@example.org')).toBe(true);
        expect(_validEmail(' alice@example.org ')).toBe(true);
        expect(_validEmail('nope')).toBe(false);
        expect(_validEmail('a@b')).toBe(false);
        expect(_validEmail('a@ b.co')).toBe(false);
    });
});

describe('_buildPanel', () => {
    it('reflects the stored data: email value and checkbox states', () => {
        const panel = _buildPanel('en', { news: true, recap: false, email: 'alice@example.org' });

        expect(panel.querySelector('.enotif-user-email').value).toBe('alice@example.org');
        expect(panel.querySelector('.enotif-cat[data-cat="news"]').checked).toBe(true);
        expect(panel.querySelector('.enotif-cat[data-cat="recap"]').checked).toBe(false);
        expect(panel.textContent).toContain('Resend');
    });

    it('renders the announcements checkbox with its FR and EN labels', () => {
        const on = _buildPanel('fr', { announcements: true });
        const off = _buildPanel('en', { announcements: false });

        expect(on.querySelector('.enotif-cat[data-cat="announcements"]').checked).toBe(true);
        expect(on.textContent).toContain('Annonces');
        expect(off.querySelector('.enotif-cat[data-cat="announcements"]').checked).toBe(false);
        expect(off.textContent).toContain('Announcements');
    });

    it('handles null data (no email, opted out)', () => {
        const panel = _buildPanel('fr', null);

        expect(panel.querySelector('.enotif-user-email').value).toBe('');
        expect(panel.querySelector('.enotif-cat[data-cat="news"]').checked).toBe(false);
        expect(panel.textContent).toContain('Notifications par email');
    });

    it('renders a test button, disabled until a contact address is set', () => {
        const withEmail = _buildPanel('en', { email: 'alice@example.org' });
        const withoutEmail = _buildPanel('en', null);

        expect(withEmail.querySelector('.enotif-test')).not.toBeNull();
        expect(withEmail.querySelector('.enotif-test').disabled).toBe(false);
        expect(withoutEmail.querySelector('.enotif-test').disabled).toBe(true);
    });

    it('treats a hostile stored address as data, never markup', () => {
        const panel = _buildPanel('en', { email: '<img src=x onerror=alert(1)>' });

        expect(panel.querySelector('.enotif-user-email').value).toBe('<img src=x onerror=alert(1)>');
        expect(panel.innerHTML).not.toContain('<img');
    });
});

describe('_detailsItemId', () => {
    const id = '579ee07037daeaede7cc5d5021a7da9f';

    it('reads the id of a detail page route, in any parameter order', () => {
        expect(_detailsItemId(`#/details?id=${id}&serverId=abc`)).toBe(id);
        expect(_detailsItemId(`#/details?serverId=abc&id=${id}`)).toBe(id);
        expect(_detailsItemId('#/details?id=579ee070-37da-eaed-e7cc-5d5021a7da9f')).toBe('579ee070-37da-eaed-e7cc-5d5021a7da9f');
    });

    it('returns null off a detail page or for a malformed id', () => {
        expect(_detailsItemId(`#/list?parentId=${id}`)).toBeNull();
        expect(_detailsItemId('#/details?id=../../admin')).toBeNull();
        expect(_detailsItemId('#/details?serverId=abc')).toBeNull();
        expect(_detailsItemId('')).toBeNull();
        expect(_detailsItemId(null)).toBeNull();
    });
});

describe('_bellEnabled', () => {
    it('is off only when the script tag says data-series-bell="false"', () => {
        const tag = document.createElement('script');
        expect(_bellEnabled(tag)).toBe(true);
        tag.setAttribute('data-series-bell', 'false');
        expect(_bellEnabled(tag)).toBe(false);
        tag.setAttribute('data-series-bell', 'true');
        expect(_bellEnabled(tag)).toBe(true);
    });

    it('stays on when the script tag is unknown', () => {
        expect(_bellEnabled(null)).toBe(true);
    });
});

describe('_bellView', () => {
    const ready = { email: 'alice@example.org', episodes: true };

    it('switches icon, state and tooltip when following', () => {
        const off = _bellView('en', false, ready);
        const on = _bellView('fr', true, ready);

        expect(off).toEqual({ icon: 'notifications_none', title: 'Follow new episodes', active: false, warn: false });
        expect(on).toEqual({ icon: 'notifications_active', title: 'Nouveaux épisodes suivis. Cliquez pour arrêter.', active: true, warn: false });
    });

    it('warns a follower who has no address or unticked New episodes', () => {
        const noEmail = _bellView('en', true, { email: null, episodes: true });
        const optedOut = _bellView('fr', true, { email: 'alice@example.org', episodes: false });

        expect(noEmail.warn).toBe(true);
        expect(noEmail.title).toContain('set a contact address');
        expect(optedOut.warn).toBe(true);
        expect(optedOut.title).toContain('« Nouveaux épisodes » est décoché');
    });

    it('does not warn before following, nor without preference data', () => {
        expect(_bellView('en', false, { email: null }).warn).toBe(false);
        expect(_bellView('en', true, null).warn).toBe(false);
    });
});

describe('_buildBell', () => {
    it('builds a native-looking action button carrying the item id and state', () => {
        const bell = _buildBell('abc', _bellView('en', true, { email: null }));

        expect(bell.tagName).toBe('BUTTON');
        expect(bell.type).toBe('button');
        expect(bell.classList.contains('detailButton')).toBe(true);
        expect(bell.classList.contains('enotif-series-bell-active')).toBe(true);
        expect(bell.classList.contains('enotif-series-bell-warn')).toBe(true);
        expect(bell.getAttribute('data-id')).toBe('abc');
        expect(bell.getAttribute('aria-pressed')).toBe('true');
        expect(bell.querySelector('.detailButton-icon').classList.contains('notifications_active')).toBe(true);
        expect(bell.title).toBe(bell.getAttribute('aria-label'));
    });
});

describe('DICT', () => {
    it('has the same keys in fr and en', () => {
        expect(Object.keys(DICT.fr).sort()).toEqual(Object.keys(DICT.en).sort());
    });

    it('translates the test button per locale', () => {
        expect(_t('fr', 'panel.test')).toBe('Envoyer un test');
        expect(_t('en', 'panel.test')).toBe('Send a test');
        expect(_t('fr', 'panel.test')).not.toBe(_t('en', 'panel.test'));
    });
});

describe('PLUGIN_ID', () => {
    it('is the plugin GUID', () => {
        expect(PLUGIN_ID).toBe('7a27339e-e774-4969-9755-8cd213dcc5e7');
    });
});

describe('module import', () => {
    it('exports only the pure helpers and starts nothing', () => {
        expect(typeof enotifUser._buildPanel).toBe('function');
        expect(enotifUser._start).toBeUndefined();
        expect(enotifUser._tick).toBeUndefined();
        expect(enotifUser._load).toBeUndefined();
        expect(enotifUser._bind).toBeUndefined();
    });
});
