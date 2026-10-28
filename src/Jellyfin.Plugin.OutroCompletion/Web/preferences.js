(() => {
    'use strict';

    // Derive the server root from this asset, preserving Jellyfin's optional /base-url.
    const scriptUrl = document.currentScript.src;
    const serverRoot = new URL('../../', scriptUrl);
    const element = id => document.getElementById(id);
    const status = element('status');
    let token = null; // In memory only. Never in a URL, localStorage, or sessionStorage.
    let serverSettings = null;
    let busy = false;
    const randomId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
    let deviceId;
    try {
        deviceId = sessionStorage.getItem('outro-completion-device') || randomId();
        sessionStorage.setItem('outro-completion-device', deviceId); // Not a credential.
    } catch {
        deviceId = randomId();
    }

    function authorization() {
        return `MediaBrowser Client="Outro Completion Preferences", Device="Browser", DeviceId="${deviceId}", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
    }

    function setStatus(message, error = false) {
        status.textContent = message;
        status.dataset.error = String(error);
    }

    function setBusy(value) {
        busy = value;
        document.querySelectorAll('button').forEach(button => { button.disabled = value; });
    }

    async function request(path, options = {}) {
        const response = await fetch(new URL(path, serverRoot), {
            ...options,
            credentials: 'omit',
            cache: 'no-store',
            redirect: 'error',
            headers: {
                Authorization: authorization(),
                ...(options.body ? { 'Content-Type': 'application/json' } : {})
            }
        });
        if (!response.ok) {
            if (response.status === 401 || response.status === 403) {
                throw new Error('Sign-in failed or your session expired. Sign out and try again.');
            }
            throw new Error(`The server returned ${response.status}. Ask your administrator to check the plugin logs.`);
        }
        return response.status === 204 ? null : response.json();
    }

    function showEffectiveSetting() {
        if (!serverSettings) return;
        const choice = element('require-next').value;
        const requireNext = choice === 'inherit' ? serverSettings.defaultRequireNextEpisode : choice === 'true';
        element('effective-setting').textContent = requireNext
            ? `The next episode must start within ${serverSettings.confirmationWindowSeconds} seconds. This extra check only controls this plugin, not Jellyfin’s normal completion rules.`
            : 'An episode stopped inside credits will be completed without waiting for another episode.';
    }

    function displaySettings(settings) {
        serverSettings = settings;
        element('enabled').checked = settings.enabled;
        element('require-next').value = settings.requireNextEpisode === null ? 'inherit' : String(settings.requireNextEpisode);
        element('server-disabled').hidden = settings.serverEnabled;
        showEffectiveSetting();
    }

    async function logout() {
        try {
            if (token) await request('Sessions/Logout', { method: 'POST' });
            setStatus('Signed out.');
        } catch {
            setStatus('Signed out locally. Server logout could not be confirmed.', true);
        } finally {
            token = null;
            serverSettings = null;
            element('preferences-section').hidden = true;
            element('login-section').hidden = false;
            element('signed-in-as').textContent = '';
        }
    }

    element('https-warning').hidden = location.protocol === 'https:' || ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
    element('require-next').addEventListener('change', showEffectiveSetting);
    element('login-form').addEventListener('submit', async event => {
        event.preventDefault();
        if (busy) return;
        setBusy(true);
        setStatus('Signing in…');
        try {
            const result = await request('Users/AuthenticateByName', {
                method: 'POST',
                body: JSON.stringify({ Username: element('username').value, Pw: element('password').value })
            });
            token = result.AccessToken || result.accessToken;
            if (!token) throw new Error('The server did not return a user access token.');
            const settings = await request('OutroCompletion/Me');
            displaySettings(settings);
            element('signed-in-as').textContent = `Signed in as ${(result.User || result.user)?.Name || (result.User || result.user)?.name || element('username').value}.`;
            element('login-section').hidden = true;
            element('preferences-section').hidden = false;
            setStatus('');
        } catch (error) {
            // Revoke a successful login if loading preferences failed afterward.
            if (token) await logout();
            setStatus(error.message, true);
        } finally {
            element('password').value = '';
            setBusy(false);
        }
    });

    element('preferences-form').addEventListener('submit', async event => {
        event.preventDefault();
        if (busy) return;
        setBusy(true);
        setStatus('Saving…');
        try {
            const choice = element('require-next').value;
            displaySettings(await request('OutroCompletion/Me', {
                method: 'PUT',
                body: JSON.stringify({
                    enabled: element('enabled').checked,
                    requireNextEpisode: choice === 'inherit' ? null : choice === 'true'
                })
            }));
            setStatus('Preferences saved.');
        } catch (error) {
            setStatus(error.message, true);
        } finally {
            setBusy(false);
        }
    });

    element('logout').addEventListener('click', async () => {
        if (busy) return;
        setBusy(true);
        await logout();
        setBusy(false);
    });
})();
