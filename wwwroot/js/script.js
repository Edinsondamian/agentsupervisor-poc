const AppConfig = {
    chatEndpoint: '/api/chat',
    attachmentsEndpoint: '/api/attachments',
    joinEventName: 'webchat/join',
    analyzingMessage: '⏳ ¡Gracias! Estamos analizando tu documento, esto puede tomar unos segundos. Por favor, espera…',
    maxMessageLength: 4000,
    maxFileMb: 5,
    maxFileNameLength: 24,
    maxFiles: 10
};

let sessionId = sessionStorage.getItem('crediteval-session');
let joinSent = false;
let isWaiting = false;
let isUploading = false;
let pendingAttachments = [];

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text ?? '';
    return div.innerHTML;
}

function renderMarkdown(text) {
    if (!text) return '';
    const lines = String(text).split(/\r?\n/);
    let html = '';
    let inList = false;

    for (const raw of lines) {
        const line = raw.trim();
        const safe = escapeHtml(line)
            .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
            .replace(/(^|\s)\*([^*\s][^*]*)\*/g, '$1<em>$2</em>');

        const bullet = safe.match(/^(?:[-•])\s+(.*)$/);
        const header = safe.match(/^#{1,3}\s+(.*)$/);

        if (bullet) {
            if (!inList) {
                html += '<ul>';
                inList = true;
            }
            html += '<li>' + bullet[1] + '</li>';
        } else {
            if (inList) {
                html += '</ul>';
                inList = false;
            }
            if (line === '') continue;
            const content = header
                ? '<strong>' + header[1] + '</strong>'
                : safe;
            html += '<p>' + content + '</p>';
        }
    }
    if (inList) html += '</ul>';
    return html;
}

function attachmentIcon(name) {
    return /\.(png|jpe?g)$/i.test(name) ? 'fas fa-file-image' : 'fas fa-file-pdf';
}

function shortenFileName(name) {
    if (!name || name.length <= AppConfig.maxFileNameLength) return name || '';

    var dot = name.lastIndexOf('.');
    var ext = dot > 0 ? name.slice(dot) : '';
    var keep = AppConfig.maxFileNameLength - ext.length - 1;

    if (keep <= 0) return name.slice(0, AppConfig.maxFileNameLength - 1) + '…';
    return name.slice(0, keep) + '…' + ext;
}

function isAllowedFile(file) {
    return /\.(pdf|png|jpe?g)$/i.test(file.name)
        || ['application/pdf', 'image/png', 'image/jpeg'].includes(file.type);
}

function fileKindOf(file) {
    var isImage = /\.(png|jpe?g)$/i.test(file.name)
        || file.type === 'image/png'
        || file.type === 'image/jpeg';
    return isImage ? 'imagen' : 'pdf';
}

function nameKindOf(name) {
    return /\.(png|jpe?g)$/i.test(name) ? 'imagen' : 'pdf';
}

function appendMessage(role, text, attachments) {
    const content = document.getElementById('chat-app_content');
    const message = document.createElement('div');
    message.className = 'message' + (role === 'user' ? ' reply' : '');

    if (role === 'user') {
        let html = text ? '<p>' + escapeHtml(text) + '</p>' : '';
        (attachments || []).forEach(a => {
            html += '<div class="attachment-name" title="' + escapeHtml(a.name) + '"><i class="' + attachmentIcon(a.name) + '"></i><span class="attachment-file">' + escapeHtml(shortenFileName(a.name)) + '</span></div>';
        });
        message.innerHTML = html;
    } else {
        message.innerHTML = renderMarkdown(text);
    }

    content.appendChild(message);
    content.scrollTop = content.scrollHeight;
}

function showTyping() {
    const content = document.getElementById('chat-app_content');
    const typing = document.createElement('div');
    typing.className = 'message typing';
    typing.id = 'typing-indicator';
    typing.innerHTML = '<span class="dot"></span><span class="dot"></span><span class="dot"></span>';
    content.appendChild(typing);
    content.scrollTop = content.scrollHeight;
}

function hideTyping() {
    const typing = document.getElementById('typing-indicator');
    if (typing) typing.remove();
}

function setInputEnabled(enabled) {
    document.getElementById('chat-input').disabled = !enabled;
    document.getElementById('send-button').disabled = !enabled;
    document.getElementById('attach-button').disabled = !enabled;
}

function setActionButtonsEnabled(enabled) {
    document.getElementById('send-button').disabled = !enabled;
    document.getElementById('attach-button').disabled = !enabled;
}

function addAttachmentChip(name, uploading) {
    const bar = document.getElementById('chat-app_attachments');
    const chip = document.createElement('div');
    chip.className = 'attachment-chip' + (uploading ? ' uploading' : '');
    chip.title = name;
    chip.innerHTML =
        '<i class="' + attachmentIcon(name) + '"></i>' +
        '<span class="chip-name">' + escapeHtml(shortenFileName(name)) + '</span>' +
        (uploading ? '<span class="chip-status">subiendo…</span>' : '');
    if (!uploading) {
        chip.appendChild(buildChipRemoveButton(chip));
    }
    bar.appendChild(chip);
    return chip;
}

function removeAttachmentChip(chip) {
    chip.remove();
}

function clearAttachmentChips() {
    pendingAttachments = [];
    const bar = document.getElementById('chat-app_attachments');
    if (bar) bar.innerHTML = '';
}

function buildChipRemoveButton(chip) {
    const button = document.createElement('button');
    button.className = 'chip-remove';
    button.type = 'button';
    button.setAttribute('aria-label', 'Quitar adjunto');
    button.innerHTML = '<i class="fas fa-times"></i>';
    button.addEventListener('click', () => {
        pendingAttachments = pendingAttachments.filter(a => a.fileId !== chip.dataset.fileId);
        removeAttachmentChip(chip);
    });
    return button;
}

async function uploadAttachment(file) {
    if (!isAllowedFile(file)) {
        appendMessage('bot', 'Solo se permiten archivos PDF o imágenes (JPG/PNG).');
        return;
    }
    if (file.size > AppConfig.maxFileMb * 1024 * 1024) {
        appendMessage('bot', 'El archivo supera el límite de ' + AppConfig.maxFileMb + ' MB.');
        return;
    }

    const chip = addAttachmentChip(file.name, true);
    isUploading = true;
    setActionButtonsEnabled(false);

    const formData = new FormData();
    formData.append('sessionId', sessionId || '');
    formData.append('file', file);

    try {
        const res = await fetch(AppConfig.attachmentsEndpoint, {
            method: 'POST',
            body: formData
        });

        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            removeAttachmentChip(chip);
            appendMessage('bot', data.error || 'No se pudo adjuntar el archivo. Inténtalo de nuevo.');
            return;
        }

        if (data.sessionId && !sessionId) {
            sessionId = data.sessionId;
            sessionStorage.setItem('crediteval-session', sessionId);
        }

        chip.dataset.fileId = data.fileId;
        chip.classList.remove('uploading');
        const status = chip.querySelector('.chip-status');
        if (status) status.remove();
        chip.appendChild(buildChipRemoveButton(chip));

        pendingAttachments.push({ fileId: data.fileId, name: data.name || file.name });
    } catch (error) {
        console.error('>>> Error subiendo adjunto:', error);
        removeAttachmentChip(chip);
        appendMessage('bot', 'No pude subir el archivo. Verifica tu conexión e inténtalo de nuevo.');
    } finally {
        isUploading = false;
        setActionButtonsEnabled(!isWaiting);
    }
}

