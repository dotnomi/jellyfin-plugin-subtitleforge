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

    // Built from Jellyfin's own dialog/form markup (formDialog, emby-select, emby-checkbox, raised button-submit),
    // so it takes the look of whatever theme is active (Dark, Light, custom CSS like Abyss).
    function openDialog(itemId) {
        api('GET', 'SubtitleForge/Items/' + itemId + '/Info').then(function (info) {
            if (!info || !info.videos) {
                return;
            }

            var backdrop = el('div', { 'class': 'dialogBackdrop dialogBackdropOpened' });
            var container = el('div', { 'class': 'dialogContainer' });
            var dlg = el('div', { 'class': 'focuscontainer dialog formDialog centeredDialog opened sfDialog' });
            dlg.style.cssText = 'animation:scaleup 180ms ease-out normal both;max-width:min(32em,94vw);width:32em;';
            dlg.innerHTML =
                '<div class="formDialogHeader">' +
                    '<button is="paper-icon-button-light" class="btnCancel autoSize paper-icon-button-light" title="Schließen"><span class="material-icons arrow_back" aria-hidden="true"></span></button>' +
                    '<h3 class="formDialogHeaderTitle"></h3>' +
                '</div>' +
                '<div class="formDialogContent smoothScrollY" style="margin-bottom:0">' +
                    '<div class="dialogContentInner dialog-content-centered" style="padding-bottom:1.5em">' +
                        '<p class="secondaryText sfScope"></p>' +
                        '<div class="selectContainer"><select is="emby-select" class="sfAction" label="Aktion"></select></div>' +
                        '<div class="selectContainer sfLangRow"><select is="emby-select" class="sfLang" label="Zielsprache"></select></div>' +
                        '<div class="selectContainer sfAudioRow"><select is="emby-select" class="sfAudio" label="Aus Tonspur"></select></div>' +
                        '<div class="checkboxContainer sfReplaceRow"><label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="sfReplace" checked /><span>Heruntergeladenen Untertitel ersetzen</span></label></div>' +
                        '<div class="fieldDescription sfHint" style="margin:.4em 0 .6em"></div>' +
                        '<div class="fieldDescription sfStatus" style="font-weight:bold"></div>' +
                        '<div style="margin-top:1.2em">' +
                            '<button is="emby-button" type="button" class="raised button-submit block btnSubmit sfOk"><span>Einplanen</span></button>' +
                        '</div>' +
                    '</div>' +
                '</div>';
            dlg.querySelector('.formDialogHeaderTitle').textContent = 'Untertitel – ' + info.name;
            dlg.querySelector('.sfScope').textContent = info.kind === 'video' ? 'Einzelner Titel' : info.videos + ' Filme/Folgen werden eingeplant';

            function addOption(select, value, text) {
                var o = document.createElement('option');
                o.value = value;
                o.textContent = text;
                select.appendChild(o);
            }

            var action = dlg.querySelector('.sfAction');
            addOption(action, 'search', 'Neu suchen / herunterladen');
            addOption(action, 'translate', 'Aus vorhandenem Untertitel übersetzen');
            addOption(action, 'whisper', 'Mit Whisper (KI) aus der Tonspur erzeugen');

            var lang = dlg.querySelector('.sfLang');
            (info.languages || []).concat(Object.keys(LANGS)).filter(function (v, i, a) { return a.indexOf(v) === i; })
                .forEach(function (l) { addOption(lang, l, LANGS[l] || l.toUpperCase()); });

            var audio = dlg.querySelector('.sfAudio');
            addOption(audio, '', 'automatisch');
            if (info.kind === 'video') {
                (info.audioTracks || []).forEach(function (a) { addOption(audio, 'i' + a.Index, a.Title || a.Language || ('Spur ' + (a.Index + 1))); });
            } else {
                (info.audioLanguages || []).forEach(function (l) { addOption(audio, 'l' + l, 'Tonspur ' + (LANGS[l] || l.toUpperCase())); });
            }

            var replace = dlg.querySelector('.sfReplace');
            var hint = dlg.querySelector('.sfHint');
            var hints = {
                search: 'Sucht bei SubDL, OpenSubtitles und Jimaku und prüft Treffer gegen die Tonspur.',
                translate: 'Übersetzt einen vorhandenen Untertitel einer anderen Sprache offline – Timing bleibt exakt.',
                whisper: 'Erkennt die Sprache der Tonspur per KI. Dauert je nach Hardware einige Minuten pro Folge.'
            };
            function update() {
                var w = action.value === 'whisper';
                dlg.querySelector('.sfLangRow').style.display = action.value === 'search' ? 'none' : '';
                dlg.querySelector('.sfAudioRow').style.display = w ? '' : 'none';
                dlg.querySelector('.sfReplaceRow').style.display = w ? '' : 'none';
                hint.textContent = hints[action.value];
            }
            action.addEventListener('change', update);
            update();

            function close() {
                container.remove();
                backdrop.remove();
                document.removeEventListener('keydown', onKey, true);
                window.removeEventListener('hashchange', close);
                document.removeEventListener('viewbeforehide', close);
            }
            window.addEventListener('hashchange', close);
            document.addEventListener('viewbeforehide', close);
            function onKey(e) {
                if (e.key === 'Escape') { e.stopPropagation(); close(); }
            }
            document.addEventListener('keydown', onKey, true);
            dlg.querySelector('.btnCancel').addEventListener('click', close);
            container.addEventListener('click', function (e) { if (e.target === container) { close(); } });

            var ok = dlg.querySelector('.sfOk');
            var status = dlg.querySelector('.sfStatus');
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
                    var n = r && r.queued !== undefined ? r.queued : 1;
                    status.textContent = n === 0
                        ? 'Nichts eingeplant – kein passender Untertitel bzw. keine passende Tonspur vorhanden.'
                        : n + (n === 1 ? ' Auftrag' : ' Aufträge') + ' eingeplant. Fortschritt: Dashboard → SubtitleForge.';
                    setTimeout(close, n === 0 ? 3500 : 2200);
                }, function () { status.textContent = 'Fehler beim Einplanen.'; ok.disabled = false; });
            });

            container.appendChild(dlg);
            document.body.appendChild(backdrop);
            document.body.appendChild(container);
            action.focus();
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
