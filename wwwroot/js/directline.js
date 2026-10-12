const DirectLineConfig = {
    tokenEndpoint: '/api/directline/token',
    joinEventName: 'webchat/join',
    analyzingMessage: '⏳ ¡Gracias! Estamos analizando tu documento, esto puede tomar unos segundos. Por favor, espera…',
    maxMessageLength: 4000,
    maxFileMb: 5,
    maxFileNameLength: 24,
    maxFiles: 10
};

const StyleOptions = {
    rootHeight: '100%',
    rootWidth: '100%',
    backgroundColor: 'transparent',
    bubbleBackground: '#29B6F6',
    bubbleBorderRadius: 15,
    bubbleBorderWidth: 0,
    bubbleTextColor: 'White',
    bubbleFromUserBackground: '#B3E5FC',
    bubbleFromUserBorderRadius: 15,
    bubbleFromUserBorderWidth: 0,
    bubbleFromUserTextColor: '#01579B',
    avatarSize: 0,
    hideSendBox: true
};

let webChatStarted = false;
let isWaiting = false;
let isUploading = false;
let pendingAttachments = [];
let dlStore = null;
let dlUserId = null;

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text ?? '';
    return div.innerHTML;
}

function attachmentIcon(name) {
    return /\.(png|jpe?g)$/i.test(name) ? 'fas fa-file-image' : 'fas fa-file-pdf';
}

function shortenFileName(name) {
    if (!name || name.length <= DirectLineConfig.maxFileNameLength) return name || '';

    var dot = name.lastIndexOf('.');
    var ext = dot > 0 ? name.slice(dot) : '';
    var keep = DirectLineConfig.maxFileNameLength - ext.length - 1;

    if (keep <= 0) return name.slice(0, DirectLineConfig.maxFileNameLength - 1) + '…';
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
        pendingAttachments = pendingAttachments.filter(a => a.id !== chip.dataset.id);
        removeAttachmentChip(chip);
    });
    return button;
}

function showTyping() {
    const typing = document.getElementById('dl-typing');
    if (typing) typing.classList.remove('hide');
}

function hideTyping() {
    const typing = document.getElementById('dl-typing');
    if (typing) typing.classList.add('hide');
}

function readFileAsDataUri(file) {
    return new Promise((resolve) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => resolve(null);
        reader.readAsDataURL(file);
    });
}

async function handleSelectedFiles(fileList) {
    const files = Array.from(fileList || []);
    if (files.length === 0) return;

    if (pendingAttachments.length + files.length > DirectLineConfig.maxFiles) {
        postBotToast('Solo puedes adjuntar hasta ' + DirectLineConfig.maxFiles + ' documentos por envío.');
        return;
    }

    const valid = files.filter(f => isAllowedFile(f));
    const discarded = files.length - valid.length;
    if (discarded > 0) {
        postBotToast('Solo se permiten archivos PDF o imágenes (JPG/PNG). Se descartaron ' + discarded + '.');
    }
    if (valid.length === 0) return;

    const sizeLimit = DirectLineConfig.maxFileMb * 1024 * 1024;
    const toProcess = valid.filter(f => f.size <= sizeLimit);
    if (toProcess.length < valid.length) {
        postBotToast('Cada archivo debe pesar menos de ' + DirectLineConfig.maxFileMb + ' MB. Se descartaron ' + (valid.length - toProcess.length) + '.');
    }
    if (toProcess.length === 0) return;

    const kinds = new Set(toProcess.map(f => fileKindOf(f)));
    if (kinds.size > 1) {
        postBotToast('No puedes mezclar PDF e imágenes en el mismo envío. Adjunta solo PDF o solo imágenes.');
        return;
    }

    if (pendingAttachments.length > 0 && nameKindOf(pendingAttachments[0].name) !== toUpload.map(f => fileKindOf(f))[0]) {
        postBotToast(pendingAttachments.some(a => nameKindOf(a.name) === 'pdf')
            ? 'Ya tienes PDFs en espera. Envíalos o quítalos antes de adjuntar imágenes.'
            : 'Ya tienes imágenes en espera. Envíalas o quítalas antes de adjuntar PDFs.');
        return;
    }

    isUploading = true;
    setActionButtonsEnabled(false);

    for (const file of toProcess) {
        const chip = addAttachmentChip(file.name, true);

        const dataUri = await readFileAsDataUri(file);
        if (!dataUri) {
            removeAttachmentChip(chip);
            continue;
        }

        const id = 'dl-file-' + Date.now() + '-' + Math.random().toString(36).substring(2, 8);
        chip.dataset.id = id;
        chip.classList.remove('uploading');
        const status = chip.querySelector('.chip-status');
        if (status) status.remove();
        chip.appendChild(buildChipRemoveButton(chip));

        pendingAttachments.push({ id: id, name: file.name, dataUri: dataUri, contentType: file.type || 'application/octet-stream' });
    }

    isUploading = false;
    setActionButtonsEnabled(!isWaiting);
}

