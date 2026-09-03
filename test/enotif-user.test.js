import { describe, it, expect } from 'vitest';
import enotifUser from '../src/Jellyfin.Plugin.EasyNotif/Web/enotif-user.js';

const { _isPrefsMenu, _pickLocale, _t, _escHtml, _validEmail, _buildPanel, PLUGIN_ID } = enotifUser;

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

    it('handles null data (no email, opted out)', () => {
        const panel = _buildPanel('fr', null);

        expect(panel.querySelector('.enotif-user-email').value).toBe('');
        expect(panel.querySelector('.enotif-cat[data-cat="news"]').checked).toBe(false);
        expect(panel.textContent).toContain('Notifications par email');
    });

    it('treats a hostile stored address as data, never markup', () => {
        const panel = _buildPanel('en', { email: '<img src=x onerror=alert(1)>' });

        expect(panel.querySelector('.enotif-user-email').value).toBe('<img src=x onerror=alert(1)>');
        expect(panel.innerHTML).not.toContain('<img');
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