async function handleSelectedFiles(fileList) {
    const files = Array.from(fileList || []);
    if (files.length === 0) return;

    if (pendingAttachments.length + files.length > AppConfig.maxFiles) {
        appendMessage('bot', 'Solo puedes adjuntar hasta ' + AppConfig.maxFiles + ' documentos por envío.');
        return;
    }

    const valid = files.filter(f => isAllowedFile(f));
    const discarded = files.length - valid.length;
    if (discarded > 0) {
        appendMessage('bot', 'Solo se permiten archivos PDF o imágenes (JPG/PNG). Se descartaron ' + discarded + '.');
    }
    if (valid.length === 0) return;

    const sizeLimit = AppConfig.maxFileMb * 1024 * 1024;
    const toUpload = valid.filter(f => f.size <= sizeLimit);
    if (toUpload.length < valid.length) {
        appendMessage('bot', 'Cada archivo debe pesar menos de ' + AppConfig.maxFileMb + ' MB. Se descartaron ' + (valid.length - toUpload.length) + '.');
    }
    if (toUpload.length === 0) return;

    const kinds = new Set(toUpload.map(f => fileKindOf(f)));
    if (kinds.size > 1) {
        appendMessage('bot', 'No puedes mezclar PDF e imágenes en el mismo envío. Adjunta solo PDF o solo imágenes.');
        return;
    }

    if (pendingAttachments.length > 0 && nameKindOf(pendingAttachments[0].name) !== toUpload.map(f => fileKindOf(f))[0]) {
        appendMessage('bot', pendingAttachments.some(a => nameKindOf(a.name) === 'pdf')
            ? 'Ya tienes PDFs en espera. Envíalos o quítalos antes de adjuntar imágenes.'
            : 'Ya tienes imágenes en espera. Envíalas o quítalas antes de adjuntar PDFs.');
        return;
    }

    for (const file of toUpload) {
        await uploadAttachment(file);
    }
}

