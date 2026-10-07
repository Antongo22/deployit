export function create(container, projectId) {
    const toolbar = document.createElement('div');
    toolbar.className = 'ssh-terminal-toolbar';
    const status = document.createElement('span');
    status.className = 'ssh-terminal-status';
    status.setAttribute('role', 'status');
    const actions = document.createElement('div');
    actions.className = 'inline-actions';
    const reconnect = document.createElement('button');
    reconnect.type = 'button'; reconnect.className = 'button subtle'; reconnect.textContent = 'Подключиться заново';
    const disconnect = document.createElement('button');
    disconnect.type = 'button'; disconnect.className = 'button subtle'; disconnect.textContent = 'Отключиться';
    actions.append(reconnect, disconnect); toolbar.append(status, actions);
    const screen = document.createElement('div');
    screen.className = 'ssh-terminal-screen';
    screen.setAttribute('aria-label', 'Интерактивный SSH-терминал');
    // FitAddon reads its parent's full computed height. Keep spacing in a
    // separate wrapper so every fitted row stays visible above the bottom gap.
    const frame = document.createElement('div');
    frame.className = 'ssh-terminal-frame';
    frame.append(screen);
    container.replaceChildren(toolbar, frame);
    const terminal = new window.Terminal({
        cursorBlink: true, scrollback: 3000, fontSize: 13,
        fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
        theme: { background: '#0b1017', foreground: '#c8d5e7', cursor: '#d0fa79', selectionBackground: '#35442b' }
    });
    const fit = new window.FitAddon.FitAddon();
    terminal.loadAddon(fit); terminal.open(screen);
    let socket = null, connected = false, disposed = false, failed = false, attempt = 0, pending = null;
    function setStatus(state, text) {
        status.dataset.state = state; status.textContent = text;
        connected = state === 'connected'; failed = state === 'error';
        reconnect.disabled = state === 'connecting';
        disconnect.disabled = state === 'closed' || state === 'error';
    }
    function resize() {
        if (disposed) return;
        fit.fit();
        if (connected && socket?.readyState === WebSocket.OPEN)
            socket.send(JSON.stringify({ type: 'resize', columns: terminal.cols, rows: terminal.rows }));
    }
    const observer = new ResizeObserver(resize);
    observer.observe(screen);
    const input = terminal.onData(data => {
        if (!connected || socket?.readyState !== WebSocket.OPEN) return;
        if (data.length > 32768 || socket.bufferedAmount > 256 * 1024) {
            terminal.writeln('\r\n[Слишком большой ввод. Вставляйте команды небольшими частями.]');
            return;
        }
        for (let start = 0; start < data.length;) {
            let end = Math.min(start + 4096, data.length);
            if (end < data.length && data.charCodeAt(end - 1) >= 0xd800 && data.charCodeAt(end - 1) <= 0xdbff) end--;
            socket.send(JSON.stringify({ type: 'input', data: data.slice(start, end) }));
            start = end;
        }
    });
    async function connect() {
        if (disposed) return;
        const currentAttempt = ++attempt;
        pending?.abort();
        const previous = socket; socket = null;
        if (previous && previous.readyState < WebSocket.CLOSING) previous.close();
        setStatus('connecting', 'Подключаемся по SSH…');
        pending = new AbortController();
        try {
            const response = await fetch('/api/security/csrf', { cache: 'no-store', signal: pending.signal });
            if (!response.ok) throw new Error('Сессия панели истекла. Обновите страницу и войдите заново.');
            const csrf = await response.json();
            if (disposed || currentAttempt !== attempt) return;
            const url = new URL(`/api/projects/${encodeURIComponent(projectId)}/terminal`, window.location.href);
            url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
            const ws = new WebSocket(url, ['deployit-terminal', `csrf.${csrf.requestToken}`]);
            socket = ws; ws.binaryType = 'arraybuffer';
            ws.onmessage = event => {
                if (disposed || socket !== ws) return;
                if (event.data instanceof ArrayBuffer) terminal.write(new Uint8Array(event.data));
                else {
                    try {
                        const message = JSON.parse(event.data);
                        if (message.type === 'status') {
                            setStatus(message.state, message.message);
                            if (message.state === 'connected') { resize(); terminal.focus(); }
                            if (message.state === 'error') terminal.writeln('\r\n[' + message.message + ']');
                        }
                    } catch { setStatus('error', 'Не удалось прочитать ответ терминала. Подключитесь заново.'); }
                }
            };
            ws.onerror = () => { if (socket === ws && !disposed) setStatus('error', 'Терминал не подключился. Обновите страницу и проверьте SSH-подключение.'); };
            ws.onclose = () => { if (socket === ws && !disposed && !failed) setStatus('closed', 'SSH-соединение закрыто. Можно подключиться заново.'); };
        } catch (error) {
            if (!disposed && currentAttempt === attempt && error.name !== 'AbortError') setStatus('error', error.message);
        }
    }
    function stop() {
        attempt++; pending?.abort(); connected = false;
        if (socket && socket.readyState < WebSocket.CLOSING) socket.close();
        socket = null;
        if (!disposed) setStatus('closed', 'SSH-соединение закрыто. Можно подключиться заново.');
    }
    function dispose() {
        if (disposed) return;
        disposed = true; stop(); observer.disconnect(); input.dispose(); terminal.dispose();
        window.removeEventListener('pagehide', dispose); container.replaceChildren();
    }
    reconnect.addEventListener('click', connect);
    disconnect.addEventListener('click', stop);
    window.addEventListener('pagehide', dispose);
    requestAnimationFrame(resize);
    void connect();
    return { connect, dispose };
}
