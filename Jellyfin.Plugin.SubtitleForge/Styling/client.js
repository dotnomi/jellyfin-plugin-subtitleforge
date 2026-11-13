/* SubtitleForge web client: enforces the server-defined subtitle style.
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
