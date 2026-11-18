(() => {
    'use strict';

    if (window.audioAdjusterMovieButton) return;
    window.audioAdjusterMovieButton = true;
    const itemIdPattern = /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i;
    const buttonLabel = 'Adjust Audio via AudioAdjuster4Jellyfin plugin';
    const jobStates = new WeakMap();
    let previous;
    let running = false;

    function showJobState(button, active) {
        button.disabled = active;
        button.setAttribute('aria-busy', String(active));
    }

    async function refreshJobState(button, client, id) {
        const state = jobStates.get(button);
        if (state.checking || state.submitting || Date.now() < state.nextCheck) return;

        const submission = state.submission;
        state.checking = true;
        try {
            const job = await client.ajax({
                type: 'GET', url: client.getUrl('AudioAdjuster/movies/' + id + '/status'), dataType: 'json'
            });
            if (submission === state.submission) showJobState(button, job.IsActive);
        } catch (error) {
            if (state.initial) showJobState(button, false);
            console.debug('Audio Adjuster could not check adjustment status', error);
        } finally {
            state.initial = false;
            state.checking = false;
            if (submission === state.submission) state.nextCheck = Date.now() + 5000;
        }
    }

    async function attach() {
        const route = location.hash || location.pathname + location.search;
        if (!/\/details\?/.test(route) || running) return;

        const params = new URLSearchParams(route.split('?')[1]);
        const id = params.get('id');
        const controls = document.querySelector('.itemDetailPage:not(.hide) .mainDetailButtons');
        const client = window.ApiClient;
        if (!client || !controls || !itemIdPattern.test(id || '')) return;

        const serverId = params.get('serverId');
        if (serverId && client.serverId() !== serverId) return;

        let button = controls.querySelector('.audioAdjusterButton');
        if (button?.dataset.item === id) {
            void refreshJobState(button, client, id);
            return;
        }
        button?.remove();
        if (previous?.id === id && previous.controls === controls) return;

        running = true;
        try {
            const user = await client.getCurrentUser();
            if (!user.Policy?.IsAdministrator) {
                previous = { id, controls };
                return;
            }
            const item = await client.getItem(client.getCurrentUserId(), id);
            if (route !== (location.hash || location.pathname + location.search) || !controls.isConnected) return;

            if (item.Type !== 'Movie') {
                previous = { id, controls };
                return;
            }

            button = document.createElement('button');
            button.type = 'button';
            button.className = 'emby-button button-flat detailButton audioAdjusterButton';
            button.dataset.item = id;
            button.title = buttonLabel;
            button.setAttribute('aria-label', buttonLabel);

            const content = document.createElement('div');
            content.className = 'detailButton-content';
            // Google's Material Symbols Outlined edit_audio glyph. See NOTICE.
            const svgNamespace = 'http://www.w3.org/2000/svg';
            const icon = document.createElementNS(svgNamespace, 'svg');
            icon.setAttribute('class', 'detailButton-icon');
            icon.setAttribute('viewBox', '0 -960 960 960');
            icon.setAttribute('width', '1em');
            icon.setAttribute('height', '1em');
            icon.setAttribute('fill', 'currentColor');
            icon.setAttribute('aria-hidden', 'true');
            const arrows = document.createElementNS(svgNamespace, 'path');
            arrows.setAttribute('d', 'M240-80 80-240l160-160 57 56-64 64h494l-63-64 56-56 160 160L720-80l-57-56 64-64H233l63 64-56 56Z');
            icon.append(arrows);

            const bars = [
                { x: 120, y: -740, height: 120 },
                { x: 280, y: -840, height: 320 },
                { x: 440, y: -920, height: 480 },
                { x: 600, y: -840, height: 320 },
                { x: 760, y: -740, height: 120 }
            ];
            for (const { x, y, height } of bars) {
                const bar = document.createElementNS(svgNamespace, 'rect');
                bar.classList.add('audioAdjusterWaveformBar');
                bar.setAttribute('x', x);
                bar.setAttribute('y', y);
                bar.setAttribute('width', 80);
                bar.setAttribute('height', height);
                icon.append(bar);
            }
            content.append(icon);
            button.append(content);
            showJobState(button, true);
            const state = { initial: true, checking: false, submitting: false, submission: 0, nextCheck: 0 };
            jobStates.set(button, state);
            button.addEventListener('click', async () => {
                if (button.disabled || state.submitting) return;
                if (button.dataset.item !== new URLSearchParams((location.hash || location.search).split('?')[1]).get('id')) return;
                if (!window.confirm(`Adjust audio for "${item.Name}" using the saved Audio Adjuster settings?`)) return;
                state.submission++;
                state.submitting = true;
                showJobState(button, true);
                try {
                    await client.ajax({ type: 'POST', url: client.getUrl('AudioAdjuster/movies/' + id) });
                    showJobState(button, true);
                } catch (error) {
                    let message = 'Unable to queue audio. Check the file format and job status in Audio Adjuster.';
                    if (error instanceof Response) {
                        const body = await error.json().catch(() => null);
                        message = body?.Message || body?.message || message;
                    }
                    showJobState(button, false);
                    window.alert(message);
                } finally {
                    state.submitting = false;
                    state.nextCheck = 0;
                    void refreshJobState(button, client, id);
                }
            });
            controls.append(button);
            void refreshJobState(button, client, id);
        } catch (error) {
            console.debug('Audio Adjuster could not load movie details', error);
        } finally {
            running = false;
        }
    }

    const animationStyles = document.createElement('style');
    animationStyles.textContent = `
        @media (prefers-reduced-motion: no-preference) {
            .audioAdjusterButton[aria-busy="true"] .audioAdjusterWaveformBar {
                transform-box: fill-box;
                transform-origin: center;
                animation: audioAdjusterWaveformPulse 1.2s ease-in-out infinite;
            }

            .audioAdjusterWaveformBar:nth-of-type(2) { animation-delay: 100ms; }
            .audioAdjusterWaveformBar:nth-of-type(3) { animation-delay: 200ms; }
            .audioAdjusterWaveformBar:nth-of-type(4) { animation-delay: 300ms; }
            .audioAdjusterWaveformBar:nth-of-type(5) { animation-delay: 400ms; }

            @keyframes audioAdjusterWaveformPulse {
                0%, 100% { transform: scaleY(1); }
                50% { transform: scaleY(0.55); }
            }
        }
    `;
    document.head.append(animationStyles);

    window.addEventListener('hashchange', () => { previous = null; });
    window.setInterval(attach, 1200);
    attach();
})();
