"use strict";

class VideoConcatEditor {
    constructor() {
        this.sections = [];
        this.initialized = false;
    }

    init() {
        this.waitForElements();
    }

    waitForElements() {
        const checkInterval = setInterval(() => {
            const groupContainer = document.querySelector('[data-param-group="Video Concatenation"]');
            if (groupContainer) {
                clearInterval(checkInterval);
                this.createUI(groupContainer);
            }
        }, 200);

        setTimeout(() => clearInterval(checkInterval), 10000);
    }

    createUI(container) {
        const durationsInput = document.getElementById('input_param_Section_Durations');
        const promptsInput = document.getElementById('input_param_Section_Prompts');
        
        if (!durationsInput || !promptsInput) {
            return;
        }

        const editorDiv = document.createElement('div');
        editorDiv.className = 'video-concat-editor-wrapper';
        editorDiv.innerHTML = `
            <div class="video-concat-info">
                <p><strong>Video Concatenation</strong></p>
                <p>Set Section Durations (frames) and Section Prompts below.</p>
                <p>Requires a Video Model in "Image To Video" group.</p>
            </div>
        `;

        container.insertBefore(editorDiv, container.firstChild);
        this.initialized = true;
    }

    parseDurations(text) {
        if (!text) return [];
        return text.split(/[,\s]+/)
            .map(s => parseInt(s.trim()))
            .filter(n => !isNaN(n) && n > 0);
    }

    parsePrompts(text) {
        if (!text) return [];
        return text.split('|||').map(s => s.trim()).filter(s => s.length > 0);
    }
}

let videoConcatEditor = null;

document.addEventListener('DOMContentLoaded', () => {
    videoConcatEditor = new VideoConcatEditor();
    videoConcatEditor.init();
});

if (typeof window !== 'undefined') {
    window.videoConcatEditor = videoConcatEditor;
}