import { describe, it, expect } from 'vitest';
import enotifConfig from '../src/Jellyfin.Plugin.EasyNotif/Web/config.js';

const { _escHtml, _pickLang, _t, _secretHint, _buildPrefRow, PLUGIN_ID } = enotifConfig;

describe('_escHtml', () => {
    it('escapes HTML special characters', () => {
        expect(_escHtml('<b>Tom & "Jerry"</b>')).toBe('&lt;b&gt;Tom &amp; &quot;Jerry&quot;&lt;/b&gt;');
    });

    it('returns an empty string for nullish input', () => {
        expect(_escHtml(null)).toBe('');
        expect(_escHtml(undefined)).toBe('');
    });
});

describe('_pickLang', () => {
    it('picks fr when the locale starts with fr', () => {
        expect(_pickLang('fr')).toBe('fr');
        expect(_pickLang('fr-FR')).toBe('fr');
        expect(_pickLang('FR-ca')).toBe('fr');
    });

    it('defaults to en for anything else', () => {
        expect(_pickLang('en-US')).toBe('en');
        expect(_pickLang('de')).toBe('en');
        expect(_pickLang('')).toBe('en');
        expect(_pickLang(undefined)).toBe('en');
    });
});

describe('_t', () => {
    it('returns the translation for a known key', () => {
        expect(_t({ 'tab.settings': 'Réglages' }, 'tab.settings')).toBe('Réglages');
    });

    it('falls back to the key itself when missing', () => {
        expect(_t({}, 'tab.settings')).toBe('tab.settings');
        expect(_t(null, 'x')).toBe('x');
    });
});

describe('_secretHint', () => {
    it('uses the "set" string when a secret is stored, and never contains a value', () => {
        const dict = { 'settings.secretSet': 'A value is saved.', 'settings.secretUnset': 'None yet.' };
        expect(_secretHint(dict, true)).toBe('A value is saved.');
        expect(_secretHint(dict, false)).toBe('None yet.');
        expect(_secretHint(dict, true)).not.toMatch(/re_|whsec_/);
    });
});

describe('_buildPrefRow', () => {
    const dict = { 'pref.noEmail': 'no contact address' };

    it('renders the masked email and two checkboxes reflecting the opt-in state', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-1',
            userName: 'Alice',
            maskedEmail: 'a***e@example.org',
            hasEmail: true,
            news: true,
            recap: false,
            updatedAt: '2026-09-04T10:00:00Z'
        });

        expect(html).toContain('<td>Alice</td>');
        expect(html).toContain('a***e@example.org');
        expect(html).toContain('data-user="u-1" data-cat="news" checked');
        expect(html).toContain('data-user="u-1" data-cat="recap" />');
        expect(html).toContain('<td>2026-09-04</td>');
    });

    it('shows the no-address warning and no date when the user has no email', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-2',
            userName: 'Bob',
            maskedEmail: '(none)',
            hasEmail: false,
            news: false,
            recap: false,
            updatedAt: null
        });

        expect(html).toContain('enotif-warn');
        expect(html).toContain('no contact address');
        expect(html).toContain('<td>-</td>');
    });

    it('escapes a user name that contains markup', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-3',
            userName: '<script>alert(1)</script>',
            maskedEmail: '(none)',
            hasEmail: false,
            news: false,
            recap: false,
            updatedAt: null
        });

        expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
        expect(html).not.toContain('<script>');
    });
});

describe('PLUGIN_ID', () => {
    it('is the plugin GUID', () => {
        expect(PLUGIN_ID).toBe('7a27339e-e774-4969-9755-8cd213dcc5e7');
    });
});

describe('module import', () => {
    it('exports only the pure helpers and starts nothing', () => {
        expect(typeof enotifConfig._buildPrefRow).toBe('function');
        expect(enotifConfig._bind).toBeUndefined();
        expect(enotifConfig._loadSettings).toBeUndefined();
        expect(enotifConfig._saveSettings).toBeUndefined();
        expect(enotifConfig._onShow).toBeUndefined();
    });
});
