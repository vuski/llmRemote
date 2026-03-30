(function () {
    const loginView = document.getElementById('login-view');
    const listView = document.getElementById('list-view');
    const viewerView = document.getElementById('viewer-view');
    const loginBtn = document.getElementById('login-btn');
    const loginError = document.getElementById('login-error');
    const refreshBtn = document.getElementById('refresh-btn');
    const windowList = document.getElementById('window-list');
    const backBtn = document.getElementById('back-btn');
    const windowTitle = document.getElementById('window-title');
    const canvas = document.getElementById('screen');
    const ctx = canvas.getContext('2d');
    const container = document.getElementById('canvas-container');
    const textInput = document.getElementById('text-input');
    const sendBtn = document.getElementById('send-btn');
    const inputBar = document.getElementById('input-bar');

    let ws = null;
    let currentHwnd = null;
    let imageWidth = 0;
    let imageHeight = 0;

    // --- Pinch zoom state ---
    let pinchStartDist = 0;
    let pinchStartWidth = 0;
    let pinchStartHeight = 0;

    // --- Views ---
    function showView(view) {
        loginView.classList.add('hidden');
        listView.classList.add('hidden');
        viewerView.classList.add('hidden');
        inputBar.classList.add('hidden');
        view.classList.remove('hidden');
        if (view === viewerView) {
            inputBar.classList.remove('hidden');
        }
    }

    // --- Login ---
    document.getElementById('login-form').addEventListener('submit', async (e) => {
        e.preventDefault();
        const username = document.getElementById('username').value;
        const password = document.getElementById('password').value;
        loginError.textContent = '';

        try {
            const res = await fetch('/api/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username, password })
            });
            if (res.ok) {
                showView(listView);
                loadWindows();
            } else {
                loginError.textContent = 'Login failed. Check credentials.';
            }
        } catch (e) {
            loginError.textContent = 'Connection error.';
        }
    });

    // --- Window List ---
    async function loadWindows() {
        try {
            const res = await fetch('/api/windows');
            if (res.status === 401) {
                showView(loginView);
                return;
            }
            const windows = await res.json();
            windowList.innerHTML = '';
            windows.forEach(w => {
                const div = document.createElement('div');
                div.className = 'window-item';
                div.innerHTML = `<span class="title">${escapeHtml(w.title)}</span><span class="size">${w.width}x${w.height}</span>`;
                div.addEventListener('click', () => connectToWindow(w));
                windowList.appendChild(div);
            });
        } catch (e) {
            windowList.innerHTML = '<div class="error">Failed to load windows.</div>';
        }
    }

    refreshBtn.addEventListener('click', loadWindows);

    // Auto-login: if session cookie is still valid, skip login screen
    (async () => {
        try {
            const res = await fetch('/api/windows');
            if (res.ok) {
                showView(listView);
                const windows = await res.json();
                windowList.innerHTML = '';
                windows.forEach(w => {
                    const div = document.createElement('div');
                    div.className = 'window-item';
                    div.innerHTML = `<span class="title">${escapeHtml(w.title)}</span><span class="size">${w.width}x${w.height}</span>`;
                    div.addEventListener('click', () => connectToWindow(w));
                    windowList.appendChild(div);
                });
            }
        } catch (e) {}
    })();

    // --- Viewer ---
    function connectToWindow(w) {
        currentHwnd = w.hwnd;
        windowTitle.textContent = w.title;
        imageWidth = w.width;
        imageHeight = w.height;
        showView(viewerView);

        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        ws = new WebSocket(`${proto}//${location.host}/ws/stream?hwnd=${w.hwnd}`);

        let pendingHeader = null;

        ws.onmessage = (e) => {
            if (typeof e.data === 'string') {
                // JSON header
                pendingHeader = JSON.parse(e.data);
                return;
            }

            // Binary WebP data
            const header = pendingHeader;
            pendingHeader = null;
            if (!header) return;

            const blob = new Blob([e.data], { type: 'image/webp' });
            const img = new Image();
            img.onload = () => {
                if (header.d === 0) {
                    // Full frame
                    imageWidth = header.fw;
                    imageHeight = header.fh;
                    canvas.width = header.fw;
                    canvas.height = header.fh;
                    ctx.drawImage(img, 0, 0);
                } else {
                    // Diff: draw only the changed region
                    ctx.drawImage(img, header.x, header.y);
                }
                URL.revokeObjectURL(img.src);
            };
            img.src = URL.createObjectURL(blob);
        };

        ws.onclose = () => {};

        sendMsg({ type: 'focus' });
    }

    backBtn.addEventListener('click', () => {
        if (ws) ws.close();
        ws = null;
        currentHwnd = null;
        showView(listView);
        loadWindows();
    });

    function sendMsg(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(obj));
        }
    }

    // --- Text input bar: type here, Enter or Send to paste into remote window ---
    function sendText() {
        const text = textInput.value;
        if (!text || !currentHwnd) return;
        sendMsg({ type: 'paste', text: text, enter: true });
        textInput.value = '';
    }

    sendBtn.addEventListener('click', sendText);

    textInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendText();
        }
    });

    // --- Special keys: keyboard shortcuts sent directly ---
    const specialKeys = {
        'Enter': 13, 'Backspace': 8, 'Tab': 9, 'Escape': 27, 'Delete': 46,
        'ArrowUp': 38, 'ArrowDown': 40, 'ArrowLeft': 37, 'ArrowRight': 39,
        'Home': 36, 'End': 35, 'PageUp': 33, 'PageDown': 34,
        'F1': 112, 'F2': 113, 'F3': 114, 'F4': 115, 'F5': 116,
        'F6': 117, 'F7': 118, 'F8': 119, 'F9': 120, 'F10': 121,
        'F11': 122, 'F12': 123
    };

    // Desktop keyboard: when not typing in text-input, send keys directly
    document.addEventListener('keydown', (e) => {
        if (!currentHwnd) return;
        if (e.target === textInput) return; // Let text input handle its own keys

        if (specialKeys[e.key] !== undefined) {
            e.preventDefault();
            sendMsg({ type: 'keydown', keyCode: specialKeys[e.key] });
            sendMsg({ type: 'keyup', keyCode: specialKeys[e.key] });
            return;
        }

        if (e.keyCode === 229) return;

        e.preventDefault();
        sendMsg({ type: 'keydown', keyCode: e.keyCode });

        if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
            sendMsg({ type: 'char', charValue: e.key });
        }
    });

    document.addEventListener('keyup', (e) => {
        if (!currentHwnd) return;
        if (e.target === textInput) return;
        if (e.keyCode === 229) return;
        e.preventDefault();
        sendMsg({ type: 'keyup', keyCode: e.keyCode });
    });

    // --- Coordinate mapping ---
    function canvasCoords(e) {
        const rect = canvas.getBoundingClientRect();
        const scaleX = imageWidth / rect.width;
        const scaleY = imageHeight / rect.height;

        let clientX, clientY;
        if (e.touches) {
            clientX = e.touches[0].clientX;
            clientY = e.touches[0].clientY;
        } else {
            clientX = e.clientX;
            clientY = e.clientY;
        }

        return {
            x: Math.round((clientX - rect.left) * scaleX),
            y: Math.round((clientY - rect.top) * scaleY)
        };
    }

    // --- Mouse events ---
    canvas.addEventListener('click', (e) => {
        const pos = canvasCoords(e);
        sendMsg({ type: 'focus' });
        sendMsg({ type: 'click', x: pos.x, y: pos.y, rightButton: false });
    });

    canvas.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        const pos = canvasCoords(e);
        sendMsg({ type: 'click', x: pos.x, y: pos.y, rightButton: true });
    });

    canvas.addEventListener('wheel', (e) => {
        e.preventDefault();
        const pos = canvasCoords(e);
        const delta = e.deltaY > 0 ? -120 : 120;
        sendMsg({ type: 'scroll', x: pos.x, y: pos.y, delta: delta });
    }, { passive: false });

    // --- Touch: single finger drag = scroll, tap = click ---
    let touchStart = null;
    let touchMoved = false;
    let scrollThrottle = 0;

    canvas.addEventListener('touchstart', (e) => {
        if (e.touches.length === 1 && !pinchStartDist) {
            touchStart = { x: e.touches[0].clientX, y: e.touches[0].clientY };
            touchMoved = false;
        }
    }, { passive: true });

    canvas.addEventListener('touchmove', (e) => {
        if (e.touches.length === 1 && touchStart && !pinchStartDist) {
            const dx = e.touches[0].clientX - touchStart.x;
            const dy = e.touches[0].clientY - touchStart.y;

            if (Math.abs(dx) > 5 || Math.abs(dy) > 5) {
                touchMoved = true;
                e.preventDefault();

                const now = Date.now();
                if (now - scrollThrottle < 50) return;
                scrollThrottle = now;

                const rect = canvas.getBoundingClientRect();
                const scaleX = imageWidth / rect.width;
                const scaleY = imageHeight / rect.height;
                const cx = Math.round((e.touches[0].clientX - rect.left) * scaleX);
                const cy = Math.round((e.touches[0].clientY - rect.top) * scaleY);

                const scrollDelta = Math.round(dy * 3);
                if (Math.abs(scrollDelta) > 0) {
                    sendMsg({ type: 'scroll', x: cx, y: cy, delta: scrollDelta });
                }

                touchStart.x = e.touches[0].clientX;
                touchStart.y = e.touches[0].clientY;
            }
        }
    }, { passive: false });

    canvas.addEventListener('touchend', (e) => {
        if (e.changedTouches.length === 1 && !pinchStartDist && !touchMoved && touchStart) {
            e.preventDefault();
            const touch = e.changedTouches[0];
            const rect = canvas.getBoundingClientRect();
            const scaleX = imageWidth / rect.width;
            const scaleY = imageHeight / rect.height;
            const x = Math.round((touch.clientX - rect.left) * scaleX);
            const y = Math.round((touch.clientY - rect.top) * scaleY);
            sendMsg({ type: 'focus' });
            sendMsg({ type: 'click', x, y, rightButton: false });
        }
        touchStart = null;
        touchMoved = false;
    });

    // --- Pinch zoom -> window resize (independent X/Y) ---
    let pinchStartX = 0;
    let pinchStartY = 0;

    container.addEventListener('touchstart', (e) => {
        if (e.touches.length === 2) {
            e.preventDefault();
            pinchStartDist = getTouchDist(e.touches);
            pinchStartX = Math.abs(e.touches[0].clientX - e.touches[1].clientX);
            pinchStartY = Math.abs(e.touches[0].clientY - e.touches[1].clientY);
            pinchStartWidth = imageWidth;
            pinchStartHeight = imageHeight;
        }
    }, { passive: false });

    container.addEventListener('touchmove', (e) => {
        if (e.touches.length === 2 && pinchStartDist > 0) {
            e.preventDefault();

            const curX = Math.abs(e.touches[0].clientX - e.touches[1].clientX);
            const curY = Math.abs(e.touches[0].clientY - e.touches[1].clientY);

            const scaleX = pinchStartX > 20 ? curX / pinchStartX : 1;
            const scaleY = pinchStartY > 20 ? curY / pinchStartY : 1;

            let newWidth = Math.round(pinchStartWidth * Math.max(0.3, Math.min(3.0, scaleX)));
            let newHeight = Math.round(pinchStartHeight * Math.max(0.3, Math.min(3.0, scaleY)));

            newWidth = Math.max(200, Math.min(3840, newWidth));
            newHeight = Math.max(150, Math.min(2160, newHeight));

            sendMsg({ type: 'resize', width: newWidth, height: newHeight });
        }
    }, { passive: false });

    container.addEventListener('touchend', (e) => {
        if (e.touches.length < 2) {
            pinchStartDist = 0;
        }
    });

    function getTouchDist(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    // --- Utility ---
    function escapeHtml(str) {
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }
})();
