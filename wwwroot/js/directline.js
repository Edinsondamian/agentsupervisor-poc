const DirectLineConfig = {
    tokenEndpoint: '/api/directline/token',
    joinEventName: 'webchat/join'
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
    sendBoxBackground: 'White',
    sendBoxBorderTop: 'solid 1px #B3E5FC',
    sendBoxTextColor: '#01579B',
    sendBoxPlaceholderTextColor: '#4FC3F7'
};

let webChatStarted = false;

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
        const attachButton = document.getElementById('dl-attach');
        if (attachButton) attachButton.classList.add('hide');
        return;
    }

    const directLine = window.WebChat.createDirectLine({ token: data.token });

    const store = window.WebChat.createStore({}, ({ dispatch }) => next => action => {
        if (action.type === 'DIRECT_LINE/CONNECT_FULFILLED') {
            dispatch({
                type: 'WEB_CHAT/SEND_EVENT',
                payload: { name: DirectLineConfig.joinEventName, value: { language: 'es' } }
            });
        }
        return next(action);
    });

    window.WebChat.renderWebChat(
        {
            directLine: directLine,
            store: store,
            userID: data.userId,
            locale: 'es-ES',
            styleOptions: StyleOptions
        },
        container);

    const attachButton = document.getElementById('dl-attach');
    if (attachButton) attachButton.classList.remove('hide');

    setupAttachments(store);
}

function setupAttachments(store) {
    const attachButton = document.getElementById('dl-attach');
    const fileInput = document.getElementById('dl-file-input');

    if (!attachButton || !fileInput) return;

    attachButton.addEventListener('click', () => fileInput.click());

    fileInput.addEventListener('change', () => {
        const files = Array.from(fileInput.files || []);
        fileInput.value = '';
        if (files.length === 0) return;

        const allowed = files.filter(f =>
            /\.(pdf|png|jpe?g)$/i.test(f.name)
            || ['application/pdf', 'image/png', 'image/jpeg'].includes(f.type));

        if (allowed.length > 0) {
            store.dispatch({ type: 'WEB_CHAT/SET_SEND_FILES', payload: { files: allowed } });
        }
    });
}

document.addEventListener('DOMContentLoaded', setupToggleUI);
