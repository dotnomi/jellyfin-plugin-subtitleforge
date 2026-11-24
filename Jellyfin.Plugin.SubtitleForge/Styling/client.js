/* SubtitleForge web client: (1) enforces the server-defined subtitle style,
 * (2) adds an "Untertitel" button for administrators on the detail page of movies, episodes, seasons and series.
 *
 * Style:
 * Jellyfin web applies the per-device appearance settings as inline styles on
 * .videoSubtitles / .videoSubtitlesInner and as `.htmlvideoplayer::cue` rules.
 * Rules marked !important here beat those (inline styles are not !important). */
(function () {
    'use strict';
    var s = window.__subtitleForgeStyle;
    if (!s || !s.enabled) {
        return;
    }

    function hexToRgba(hex, alpha) {
        var m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex || '');
        if (!m) {
            return 'rgba(0,0,0,' + alpha + ')';
        }
        return 'rgba(' + parseInt(m[1], 16) + ',' + parseInt(m[2], 16) + ',' + parseInt(m[3], 16) + ',' + alpha + ')';
    }

    function textShadow() {
        var parts = [];
        var w = Number(s.outlineWidthPx) || 0;
        if (w > 0) {
            // 16-direction ring approximates a real outline and also works inside ::cue.
            for (var i = 0; i < 16; i++) {
                var a = (i / 16) * Math.PI * 2;
                parts.push((Math.cos(a) * w).toFixed(2) + 'px ' + (Math.sin(a) * w).toFixed(2) + 'px 0 ' + s.outlineColor);
            }
        }
        if (s.dropShadow) {
            parts.push('0 0 ' + Math.max(4, w * 3) + 'px rgba(0,0,0,0.9)');
        }
        return parts.length ? parts.join(', ') : 'none';
    }

    var bgAlpha = Math.max(0, Math.min(1, Number(s.backgroundOpacity) || 0));
    var bg = bgAlpha > 0 ? hexToRgba(s.backgroundColor, bgAlpha) : 'transparent';
    var text = [
        'font-family:' + s.fontFamily + '!important',
        'font-size:' + s.fontSizeVh + 'vh!important',
        'font-weight:' + s.fontWeight + '!important',
        'font-variant:normal!important',
        'color:' + s.textColor + '!important',
        'text-shadow:' + textShadow() + '!important',
        'background-color:' + bg + '!important',
        'line-height:1.25!important'
    ];

    var css =
        '.videoSubtitles{padding-bottom:' + s.bottomOffsetPercent + 'vh!important;}' +
        '.videoSubtitlesInner,.videoSecondarySubtitlesInner{' + text.join(';') + ';' +
        'margin-top:0!important;margin-bottom:0!important;' +
        (bgAlpha > 0 ? 'padding:0.1em 0.4em!important;border-radius:0.2em!important;' : '') +
        '-webkit-box-decoration-break:clone;box-decoration-break:clone;}' +
        'video.htmlvideoplayer::cue{' + text.join(';') + ';}';

    // Chromium/WebKit ignore cue.line for these tracks, but allow moving the whole native cue container.
    var isWebkit = /AppleWebKit/.test(navigator.userAgent);
    if (isWebkit) {
        css += 'video.htmlvideoplayer::-webkit-media-text-track-container{transform:translateY(-' +
            s.bottomOffsetPercent + 'vh)!important;}';
    }

    var style = document.createElement('style');
    style.id = 'subtitleforge-style';
    style.textContent = css;
    (document.head || document.documentElement).appendChild(style);

    // Firefox: native cues cannot be moved via CSS. Jellyfin creates the cues and calls
    // addCue(), so position every subtitle cue right before it is added (before it is ever rendered).
    var targetLine = Math.max(0, Math.min(100, 100 - (Number(s.bottomOffsetPercent) || 0)));
    if (!isWebkit && window.TextTrack && TextTrack.prototype.addCue && !TextTrack.prototype.__sfPatched) {
        var originalAddCue = TextTrack.prototype.addCue;
        TextTrack.prototype.addCue = function (cue) {
            try {
                if ((this.kind === 'subtitles' || this.kind === 'captions') && cue && typeof cue.snapToLines !== 'undefined') {
                    cue.snapToLines = false;
                    cue.line = targetLine;
                    cue.lineAlign = 'end';
                }
            } catch (e) {
                // never break playback because of styling
            }
            return originalAddCue.apply(this, arguments);
        };
        TextTrack.prototype.__sfPatched = true;
    }

    console.info('SubtitleForge: subtitle style enforced');
})();