var toUpload = [];

function postBotToast(text) {
    if (dlStore) {
        dlStore.dispatch({
            type: 'WEB_CHAT/EMIT_TYPING_INDICATOR'
        });
    }
    const typing = document.getElementById('dl-typing');
    if (typing) {
        typing.classList.remove('hide');
        typing.innerHTML = '<span style="font-size:0.85em;color:#01579B;padding:0.5em 1em;">' + escapeHtml(text) + '</span>';
        setTimeout(() => {
            typing.classList.add('hide');
            typing.innerHTML = '<span class="dot"></span><span class="dot"></span><span class="dot"></span>';
        }, 4000);
    }
}

function sendMessage() {
    if (isWaiting || isUploading || !dlStore) return;

    const input = document.getElementById('chat-input');
    const text = input.value.trim();

    if (!text && pendingAttachments.length === 0) return;

    if (text.length > DirectLineConfig.maxMessageLength) {
        postBotToast('Tu mensaje es demasiado largo. Por favor, resúmelo en menos de ' + DirectLineConfig.maxMessageLength + ' caracteres.');
        return;
    }

    clearAttachmentChips();

    isWaiting = true;
    setInputEnabled(false);

    if (pendingAttachments.length > 0) {
        postBotToast(DirectLineConfig.analyzingMessage);
    }

    showTyping();

    const activity = {
        type: 'message',
        from: { id: dlUserId || 'user', role: 'user' },
        text: text
    };

    if (pendingAttachments.length > 0) {
        activity.attachments = pendingAttachments.map(a => ({
            contentType: a.contentType,
            contentUrl: a.dataUri,
            name: a.name
        }));
    }

    dlStore.dispatch({
        type: 'DIRECT_LINE/POST_ACTIVITY',
        payload: { activity: activity }
    });
}

function setupFooter() {
    const attachButton = document.getElementById('attach-button');
    const fileInput = document.getElementById('file-input');
    const sendButton = document.getElementById('send-button');
    const input = document.getElementById('chat-input');

    attachButton.addEventListener('click', () => fileInput.click());

    fileInput.addEventListener('change', async (evt) => {
        const files = Array.from(evt.target.files || []);
        evt.target.value = '';
        toUpload = files;
        if (files.length === 0) return;
        await handleSelectedFiles(files);
    });

    sendButton.addEventListener('click', sendMessage);

    input.addEventListener('keydown', (evt) => {
        if (evt.key === 'Enter' || evt.keyCode === 13) {
            evt.preventDefault();
            sendMessage();
        }
    });
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

            if (!webChatStarted) {
                webChatStarted = true;
                initDirectLine();
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
}

async function initDirectLine() {
    const container = document.getElementById('webchat');
    const errorBox = document.getElementById('dl-error');

    let data;
    try {
        const res = await fetch(DirectLineConfig.tokenEndpoint);
        if (!res.ok) throw new Error('token');
        data = await res.json();
    } catch (error) {
        console.error('>>> Error obteniendo token DirectLine:', error);
        container.classList.add('hide');
        errorBox.classList.remove('hide');
        return;
    }

    dlUserId = data.userId;

    const directLine = window.WebChat.createDirectLine({ token: data.token });

    dlStore = window.WebChat.createStore({}, ({ dispatch }) => next => action => {
        if (action.type === 'DIRECT_LINE/CONNECT_FULFILLED') {
            dispatch({
                type: 'WEB_CHAT/SEND_EVENT',
                payload: { name: DirectLineConfig.joinEventName, value: { language: 'es' } }
            });
        }

        if (action.type === 'DIRECT_LINE/ACTIVITY_RECEIVED'
            && action.payload?.activity?.from?.role === 'bot') {
            hideTyping();
            isWaiting = false;
            setInputEnabled(true);
        }

        return next(action);
    });

    window.WebChat.renderWebChat(
        {
            directLine: directLine,
            store: dlStore,
            userID: data.userId,
            locale: 'es-ES',
            styleOptions: StyleOptions
        },
        container);

    setupFooter();
}

document.addEventListener('DOMContentLoaded', setupToggleUI);
