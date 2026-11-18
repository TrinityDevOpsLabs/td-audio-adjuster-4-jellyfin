(() => {
    'use strict';

    if (window.audioAdjusterMovieButton) return;
    window.audioAdjusterMovieButton = true;
    const itemIdPattern = /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i;
    const jobStates = new WeakMap();
    let previous;
    let running = false;

    function showJobState(button, active) {
        button.disabled = active;
        button.textContent = active ? 'Audio adjustment in progress' : 'Adjust Audio';
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
            if (button.textContent === 'Checking audio adjustment…') showJobState(button, false);
            console.debug('Audio Adjuster could not check adjustment status', error);
        } finally {
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
            button.textContent = 'Checking audio adjustment…';
            button.disabled = true;
            const state = { checking: false, submitting: false, submission: 0, nextCheck: 0 };
            jobStates.set(button, state);
            button.addEventListener('click', async () => {
                if (button.disabled || state.submitting) return;
                if (button.dataset.item !== new URLSearchParams((location.hash || location.search).split('?')[1]).get('id')) return;
                if (!window.confirm(`Adjust audio for "${item.Name}" using the saved Audio Adjuster settings?`)) return;
                state.submission++;
                state.submitting = true;
                button.textContent = 'Submitting audio adjustment…';
                button.disabled = true;
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

    window.addEventListener('hashchange', () => { previous = null; });
    window.setInterval(attach, 1200);
    attach();
})();