function setupAttachmentsUI() {
    const attachButton = document.getElementById('attach-button');
    const fileInput = document.getElementById('file-input');

    attachButton.addEventListener('click', () => fileInput.click());

    fileInput.addEventListener('change', async (evt) => {
        const files = Array.from(evt.target.files || []);
        evt.target.value = '';
        if (files.length === 0) return;
        await handleSelectedFiles(files);
    });
}

async function sendMessage() {
    if (isWaiting || isUploading) return;

    const input = document.getElementById('chat-input');
    const text = input.value.trim();
    const attachments = pendingAttachments.slice();

    if (!text && attachments.length === 0) return;

    if (text.length > AppConfig.maxMessageLength) {
        appendMessage('bot', 'Tu mensaje es demasiado largo. Por favor, resúmelo en menos de ' + AppConfig.maxMessageLength + ' caracteres.');
        return;
    }

    appendMessage('user', text, attachments);
    input.value = '';
    isWaiting = true;
    setInputEnabled(false);
    clearAttachmentChips();

    if (attachments.length > 0) {
        appendMessage('bot', AppConfig.analyzingMessage);
    }

    showTyping();

    try {
        const res = await fetch(AppConfig.chatEndpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                sessionId: sessionId,
                message: text,
                attachmentIds: attachments.map(a => a.fileId)
            })
        });

        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            appendMessage('bot', data.error || 'Ocurrió un error al procesar tu consulta. Inténtalo de nuevo en unos momentos.');
            return;
        }

        if (data.sessionId) {
            sessionId = data.sessionId;
            sessionStorage.setItem('crediteval-session', sessionId);
        }

        appendMessage('bot', data.content || '(sin respuesta)');
    } catch (error) {
        console.error('>>> Error enviando mensaje:', error);
        appendMessage('bot', 'No pude conectarme con el asistente. Verifica tu conexión e inténtalo de nuevo.');
    } finally {
        hideTyping();
        isWaiting = false;
        setInputEnabled(true);
        input.focus();
    }
}

async function sendJoinEvent() {
    showTyping();
    try {
        const res = await fetch(AppConfig.chatEndpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sessionId: sessionId, event: AppConfig.joinEventName })
        });

        const data = await res.json().catch(() => ({}));

        if (res.ok && data.content) {
            if (data.sessionId) {
                sessionId = data.sessionId;
                sessionStorage.setItem('crediteval-session', sessionId);
            }
            appendMessage('bot', data.content);
        }
    } catch (error) {
        console.error('>>> Error enviando ' + AppConfig.joinEventName + ':', error);
    } finally {
        hideTyping();
    }
}

function setupToggleUI() {
    const chat = document.querySelector('#chat-app');
    const toggles = chat.querySelectorAll('.toggle');
    const close = chat.querySelector('.close');

    toggles.forEach(toggle => {
        toggle.addEventListener('click', () => {
            chat.classList.add('is-active');
            toggle.classList.add('hide');
            close.classList.remove('hide');

            if (!joinSent) {
                joinSent = true;
                sendJoinEvent();
            }

            document.getElementById('chat-input').focus();
        });
    });

    close.addEventListener('click', () => {
        chat.classList.remove('is-active');
        toggles.forEach(toggle => {
            toggle.classList.remove('hide');
            close.classList.add('hide');
        });
    });

    document.onkeydown = function (evt) {
        const isEscape = (evt.key === 'Escape' || evt.key === 'Esc' || evt.keyCode === 27);
        if (isEscape) {
            chat.classList.remove('is-active');
        }
    };

    document.getElementById('send-button').addEventListener('click', sendMessage);

    document.getElementById('chat-input').addEventListener('keydown', (evt) => {
        if (evt.key === 'Enter' || evt.keyCode === 13) {
            evt.preventDefault();
            sendMessage();
        }
    });
}

document.addEventListener('DOMContentLoaded', () => {
    setupToggleUI();
    setupAttachmentsUI();
});