/* ---------- "Untertitel" button on item detail pages (administrators only) ---------- */
(function () {
    'use strict';
    var isAdmin = null;

    function api(method, path) {
        var c = window.ApiClient;
        return c.ajax({ type: method, url: c.getUrl(path), dataType: method === 'GET' ? 'json' : 'json' });
    }

    function checkAdmin() {
        if (isAdmin !== null) {
            return Promise.resolve(isAdmin);
        }
        if (!window.ApiClient || !window.ApiClient.getCurrentUser) {
            return Promise.resolve(false);
        }
        return window.ApiClient.getCurrentUser().then(function (u) {
            isAdmin = !!(u && u.Policy && u.Policy.IsAdministrator);
            return isAdmin;
        }, function () { return false; });
    }

    function el(tag, attrs, text) {
        var e = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) { e.setAttribute(k, attrs[k]); });
        if (text !== undefined) { e.textContent = text; }
        return e;
    }

    var LANGS = { de: 'Deutsch', en: 'Englisch', ja: 'Japanisch', fr: 'Französisch', es: 'Spanisch', it: 'Italienisch' };

    function openDialog(itemId) {
        api('GET', 'SubtitleForge/Items/' + itemId + '/Info').then(function (info) {
            if (!info || !info.videos) {
                return;
            }
            var overlay = el('div');
            overlay.style.cssText = 'position:fixed;inset:0;z-index:99999;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;';
            var box = el('div');
            box.style.cssText = 'background:#202020;color:#eee;padding:1.4em 1.6em;border-radius:.5em;min-width:min(26em,92vw);max-width:92vw;font-size:.95em;box-shadow:0 0 2em rgba(0,0,0,.6);';
            box.appendChild(el('h3', { style: 'margin-top:0' }, 'Untertitel – ' + info.name));
            box.appendChild(el('div', { style: 'opacity:.75;margin-bottom:1em' }, info.kind === 'video' ? 'Einzelner Titel' : info.videos + ' Filme/Folgen werden eingeplant'));

            function row(label, control) {
                var r = el('label', { style: 'display:flex;gap:.8em;align-items:center;margin:.5em 0;justify-content:space-between' });
                r.appendChild(el('span', {}, label));
                r.appendChild(control);
                box.appendChild(r);
                return r;
            }
            var selectCss = 'background:#333;color:#eee;border:1px solid #555;padding:.3em;border-radius:.3em;min-width:12em';
            var action = el('select', { style: selectCss });
            action.appendChild(el('option', { value: 'search' }, 'Neu suchen / herunterladen'));
            action.appendChild(el('option', { value: 'translate' }, 'Aus vorhandenem Untertitel übersetzen'));
            action.appendChild(el('option', { value: 'whisper' }, 'Mit Whisper (KI) erzeugen'));
            row('Aktion', action);

            var lang = el('select', { style: selectCss });
            var langs = (info.languages || []).concat(Object.keys(LANGS)).filter(function (v, i, a) { return a.indexOf(v) === i; });
            langs.forEach(function (l) { lang.appendChild(el('option', { value: l }, (LANGS[l] || l.toUpperCase()))); });
            var langRow = row('Zielsprache', lang);

            var audio = el('select', { style: selectCss });
            audio.appendChild(el('option', { value: '' }, 'automatisch'));
            if (info.kind === 'video') {
                (info.audioTracks || []).forEach(function (a) { audio.appendChild(el('option', { value: 'i' + a.Index }, a.Title || a.Language || ('Spur ' + (a.Index + 1)))); });
            } else {
                (info.audioLanguages || []).forEach(function (l) { audio.appendChild(el('option', { value: 'l' + l }, 'Tonspur ' + (LANGS[l] || l.toUpperCase()))); });
            }
            var audioRow = row('Aus Tonspur', audio);

            var replace = el('input', { type: 'checkbox' });
            replace.checked = true;
            var replaceRow = row('Heruntergeladenen Untertitel ersetzen', replace);

            function update() {
                var w = action.value === 'whisper';
                langRow.style.display = action.value === 'search' ? 'none' : 'flex';
                audioRow.style.display = replaceRow.style.display = w ? 'flex' : 'none';
            }
            action.addEventListener('change', update);
            update();

            var buttons = el('div', { style: 'display:flex;gap:.6em;justify-content:flex-end;margin-top:1.2em' });
            var cancel = el('button', { type: 'button', style: 'padding:.5em 1em;background:#444;color:#eee;border:0;border-radius:.3em;cursor:pointer' }, 'Abbrechen');
            var ok = el('button', { type: 'button', style: 'padding:.5em 1em;background:#00a4dc;color:#fff;border:0;border-radius:.3em;cursor:pointer' }, 'Einplanen');
            buttons.appendChild(cancel);
            buttons.appendChild(ok);
            box.appendChild(buttons);
            var status = el('div', { style: 'margin-top:.8em;opacity:.85' });
            box.appendChild(status);

            function close() { overlay.remove(); }
            cancel.addEventListener('click', close);
            overlay.addEventListener('click', function (e) { if (e.target === overlay) { close(); } });
            ok.addEventListener('click', function () {
                var path;
                if (action.value === 'search') {
                    path = 'SubtitleForge/Items/' + itemId + '/Search';
                } else if (action.value === 'translate') {
                    path = 'SubtitleForge/Items/' + itemId + '/Translate?language=' + lang.value;
                } else {
                    path = 'SubtitleForge/Items/' + itemId + '/Whisper?language=' + lang.value + '&replace=' + replace.checked;
                    if (audio.value.charAt(0) === 'i') { path += '&audioIndex=' + audio.value.substring(1); }
                    if (audio.value.charAt(0) === 'l') { path += '&audioLanguage=' + audio.value.substring(1); }
                }
                ok.disabled = true;
                api('POST', path).then(function (r) {
                    status.textContent = (r && r.queued !== undefined ? r.queued : 1) + ' Auftrag/Aufträge eingeplant. Fortschritt: Dashboard → Plugins → SubtitleForge.';
                    setTimeout(close, 2500);
                }, function () { status.textContent = 'Fehler beim Einplanen.'; ok.disabled = false; });
            });

            overlay.appendChild(box);
            document.body.appendChild(overlay);
        });
    }

    function addButton(page, itemId) {
        var container = page.querySelector('.mainDetailButtons');
        if (!container) {
            return false;
        }
        var btn = container.querySelector('.btnSubtitleForge');
        if (!btn) {
            btn = el('button', { is: 'emby-button', type: 'button', 'class': 'button-flat btnSubtitleForge detailButton hide', title: 'Untertitel (SubtitleForge)' });
            var content = el('div', { 'class': 'detailButton-content' });
            content.appendChild(el('span', { 'class': 'material-icons detailButton-icon closed_caption', 'aria-hidden': 'true' }));
            btn.appendChild(content);
            btn.addEventListener('click', function () { openDialog(btn.getAttribute('data-id')); });
            var more = container.querySelector('.btnMoreCommands');
            container.insertBefore(btn, more || null);
        }
        btn.setAttribute('data-id', itemId);
        btn.classList.add('hide');
        api('GET', 'SubtitleForge/Items/' + itemId + '/Info').then(function (info) {
            if (info && info.videos > 0 && btn.getAttribute('data-id') === itemId) {
                btn.classList.remove('hide');
            }
        }, function () { });
        return true;
    }

    document.addEventListener('viewshow', function (e) {
        var page = e.target;
        var params = e.detail && e.detail.params;
        if (!page || page.id !== 'itemDetailPage' || !params || !params.id) {
            return;
        }
        checkAdmin().then(function (admin) {
            if (!admin) {
                return;
            }
            var tries = 0;
            (function attempt() {
                if (!addButton(page, params.id) && tries++ < 20) {
                    setTimeout(attempt, 250);
                }
            })();
        });
    });
})();
