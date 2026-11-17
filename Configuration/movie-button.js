(() => {
    'use strict';

    if (window.audioAdjusterMovieButton) return;
    window.audioAdjusterMovieButton = true;
    const itemIdPattern = /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i;
    let previous;
    let running = false;

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
        if (button?.dataset.item === id) return;
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

            button = document.createElement('button', { is: 'emby-button' });
            button.type = 'button';
            button.className = 'emby-button button-flat detailButton audioAdjusterButton';
            button.dataset.item = id;
            button.textContent = 'Adjust Audio';
            button.addEventListener('click', async () => {
                if (button.dataset.item !== new URLSearchParams((location.hash || location.search).split('?')[1]).get('id')) return;
                if (!window.confirm(`Adjust audio for "${item.Name}" using the saved Audio Adjuster settings?`)) return;
                button.disabled = true;
                try {
                    await client.ajax({ type: 'POST', url: client.getUrl('AudioAdjuster/movies/' + id) });
                    button.textContent = 'Audio queued';
                    window.setTimeout(() => { button.textContent = 'Adjust Audio'; }, 3000);
                } catch (error) {
                    let message = 'Unable to queue audio. Check the file format and job status in Audio Adjuster.';
                    if (error instanceof Response) {
                        const body = await error.json().catch(() => null);
                        message = body?.Message || body?.message || message;
                    }
                    window.alert(message);
                } finally {
                    button.disabled = false;
                }
            });
            controls.append(button);
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
