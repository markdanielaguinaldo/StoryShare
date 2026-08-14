/*
 * Story Share — adds a share button to Jellyfin item detail pages.
 *
 * Jellyfin exposes no client plugin API, so this hooks the DOM: it watches for
 * the detail page's button row and appends one button. Everything else lives in
 * a self-contained overlay so it cannot collide with jellyfin-web's own styles.
 *
 * The single action is a plain link marked is="emby-linkbutton" with
 * target="_blank". That combination is what makes the Jellyfin Android app hand
 * the URL to the phone's default browser instead of trapping it in its own
 * WebView, which has no download handler. Do NOT "improve" this with
 * window.open(), preventDefault(), or a click handler — any of those stops the
 * app from delegating, and the download silently dies again.
 */
(function () {
    'use strict';

    if (window.__storyShareLoaded) {
        return;
    }
    window.__storyShareLoaded = true;

    // Used only if /StoryShare/Styles cannot be reached; the server is the source
    // of truth for both lists.
    var FALLBACK_STYLES = {
        themes: [
            { value: 0, label: 'Poster' },
            { value: 1, label: 'Full bleed' },
            { value: 2, label: 'Minimal' },
            { value: 3, label: 'Polaroid' },
            { value: 4, label: 'Vinyl' },
            { value: 5, label: 'Stack' },
            { value: 6, label: 'Ticket' },
            { value: 7, label: 'Cassette' },
            { value: 8, label: 'Review' }
        ],
        backgrounds: [],
        defaultTheme: 0,
        defaultBackground: 'auto'
    };

    var IS_IOS = /iPad|iPhone|iPod/.test(navigator.userAgent)
        || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);

    var styles = null;

    function api() {
        return window.ApiClient || (window.Emby && window.Emby.ApiClient);
    }

    function endpoint(path) {
        var client = api();
        return client.serverAddress().replace(/\/$/, '') + '/' + path.replace(/^\//, '');
    }

    function request(path, method) {
        var client = api();
        return fetch(endpoint(path), {
            method: method || 'GET',
            headers: { 'X-Emby-Token': client.accessToken() }
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('Story Share request failed (' + response.status + ')');
            }
            return response;
        });
    }

    // Jellyfin's JSON casing has changed between releases, so read either form.
    function prop(source, name) {
        if (!source) {
            return undefined;
        }
        if (source[name] !== undefined) {
            return source[name];
        }
        return source[name.charAt(0).toLowerCase() + name.slice(1)];
    }

    function loadStyles() {
        if (styles) {
            return Promise.resolve(styles);
        }

        return request('StoryShare/Styles')
            .then(function (response) { return response.json(); })
            .then(function (data) {
                styles = {
                    themes: (prop(data, 'Themes') || []).map(function (theme) {
                        return { value: prop(theme, 'Value'), label: prop(theme, 'Label') };
                    }),
                    backgrounds: (prop(data, 'Backgrounds') || []).map(function (background) {
                        return {
                            id: prop(background, 'Id'),
                            label: prop(background, 'Label'),
                            top: prop(background, 'Top'),
                            bottom: prop(background, 'Bottom')
                        };
                    }),
                    defaultTheme: prop(data, 'DefaultTheme') || 0,
                    defaultBackground: prop(data, 'DefaultBackground') || 'auto'
                };

                if (!styles.themes.length) {
                    styles.themes = FALLBACK_STYLES.themes;
                }
                return styles;
            })
            .catch(function () {
                styles = FALLBACK_STYLES;
                return styles;
            });
    }

    // ------------------------------------------------------------------ styles

    function injectStyles() {
        if (document.getElementById('storyshare-styles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'storyshare-styles';
        style.textContent = [
            '.storyshare-overlay{position:fixed;inset:0;z-index:100000;background:rgba(8,10,14,.86);',
            'backdrop-filter:blur(6px);display:flex;align-items:center;justify-content:center;padding:24px;}',
            '.storyshare-panel{position:relative;background:#16181d;color:#f4f6f8;border-radius:14px;',
            'max-width:920px;width:100%;max-height:92vh;overflow:auto;display:flex;gap:24px;padding:24px;',
            'flex-wrap:wrap;box-shadow:0 24px 60px rgba(0,0,0,.6);}',
            '.storyshare-close{position:absolute;top:8px;right:10px;background:none;border:0;color:#f4f6f8;',
            'font-size:28px;line-height:1;cursor:pointer;opacity:.6;padding:2px 10px;}',
            '.storyshare-close:hover{opacity:1;}',
            '.storyshare-preview{flex:0 0 270px;display:flex;flex-direction:column;gap:12px;align-items:center;}',
            // The stage keeps the 9:16 box reserved whatever is inside it, so the
            // dialog never reflows between the placeholder, the image and the video.
            '.storyshare-stage{position:relative;width:270px;aspect-ratio:9/16;border-radius:10px;',
            'overflow:hidden;background:#0b0d10;}',
            '.storyshare-stage img,.storyshare-stage video{position:absolute;inset:0;width:100%;',
            'height:100%;object-fit:cover;display:block;}',
            '.storyshare-stage [hidden]{display:none;}',
            '.storyshare-placeholder{position:absolute;inset:0;display:flex;flex-direction:column;',
            'align-items:center;justify-content:center;gap:16px;padding:24px;text-align:center;',
            'background:linear-gradient(160deg,#20242e 0%,#141821 55%,#0b0d10 100%);}',
            '.storyshare-placeholder p{margin:0;font-size:.84em;line-height:1.5;opacity:.72;',
            'white-space:pre-line;}',
            '.storyshare-spinner{width:34px;height:34px;border-radius:50%;flex:none;',
            'border:3px solid rgba(255,255,255,.16);border-top-color:#00a4dc;',
            'animation:storyshare-spin .9s linear infinite;}',
            '@keyframes storyshare-spin{to{transform:rotate(360deg);}}',
            '.storyshare-placeholder.is-error{background:linear-gradient(160deg,#2a1618,#140b0c);}',
            '.storyshare-placeholder.is-error .storyshare-spinner{display:none;}',
            '.storyshare-placeholder.is-error p{color:#ff9a9a;opacity:1;}',
            '@media (prefers-reduced-motion:reduce){.storyshare-spinner{animation-duration:2.4s;}}',
            '.storyshare-controls{flex:1 1 320px;min-width:280px;display:flex;flex-direction:column;gap:16px;}',
            '.storyshare-controls h2{margin:0;font-size:1.35em;padding-right:32px;}',
            '.storyshare-controls label{display:block;font-size:.82em;opacity:.75;margin-bottom:6px;',
            'text-transform:uppercase;letter-spacing:.06em;}',
            '.storyshare-controls input:not([type="color"]),.storyshare-controls select{width:100%;',
            'padding:10px 12px;border-radius:8px;border:1px solid #33373f;background:#0f1115;color:inherit;',
            'font:inherit;box-sizing:border-box;}',
            '.storyshare-swatches{display:flex;flex-wrap:wrap;gap:9px;}',
            '.storyshare-swatch{width:34px;height:34px;border-radius:50%;padding:0;cursor:pointer;',
            'position:relative;border:1px solid rgba(255,255,255,.2);background-clip:padding-box;}',
            '.storyshare-swatch.is-selected{box-shadow:0 0 0 2px #16181d,0 0 0 4px #00a4dc;}',
            '.storyshare-swatch-auto{background:conic-gradient(#e04c2b,#f2c43d,#3ddc84,#00a4dc,#8b5cf6,#e04c2b);}',
            '.storyshare-swatch-custom{overflow:hidden;}',
            '.storyshare-swatch-custom input[type="color"]{position:absolute;inset:-4px;width:auto;height:auto;',
            'opacity:0;padding:0;margin:0;border:0;cursor:pointer;}',
            '.storyshare-btn{border:0;border-radius:8px;padding:12px 18px;font:inherit;font-weight:600;',
            'cursor:pointer;background:#00a4dc;color:#fff;display:inline-block;text-decoration:none;',
            'text-align:center;line-height:normal;align-self:flex-start;}',
            '.storyshare-btn.is-disabled{opacity:.5;cursor:default;pointer-events:none;}',
            // A text button, not a control: taking the tagline has to look optional,
            // because it is.
            '.storyshare-suggest{margin-top:8px;padding:0;border:0;background:none;color:#4cc2f1;',
            'font:inherit;font-size:.84em;cursor:pointer;text-align:left;line-height:1.4;}',
            '.storyshare-suggest:hover{text-decoration:underline;}',
            '.storyshare-note{font-size:.85em;line-height:1.5;opacity:.75;margin:0;}',
            '.storyshare-status{font-size:.88em;min-height:1.2em;}',
            '.storyshare-status.error{color:#ff8080;}'
        ].join('');
        document.head.appendChild(style);
    }

    // ------------------------------------------------------------------ dialog

    function openDialog(itemId) {
        injectStyles();

        var overlay = document.createElement('div');
        overlay.className = 'storyshare-overlay';
        overlay.innerHTML = [
            '<div class="storyshare-panel" role="dialog" aria-label="Share to Story">',
            '  <button class="storyshare-close" data-role="close" type="button" aria-label="Close">&times;</button>',
            '  <div class="storyshare-preview">',
            '    <div class="storyshare-stage">',
            // Both start hidden: an <img> with no src renders as a broken-image icon
            // plus its alt text, which is what used to sit there during a build.
            '      <img alt="Story preview" data-role="preview" hidden>',
            '      <video data-role="videopreview" autoplay loop muted playsinline hidden></video>',
            '      <div class="storyshare-placeholder" data-role="placeholder" role="status" aria-live="polite">',
            '        <div class="storyshare-spinner" data-role="spinner"></div>',
            '        <p data-role="placeholdertext">Rendering your card…</p>',
            '      </div>',
            '    </div>',
            '    <p class="storyshare-note" data-role="savehint" hidden></p>',
            '  </div>',
            '  <div class="storyshare-controls">',
            '    <h2>Share to Story</h2>',
            '    <div>',
            '      <label for="storyshare-theme">Style</label>',
            '      <select id="storyshare-theme" data-role="theme"></select>',
            '    </div>',
            '    <div>',
            '      <label id="storyshare-bg-label">Background</label>',
            '      <div class="storyshare-swatches" data-role="backgrounds" role="group"',
            '           aria-labelledby="storyshare-bg-label"></div>',
            '    </div>',
            '    <div>',
            '      <label for="storyshare-format">Format</label>',
            '      <select id="storyshare-format" data-role="format">',
            '        <option value="jpg">Still image</option>',
            '        <option value="mp4">Video (for Stories)</option>',
            '      </select>',
            '    </div>',
            '    <div>',
            '      <label for="storyshare-comment">Caption on the card (optional)</label>',
            '      <input id="storyshare-comment" data-role="comment" maxlength="180" placeholder="10/10, no notes">',
            // Shown only when the item actually has a tagline, and it only ever
            // fills the box above — nothing is put on the card unless it is clicked.
            '      <button class="storyshare-suggest" data-role="tagline" type="button" hidden></button>',
            '    </div>',
            // is="emby-linkbutton" is what makes the Android app hand this to the
            // phone's browser. Keep the attribute and keep the click unhandled.
            '    <a is="emby-linkbutton" class="storyshare-btn is-disabled" data-role="golink"',
            '       target="_blank" rel="noopener noreferrer">Go to URL</a>',
            '    <div class="storyshare-status" data-role="status"></div>',
            '  </div>',
            '</div>'
        ].join('');

        document.body.appendChild(overlay);

        var el = function (role) { return overlay.querySelector('[data-role="' + role + '"]'); };
        var status = el('status');
        var background = 'auto';

        function setStatus(message, kind) {
            status.textContent = message || '';
            status.className = 'storyshare-status' + (kind ? ' ' + kind : '');
        }

        /*
         * Covers the stage while a card is being built.
         *
         * Both media elements are hidden rather than left showing: an <img> with no
         * src renders as the browser's broken-image icon plus its alt text, and once
         * one card has been made, leaving the old one up during a rebuild shows a
         * card that no longer matches the controls. A video takes 7-17s to build, so
         * either would sit there for a long time.
         */
        function showPlaceholder(message, isError) {
            var video = el('videopreview');

            video.onloadeddata = null;
            video.onerror = null;
            // Stop the previous clip decoding behind the placeholder. Guarded because
            // load() on a source-less video fires a spurious error event.
            if (video.getAttribute('src')) {
                video.removeAttribute('src');
                video.load();
            }

            el('preview').hidden = true;
            video.hidden = true;

            var placeholder = el('placeholder');
            placeholder.classList.toggle('is-error', !!isError);
            placeholder.hidden = false;
            el('placeholdertext').textContent = message;
        }

        function showMedia(isVideo) {
            el('placeholder').hidden = true;
            el('preview').hidden = isVideo;
            el('videopreview').hidden = !isVideo;
        }

        // The link is dead until its URL has been fetched.
        function setReady(ready) {
            el('golink').classList.toggle('is-disabled', !ready);
        }

        // iOS ignores both the download attribute and Content-Disposition, and just
        // shows the file. Saving takes a different gesture per type, so say which.
        function updateSaveHint(isVideo) {
            if (!IS_IOS) {
                return;
            }

            var hint = el('savehint');
            hint.textContent = isVideo
                ? 'On iPhone: open the link, then use the share button and “Save Video”.'
                : 'On iPhone: press and hold the preview above, then choose “Save to Photos”.';
            hint.hidden = false;
        }

        function query() {
            var params = new URLSearchParams();
            params.set('theme', el('theme').value);
            params.set('format', el('format').value);
            params.set('background', background);
            var comment = el('comment').value.trim();
            if (comment) {
                params.set('comment', comment);
            }
            return params.toString();
        }

        function loadPreview() {
            setReady(false);
            setStatus('');

            var isVideo = el('format').value === 'mp4';
            // The server draws every frame and runs two ffmpeg passes, so a video is
            // nowhere near instant — say so rather than leaving a blank box.
            showPlaceholder(
                isVideo
                    ? 'Building the video…\nThis renders every frame, so give it a few seconds.'
                    : 'Rendering your card…',
                false);
            updateSaveHint(isVideo);

            return request('StoryShare/Items/' + itemId + '/ShareLink?' + query(), 'POST')
                .then(function (response) { return response.json(); })
                .then(function (data) {
                    var url = prop(data, 'Url');
                    var downloadUrl = prop(data, 'DownloadUrl');
                    if (!url || !downloadUrl) {
                        throw new Error('Server did not return a card URL.');
                    }

                    var img = el('preview');
                    var video = el('videopreview');

                    // Stay on the placeholder until the media has actually decoded —
                    // an unhidden but still-loading <video> is just a black rectangle.
                    return new Promise(function (resolve, reject) {
                        if (isVideo) {
                            video.onloadeddata = function () { resolve(); };
                            video.onerror = function () { reject(new Error('Could not build the video.')); };
                            video.src = url;
                            video.load();
                        } else {
                            img.onload = function () { resolve(); };
                            img.onerror = function () { reject(new Error('Could not render the card.')); };
                            // A plain signed URL, not a blob: this is what makes
                            // long-press → Save to Photos work on iOS.
                            img.src = url;
                        }
                    }).then(function () {
                        el('golink').href = downloadUrl;
                        showMedia(isVideo);
                        setReady(true);
                    });
                })
                .catch(function (error) { showPlaceholder(error.message, true); });
        }

        // Clicking along a row of swatches would otherwise fire a full render — and
        // for video, a full ffmpeg run — per colour.
        var pending = null;
        function reload() {
            clearTimeout(pending);
            pending = setTimeout(loadPreview, 220);
        }

        function buildSwatches(list, initial) {
            var container = el('backgrounds');
            var buttons = [];

            background = initial || 'auto';

            function highlight(element) {
                buttons.forEach(function (other) { other.classList.remove('is-selected'); });
                element.classList.add('is-selected');
            }

            function select(value, element) {
                background = value;
                highlight(element);
                reload();
            }

            function addSwatch(label, css, extraClass) {
                var button = document.createElement('button');
                button.type = 'button';
                button.className = 'storyshare-swatch' + (extraClass ? ' ' + extraClass : '');
                button.title = label;
                button.setAttribute('aria-label', label);
                if (css) {
                    button.style.background = css;
                }
                container.appendChild(button);
                buttons.push(button);
                return button;
            }

            var auto = addSwatch('Match the artwork', '', 'storyshare-swatch-auto');
            auto.addEventListener('click', function () { select('auto', auto); });

            var selected = null;
            list.forEach(function (item) {
                var swatch = addSwatch(item.label, 'linear-gradient(155deg,' + item.top + ',' + item.bottom + ')');
                swatch.addEventListener('click', function () { select(item.id, swatch); });
                if (item.id === background) {
                    selected = swatch;
                }
            });

            // Native picker, so any colour at all is reachable. Its value is sent as
            // a plain hex, which the server accepts anywhere a preset id works.
            var custom = addSwatch('Custom colour…', '#3a3f4a', 'storyshare-swatch-custom');
            var picker = document.createElement('input');
            picker.type = 'color';
            picker.value = '#2e1a47';
            picker.setAttribute('aria-label', 'Custom background colour');
            custom.appendChild(picker);

            // The transparent picker covers the whole button, so clicking anywhere on
            // it opens the native dialog; only a committed colour changes the card.
            picker.addEventListener('input', function () { custom.style.background = picker.value; });
            picker.addEventListener('change', function () {
                custom.style.background = picker.value;
                select(picker.value, custom);
            });

            if (background === 'custom') {
                selected = custom;
            }

            highlight(selected || auto);
        }

        function close() {
            overlay.remove();
        }

        el('close').addEventListener('click', close);
        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                close();
            }
        });
        document.addEventListener('keydown', function onKey(event) {
            if (event.key === 'Escape' && document.body.contains(overlay)) {
                close();
                document.removeEventListener('keydown', onKey);
            }
        });

        // No refresh button, so the card reloads whenever an input changes.
        el('theme').addEventListener('change', reload);
        el('format').addEventListener('change', reload);
        el('comment').addEventListener('change', reload);

        /*
         * Offers the item's tagline as a caption. Opt-in on purpose: some items have
         * a marketing line nobody wants on their story, so it is shown as an offer
         * with the text visible, and clicking it just types into the caption box —
         * where it can be edited or deleted like anything else typed there.
         */
        function offerTagline() {
            return request('StoryShare/Items/' + itemId + '/Caption')
                .then(function (response) { return response.json(); })
                .then(function (data) {
                    var tagline = (prop(data, 'Tagline') || '').trim();
                    if (!tagline) {
                        return;
                    }

                    var button = el('tagline');
                    var shown = tagline.length > 60 ? tagline.slice(0, 59).trim() + '…' : tagline;
                    button.textContent = 'Use the tagline: “' + shown + '”';
                    button.title = tagline;
                    button.hidden = false;

                    button.addEventListener('click', function () {
                        el('comment').value = tagline;
                        reload();
                    });
                })
                // No tagline, no endpoint, no network: the caption box works either way.
                .catch(function () { });
        }

        loadStyles().then(function (loaded) {
            var themeSelect = el('theme');
            loaded.themes.forEach(function (theme) {
                var option = document.createElement('option');
                option.value = theme.value;
                option.textContent = theme.label;
                themeSelect.appendChild(option);
            });
            themeSelect.value = loaded.defaultTheme;

            buildSwatches(loaded.backgrounds, loaded.defaultBackground);
            loadPreview();

            // After the style list, so the button cannot exist to be clicked while
            // the style dropdown is still empty and a render would have no theme.
            offerTagline();
        });
    }

    // ------------------------------------------------------------------ button

    function buttonMarkup() {
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'button-flat btn-storyshare detailButton emby-button';
        button.title = 'Share to Story';
        // Icon only, to match the rest of the row. The icon is aria-hidden, so
        // with the text label gone the button needs its own accessible name —
        // title alone is not read reliably by screen readers.
        button.setAttribute('aria-label', 'Share to Story');
        button.setAttribute('data-storyshare-button', '');
        button.innerHTML = [
            '<div class="detailButton-content">',
            '<span class="material-icons detailButton-icon" aria-hidden="true">ios_share</span>',
            '</div>'
        ].join('');
        return button;
    }

    // Only an item detail route has an item to share. Matching "id=" anywhere in
    // the hash also fired on other views that happen to carry an id.
    var DETAIL_ROUTE = /#!?\/?(details|itemdetails)/i;

    function currentItemId() {
        var hash = window.location.hash || '';
        if (!DETAIL_ROUTE.test(hash)) {
            return null;
        }

        var match = /[?&]id=([a-f0-9-]{32,36})/i.exec(hash);
        return match ? match[1] : null;
    }

    /*
     * While jellyfin-web is fetching an item it shows a spinner over a detail page
     * that is either empty or still holding the *previous* item — so a button added
     * during that window renders on the loading screen and, worse, points at the
     * wrong item. Three signals have to agree before the button goes in: the route
     * is a detail route, no spinner is up, and the page has already drawn its own
     * buttons into the row.
     */
    // Deliberately strict. jellyfin-web leaves the spinner element in the DOM and
    // only toggles a class on it, so matching on presence alone would suppress the
    // button forever. This blocks only on a spinner that is both marked active and
    // actually laid out; if the markup ever changes, the two checks below still
    // carry the fix on their own.
    function isLoading() {
        var spinner = document.querySelector('.docspinner.mdlSpinnerActive, #loadingIndicator.mdlSpinnerActive');
        return !!spinner && spinner.offsetParent !== null;
    }

    function isVisiblePage(container) {
        if (!container.offsetParent) {
            return false;
        }

        var page = container.closest ? container.closest('.page') : null;
        return !page || !page.classList.contains('hide');
    }

    function hasRenderedContent(container) {
        for (var i = 0; i < container.children.length; i++) {
            if (!container.children[i].hasAttribute('data-storyshare-button')) {
                return true;
            }
        }
        return false;
    }

    function dropStaleButtons(itemId) {
        var existing = document.querySelectorAll('[data-storyshare-button]');
        for (var i = 0; i < existing.length; i++) {
            if (!itemId || existing[i].getAttribute('data-storyshare-item') !== itemId) {
                existing[i].remove();
            }
        }
    }

    function attach() {
        var itemId = currentItemId();

        // Navigated away, or on to a different item: the old button is no longer ours.
        dropStaleButtons(itemId);

        if (!itemId || isLoading()) {
            return;
        }

        var containers = document.querySelectorAll('.mainDetailButtons');
        for (var i = 0; i < containers.length; i++) {
            var container = containers[i];
            if (!isVisiblePage(container)
                || !hasRenderedContent(container)
                || container.querySelector('[data-storyshare-button]')) {
                continue;
            }

            var button = buttonMarkup();
            button.setAttribute('data-storyshare-item', itemId);
            (function (id) {
                button.addEventListener('click', function (event) {
                    event.preventDefault();
                    event.stopPropagation();
                    openDialog(id);
                });
            })(itemId);

            container.appendChild(button);
        }
    }

    // The detail page is re-rendered on navigation, so keep checking cheaply.
    setInterval(attach, 700);
    document.addEventListener('DOMContentLoaded', attach);
    attach();
})();
