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


/* ---------- SubtitleForge actions for administrators: button on item pages, entry in the "⋮" menu of cards and
 * item pages, and for several selected items (multi-select). ---------- */
(function () {
    'use strict';
    var LANG = (window.__subtitleForgeStyle && window.__subtitleForgeStyle.lang) === 'de' ? 'de' : 'en';
    var TEXT = {
        en: {
            close: 'Close', title: 'Subtitles', scopeOne: 'Single title', scopeMany: '{0} movies/episodes will be scheduled',
            selected: '{0} selected titles', action: 'Action', target: 'Target language', fromAudio: 'From audio track',
            replace: 'Replace the downloaded subtitle', schedule: 'Schedule',
            actSearch: 'Search / download again', actTranslate: 'Translate an existing subtitle',
            actWhisper: 'Generate from the audio with Whisper (AI)', actManage: 'Show / delete subtitle files',
            hintSearch: 'Searches SubDL, OpenSubtitles and Jimaku and checks every match against the audio.',
            hintTranslate: 'Translates an existing subtitle of another language offline. The timing stays exact.',
            hintWhisper: 'Recognises the speech of the audio track with AI. Takes a few minutes per episode, depending on the hardware.',
            hintManage: 'Subtitles SubtitleForge made ([SF]) can be deleted and are not created again automatically. Embedded tracks and other files are never touched.',
            auto: 'automatic', track: 'Track {0}', audioLang: 'Audio: {0}',
            nothing: 'Nothing scheduled: no matching subtitle or audio track.',
            queuedOne: '1 job scheduled. Progress: Dashboard → SubtitleForge.', queuedMany: '{0} jobs scheduled. Progress: Dashboard → SubtitleForge.',
            error: 'Could not schedule.', del: 'Delete', delSure: 'Really delete?', deleted: 'deleted', delFailed: 'Could not delete.',
            noFiles: 'This title has no external subtitle files.', cues: '{0} cues', forced: 'forced', loading: 'Loading …',
            o_download: 'download', o_translation: 'translation', o_whisper: 'Whisper', o_ocr: 'OCR', o_plugin: 'SubtitleForge', o_foreign: 'not from SubtitleForge',
            de: 'German', en: 'English', ja: 'Japanese', fr: 'French', es: 'Spanish', it: 'Italian'
        },
        de: {
            close: 'Schließen', title: 'Untertitel', scopeOne: 'Einzelner Titel', scopeMany: '{0} Filme/Folgen werden eingeplant',
            selected: '{0} ausgewählte Titel', action: 'Aktion', target: 'Zielsprache', fromAudio: 'Aus Tonspur',
            replace: 'Heruntergeladenen Untertitel ersetzen', schedule: 'Einplanen',
            actSearch: 'Neu suchen / herunterladen', actTranslate: 'Aus vorhandenem Untertitel übersetzen',
            actWhisper: 'Mit Whisper (KI) aus der Tonspur erzeugen', actManage: 'Untertitel-Dateien ansehen / löschen',
            hintSearch: 'Sucht bei SubDL, OpenSubtitles und Jimaku und prüft jeden Treffer gegen die Tonspur.',
            hintTranslate: 'Übersetzt einen vorhandenen Untertitel einer anderen Sprache offline. Das Timing bleibt exakt.',
            hintWhisper: 'Erkennt die Sprache der Tonspur per KI. Dauert je nach Hardware einige Minuten pro Folge.',
            hintManage: 'Untertitel von SubtitleForge ([SF]) lassen sich löschen und werden nicht automatisch neu erstellt. Eingebettete Spuren und fremde Dateien bleiben unangetastet.',
            auto: 'automatisch', track: 'Spur {0}', audioLang: 'Tonspur {0}',
            nothing: 'Nichts eingeplant: kein passender Untertitel bzw. keine passende Tonspur vorhanden.',
            queuedOne: '1 Auftrag eingeplant. Fortschritt: Dashboard → SubtitleForge.', queuedMany: '{0} Aufträge eingeplant. Fortschritt: Dashboard → SubtitleForge.',
            error: 'Fehler beim Einplanen.', del: 'Löschen', delSure: 'Wirklich löschen?', deleted: 'gelöscht', delFailed: 'Löschen fehlgeschlagen.',
            noFiles: 'Dieser Titel hat keine externen Untertitel-Dateien.', cues: '{0} Einträge', forced: 'forced', loading: 'Wird geladen …',
            o_download: 'Download', o_translation: 'Übersetzung', o_whisper: 'Whisper', o_ocr: 'OCR', o_plugin: 'SubtitleForge', o_foreign: 'nicht von SubtitleForge',
            de: 'Deutsch', en: 'Englisch', ja: 'Japanisch', fr: 'Französisch', es: 'Spanisch', it: 'Italienisch'
        }
    };
    function t(key) {
        var v = TEXT[LANG][key];
        return v !== undefined ? v : (TEXT.en[key] !== undefined ? TEXT.en[key] : key);
    }
    function fmt(key, arg) { return t(key).replace('{0}', arg); }
    function langName(code) { return TEXT.en[code] ? t(code) : String(code).toUpperCase(); }

    var isAdmin = null;
    function api(method, path) {
        var c = window.ApiClient;
        return c.ajax({ type: method, url: c.getUrl(path), dataType: 'json' });
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

    function unique(list) { return list.filter(function (v, i, a) { return v && a.indexOf(v) === i; }); }

    // Item ids come from the page (data-id attributes, the URL): only plain Jellyfin ids reach API paths.
    function validId(id) { return typeof id === 'string' && /^[0-9a-f]{32}$/i.test(id.replace(/-/g, '')); }
    function cleanIds(list) { return unique((list || []).filter(validId)); }

    // Info for one or several items, merged (several = like a folder).
    function loadInfo(ids) {
        ids = cleanIds(ids);
        return Promise.all(ids.map(function (id) {
            return api('GET', 'SubtitleForge/Items/' + id + '/Info').then(function (i) { return i ? { id: id, info: i } : null; }, function () { return null; });
        })).then(function (list) {
            list = list.filter(function (x) { return x && x.info.videos > 0; });
            if (!list.length) {
                return null;
            }
            if (list.length === 1) {
                var one = list[0].info;
                one.ids = [list[0].id];
                return one;
            }
            return {
                ids: list.map(function (x) { return x.id; }),
                kind: 'folder',
                name: fmt('selected', list.length),
                videos: list.reduce(function (n, x) { return n + x.info.videos; }, 0),
                languages: unique([].concat.apply([], list.map(function (x) { return x.info.languages || []; }))),
                audioLanguages: unique([].concat.apply([], list.map(function (x) { return x.info.audioLanguages || []; }))),
                audioTracks: []
            };
        });
    }

    function addOption(select, value, text) {
        var o = document.createElement('option');
        o.value = value;
        o.textContent = text;
        select.appendChild(o);
    }

    // Built from Jellyfin's own dialog/form markup (formDialog, emby-select, emby-checkbox, raised button-submit),
    // so it takes the look of whatever theme is active (Dark, Light, custom CSS like Abyss).
    function openDialog(ids) {
        loadInfo(ids).then(function (info) {
            if (!info) {
                return;
            }
            var single = info.kind === 'video' && info.ids.length === 1;

            var backdrop = el('div', { 'class': 'dialogBackdrop dialogBackdropOpened' });
            var container = el('div', { 'class': 'dialogContainer' });
            var dlg = el('div', { 'class': 'focuscontainer dialog formDialog centeredDialog opened sfDialog' });
            dlg.style.cssText = 'animation:scaleup 180ms ease-out normal both;max-width:min(36em,94vw);width:36em;';
            dlg.innerHTML =
                '<div class="formDialogHeader">' +
                    '<button is="paper-icon-button-light" class="btnCancel autoSize paper-icon-button-light"><span class="material-icons arrow_back" aria-hidden="true"></span></button>' +
                    '<h3 class="formDialogHeaderTitle"></h3>' +
                '</div>' +
                '<div class="formDialogContent smoothScrollY" style="margin-bottom:0">' +
                    '<div class="dialogContentInner dialog-content-centered" style="padding-bottom:1.5em">' +
                        '<p class="secondaryText sfScope"></p>' +
                        '<div class="selectContainer"><select is="emby-select" class="sfAction"></select></div>' +
                        '<div class="selectContainer sfLangRow"><select is="emby-select" class="sfLang"></select></div>' +
                        '<div class="selectContainer sfAudioRow"><select is="emby-select" class="sfAudio"></select></div>' +
                        '<div class="checkboxContainer sfReplaceRow"><label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="sfReplace" checked /><span class="sfReplaceText"></span></label></div>' +
                        '<div class="fieldDescription sfHint" style="margin:.4em 0 .6em"></div>' +
                        '<div class="sfFiles"></div>' +
                        '<div class="fieldDescription sfStatus" style="font-weight:bold"></div>' +
                        '<div class="sfOkRow" style="margin-top:1.2em">' +
                            '<button is="emby-button" type="button" class="raised button-submit block btnSubmit sfOk"><span></span></button>' +
                        '</div>' +
                    '</div>' +
                '</div>';
            dlg.querySelector('.btnCancel').title = t('close');
            dlg.querySelector('.formDialogHeaderTitle').textContent = t('title') + ' – ' + info.name;
            dlg.querySelector('.sfScope').textContent = single ? t('scopeOne') : fmt('scopeMany', info.videos);
            dlg.querySelector('.sfAction').setAttribute('label', t('action'));
            dlg.querySelector('.sfLang').setAttribute('label', t('target'));
            dlg.querySelector('.sfAudio').setAttribute('label', t('fromAudio'));
            dlg.querySelector('.sfReplaceText').textContent = t('replace');
            dlg.querySelector('.sfOk span').textContent = t('schedule');

            var action = dlg.querySelector('.sfAction');
            addOption(action, 'search', t('actSearch'));
            addOption(action, 'translate', t('actTranslate'));
            addOption(action, 'whisper', t('actWhisper'));
            if (single) {
                addOption(action, 'manage', t('actManage'));
            }

            var lang = dlg.querySelector('.sfLang');
            unique((info.languages || []).concat(['de', 'en', 'ja', 'fr', 'es', 'it']))
                .forEach(function (l) { addOption(lang, l, langName(l)); });

            var audio = dlg.querySelector('.sfAudio');
            addOption(audio, '', t('auto'));
            if (single) {
                (info.audioTracks || []).forEach(function (a) { addOption(audio, 'i' + a.Index, a.Title || a.Language || fmt('track', a.Index + 1)); });
            } else {
                (info.audioLanguages || []).forEach(function (l) { addOption(audio, 'l' + l, fmt('audioLang', langName(l))); });
            }

            var replace = dlg.querySelector('.sfReplace');
            var hint = dlg.querySelector('.sfHint');
            var files = dlg.querySelector('.sfFiles');
            var status = dlg.querySelector('.sfStatus');
            var hints = { search: 'hintSearch', translate: 'hintTranslate', whisper: 'hintWhisper', manage: 'hintManage' };
            function update() {
                var a = action.value;
                dlg.querySelector('.sfLangRow').style.display = a === 'translate' || a === 'whisper' ? '' : 'none';
                dlg.querySelector('.sfAudioRow').style.display = a === 'whisper' ? '' : 'none';
                dlg.querySelector('.sfReplaceRow').style.display = a === 'whisper' ? '' : 'none';
                dlg.querySelector('.sfOkRow').style.display = a === 'manage' ? 'none' : '';
                files.style.display = a === 'manage' ? '' : 'none';
                hint.textContent = t(hints[a]);
                status.textContent = '';
                if (a === 'manage') {
                    loadFiles();
                }
            }

            function loadFiles() {
                files.textContent = t('loading');
                api('GET', 'SubtitleForge/Items/' + info.ids[0] + '/Subtitles').then(function (list) {
                    files.textContent = '';
                    if (!list || !list.length) {
                        files.appendChild(el('div', { 'class': 'fieldDescription' }, t('noFiles')));
                        return;
                    }
                    list.forEach(function (f) {
                        var row = el('div', { 'class': 'listItem listItem-border' });
                        row.style.cssText = 'display:flex;align-items:center;gap:.8em;padding:.5em 0;';
                        var body = el('div', { 'class': 'listItemBody' });
                        body.style.cssText = 'flex:1;min-width:0;padding:0;';
                        var name = el('div', { 'class': 'listItemBodyText' }, (f.title || f.file));
                        name.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                        var meta = [langName((f.language || '').substring(0, 2)), t('o_' + f.origin), fmt('cues', f.count)];
                        if (f.forced) { meta.push(t('forced')); }
                        body.appendChild(name);
                        body.appendChild(el('div', { 'class': 'listItemBodyText secondary' }, meta.join(' · ')));
                        row.appendChild(body);
                        if (f.canDelete) {
                            var b = el('button', { is: 'emby-button', type: 'button', 'class': 'raised emby-button' }, t('del'));
                            b.style.cssText = 'margin:0;flex-shrink:0;';
                            var armed = false;
                            b.addEventListener('click', function () {
                                if (!armed) {
                                    armed = true;
                                    b.textContent = t('delSure');
                                    b.style.background = '#c62828';
                                    b.style.color = '#fff';
                                    setTimeout(function () {
                                        if (armed && b.isConnected && !b.disabled) {
                                            armed = false; b.textContent = t('del'); b.style.background = ''; b.style.color = '';
                                        }
                                    }, 4000);
                                    return;
                                }
                                b.disabled = true;
                                api('POST', 'SubtitleForge/Items/' + info.ids[0] + '/Subtitles/Delete?file=' + encodeURIComponent(f.file)).then(function () {
                                    b.textContent = t('deleted');
                                    b.style.background = '';
                                    row.style.opacity = '.45';
                                }, function () { status.textContent = t('delFailed'); b.disabled = false; });
                            });
                            row.appendChild(b);
                        }
                        files.appendChild(row);
                    });
                }, function () { files.textContent = t('error'); });
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
            // Registered a moment later: the closing "⋮" menu may still change the history right now.
            setTimeout(function () {
                if (container.isConnected) {
                    window.addEventListener('hashchange', close);
                    document.addEventListener('viewbeforehide', close);
                }
            }, 600);
            function onKey(e) {
                if (e.key === 'Escape') { e.stopPropagation(); close(); }
            }
            document.addEventListener('keydown', onKey, true);
            dlg.querySelector('.btnCancel').addEventListener('click', close);
            container.addEventListener('click', function (e) { if (e.target === container) { close(); } });

            var ok = dlg.querySelector('.sfOk');
            ok.addEventListener('click', function () {
                function pathFor(id) {
                    if (action.value === 'search') {
                        return 'SubtitleForge/Items/' + id + '/Search';
                    }
                    if (action.value === 'translate') {
                        return 'SubtitleForge/Items/' + id + '/Translate?language=' + lang.value;
                    }
                    var p = 'SubtitleForge/Items/' + id + '/Whisper?language=' + lang.value + '&replace=' + replace.checked;
                    if (audio.value.charAt(0) === 'i') { p += '&audioIndex=' + audio.value.substring(1); }
                    if (audio.value.charAt(0) === 'l') { p += '&audioLanguage=' + audio.value.substring(1); }
                    return p;
                }
                ok.disabled = true;
                var total = 0;
                info.ids.reduce(function (chain, id) {
                    return chain.then(function () {
                        return api('POST', pathFor(id)).then(function (r) { total += r && r.queued !== undefined ? r.queued : 1; });
                    });
                }, Promise.resolve()).then(function () {
                    status.textContent = total === 0 ? t('nothing') : total === 1 ? t('queuedOne') : fmt('queuedMany', total);
                    setTimeout(close, total === 0 ? 3500 : 2200);
                }, function () { status.textContent = t('error'); ok.disabled = false; });
            });

            container.appendChild(dlg);
            document.body.appendChild(backdrop);
            document.body.appendChild(container);
            action.focus();
        });
    }

    /* Button on the item detail page. */
    function addButton(page, itemId) {
        if (!validId(itemId)) {
            return true;
        }
        var container = page.querySelector('.mainDetailButtons');
        if (!container) {
            return false;
        }
        var btn = container.querySelector('.btnSubtitleForge');
        if (!btn) {
            btn = el('button', { is: 'emby-button', type: 'button', 'class': 'button-flat btnSubtitleForge detailButton hide', title: t('title') + ' (SubtitleForge)' });
            var content = el('div', { 'class': 'detailButton-content' });
            content.appendChild(el('span', { 'class': 'material-icons detailButton-icon closed_caption', 'aria-hidden': 'true' }));
            btn.appendChild(content);
            btn.addEventListener('click', function () { openDialog([btn.getAttribute('data-id')]); });
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

    /* Entry in Jellyfin's "⋮" menu (cards, list rows, item page, multi-select). We remember which items the menu
     * was opened for, then add our entry to the action sheet when it appears. The entry is a regular action sheet
     * item, so the sheet closes normally; Jellyfin ignores the unknown command id. */
    var pendingIds = null;
    var pendingAt = 0;
    function remember(ids) {
        pendingIds = cleanIds(ids);
        pendingAt = Date.now();
    }
    function currentItemId() {
        var q = location.hash.split('?')[1] || location.search.substring(1);
        return q ? new URLSearchParams(q).get('id') : null;
    }
    function selectedIds() {
        return Array.prototype.map.call(document.querySelectorAll('.chkItemSelect:checked'), function (c) {
            var card = c.closest('[data-id]');
            return card ? card.getAttribute('data-id') : null;
        });
    }

    document.addEventListener('click', function (e) {
        var target = e.target && e.target.closest ? e.target : null;
        if (!target) {
            return;
        }
        if (target.closest('.btnSelectionPanelOptions')) {
            remember(selectedIds());
            return;
        }
        var more = target.closest('.btnMoreCommands');
        if (more && more.closest('#itemDetailPage')) {
            remember([currentItemId()]);
            return;
        }
        var opt = target.closest('.btnCardOptions, [data-action="menu"]');
        var card = opt && opt.closest('[data-id]');
        if (card) {
            remember([card.getAttribute('data-id')]);
        }
    }, true);

    document.addEventListener('contextmenu', function (e) {
        var card = e.target && e.target.closest && e.target.closest('.card[data-id], .listItem[data-id]');
        if (card) {
            remember([card.getAttribute('data-id')]);
        }
    }, true);

    function enhance(sheet, ids) {
        checkAdmin().then(function (admin) {
            if (!admin || !ids.length) {
                return;
            }
            var check = ids.length === 1
                ? api('GET', 'SubtitleForge/Items/' + ids[0] + '/Info').then(function (i) { return !!(i && i.videos > 0); }, function () { return false; })
                : Promise.resolve(true);
            check.then(function (ok) {
                if (!ok || !sheet.isConnected || sheet.querySelector('[data-id="subtitleforge"]')) {
                    return;
                }
                var items = sheet.querySelectorAll('.actionSheetMenuItem');
                if (!items.length) {
                    return;
                }
                var ref = sheet.querySelector('.actionSheetMenuItem[data-id="editsubtitles"]') || items[items.length - 1];
                var entry = ref.cloneNode(true);
                entry.setAttribute('data-id', 'subtitleforge');
                entry.classList.remove('selected');
                var icon = entry.querySelector('.actionsheetMenuItemIcon');
                if (icon) {
                    icon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons closed_caption';
                    icon.style.visibility = '';
                }
                var text = entry.querySelector('.actionSheetItemText');
                if (text) {
                    text.textContent = 'SubtitleForge';
                }
                Array.prototype.forEach.call(entry.querySelectorAll('.actionSheetItemAsideText'), function (x) { x.remove(); });
                var chosen = false;
                entry.addEventListener('click', function () { chosen = true; });
                sheet.addEventListener('close', function () {
                    if (chosen) {
                        setTimeout(function () { openDialog(ids); }, 120);
                    }
                });
                ref.parentNode.insertBefore(entry, ref.nextSibling);
            });
        });
    }

    new MutationObserver(function (mutations) {
        if (!pendingIds || Date.now() - pendingAt > 5000) {
            return;
        }
        mutations.forEach(function (m) {
            Array.prototype.forEach.call(m.addedNodes, function (n) {
                if (n.nodeType !== 1) {
                    return;
                }
                var sheet = n.classList.contains('actionSheet') ? n : n.querySelector('.actionSheet');
                if (sheet && pendingIds) {
                    var ids = pendingIds;
                    pendingIds = null;
                    enhance(sheet, ids);
                }
            });
        });
    }).observe(document.body, { childList: true });
})();
