(function(args) {
    const norm = text => String(text || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const identity = raw => {
        try {
            const u = new URL(raw);
            if (u.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(u.hostname)) return '';
            const p = u.pathname.split('/').filter(Boolean);
            const c = p.indexOf('learn'), a = p.indexOf('peer');
            if (c < 0 || a < 0 || !p[c+1] || !p[a+2]) return '';
            if (p.slice(a+3).some(s => /^(review|reviews|give-feedback)$/.test(s))) return '';
            return [p[c+1], p[a+1], p[a+2]].join('|');
        } catch (_) { return ''; }
    };
    const expected = identity(args.expectedUrl);
    const result = (State, Detail = '') => JSON.stringify({ State, Title: title, Detail });
    let title = '';
    if (!expected || identity(location.href) !== expected) return result('STALE_DOCUMENT');
    const hidden = el => {
        for (let p = el; p && p.nodeType === 1; p = p.parentElement) {
            const s = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse') return true;
        }
        return false;
    };
    const visible = el => !!el && el.isConnected && el.getClientRects().length > 0 && !hidden(el);
    const enabled = el => !el.disabled && el.getAttribute('aria-disabled') !== 'true';
    const inContent = el => !el.closest('aside,nav,[role="navigation"],header,footer');
    const controls = Array.from(document.querySelectorAll('button,a,[role="button"],[role="tab"]')).filter(visible).filter(inContent);
    const label = el => norm(el.getAttribute('aria-label') || el.innerText || el.textContent);
    const tabs = controls.filter(el => /^my submissions?$/.test(label(el)) || label(el) === 'bài nộp của tôi');
    const roleTabs = tabs.filter(el => el.getAttribute('role') === 'tab');
    const tab = roleTabs.length === 1 ? roleTabs[0] : tabs.length === 1 ? tabs[0] : null;
    const panelId = tab?.getAttribute('aria-controls');
    const panel = panelId && document.getElementById(panelId);
    const scope = panel && visible(panel) ? panel : document.querySelector('main,[role="main"]') || document.body;
    // Text nodes only: a hidden or sidebar submission must not mark this form as submitted.
    const contentRoot = document.querySelector('main,[role="main"]') || document.body;
    const walker = document.createTreeWalker(contentRoot, NodeFilter.SHOW_TEXT);
    const pieces = [];
    while (walker.nextNode()) {
        const node = walker.currentNode, el = node.parentElement;
        if (el && visible(el) && inContent(el) && !el.closest('script,style,textarea,input,[role="tablist"]') &&
            !(el.closest('[role="tabpanel"]') && !scope.contains(el)))
            pieces.push(node.textContent);
    }
    const pageText = norm(pieces.join(' '));
    const headings = Array.from(document.querySelectorAll('[data-testid="header-title"],.assignment-title,main h1,[role="main"] h1,h1'));
    title = headings.filter(visible).filter(inContent).map(el => (el.innerText || '').trim()).find(Boolean) || '';
    const within = el => scope.contains(el) && inContent(el);
    const addButtons = controls.filter(within).filter(enabled).filter(el => /^(add file|thêm tệp|thêm tập tin)$/.test(label(el)));
    const inputs = Array.from(scope.querySelectorAll('input[type="file"]')).filter(el =>
        el.isConnected && enabled(el) && inContent(el) && !hidden(el.parentElement));
    const attachments = Array.from(scope.querySelectorAll('a[href],.uppy-Dashboard-Item-name,[data-testid*="attachment"],[data-testid*="uploaded-file"],.rc-FileUploadItem [title],.rc-FileUploadItem a')).filter(visible).filter(inContent);
    const hasPdfAttachment = attachments.some(el => /\.pdf$/i.test(norm(el.innerText) || norm(el.getAttribute('title'))));
    const editorHasAttachment = hasPdfAttachment && Array.from(scope.querySelectorAll('input[type="text"],input:not([type]),input[type="checkbox"]')).some(visible);
    const editing = addButtons.length > 0 || inputs.length > 0 || editorHasAttachment;
    const scopedControls = controls.filter(within);
    const hasNewAttempt = scopedControls.some(el => /^(start new attempt|edit submission|bắt đầu lần (nộp|thử) mới|chỉnh sửa bài nộp)$/.test(label(el)));
    const hasDate = /\bsubmitted on\b|đã nộp (vào|ngày)/.test(pageText);
    const needsReviews = /not reviewed enough peers|review more peers to see your grade|complete (your |the )?required peer reviews|chưa.*đánh giá.*đủ/.test(pageText);
    const pending = /results pending|awaiting (a |your )?grade|waiting for (a |your )?grade|chờ (chấm|kết quả)/.test(pageText);
    const failed = /\b(not passed|you did not pass|you haven't passed|failed)\b|chưa đạt/.test(pageText);
    const passed = /\b(you passed|you have passed|congratulations[^.]{0,80}passed)\b|đã đạt yêu cầu/.test(pageText);
    const explicitlyInactive = tab && (tab.getAttribute('aria-selected') === 'false' || (panel && !visible(panel)));
    if (!explicitlyInactive && !editing && (hasDate || hasNewAttempt || pending || passed || failed)) {
        if (failed) return result('SUBMITTED_FAILED');
        if (needsReviews) return result('SUBMITTED_PENDING_REVIEWS');
        if (pending) return result('SUBMITTED_PENDING_GRADE');
        if (passed) return result('SUBMITTED_PASSED');
        return result('ALREADY_SUBMITTED');
    }
    // Respect explicit inactive tabs; never upload into a different pane.
    if (explicitlyInactive || (!editing && tab && !scope.querySelector('input[type="file"]'))) {
        if (args.action === 'open' && enabled(tab)) {
            const memory = '__acosePeerTab_' + expected;
            if (window[memory]) return result('WAIT');
            window[memory] = true;
            tab.click();
            return result('MY_SUBMISSION_CLICKED');
        }
        if (explicitlyInactive) return result('WAIT');
    }
    if (addButtons.length > 1 || inputs.length > 1) return result('AMBIGUOUS_UPLOAD');
    if (args.expectedFileName) {
        const fileName = norm(args.expectedFileName);
        const alerts = Array.from(scope.querySelectorAll('[role="alert"],.uppy-Informer,[data-testid*="error"]')).filter(visible);
        const error = alerts.find(el => /upload failed|failed to upload|could not upload|file too large|exceeds|invalid file|not supported|tải lên thất bại/i.test(el.innerText));
        if (error) return result('UPLOAD_FAILED', norm(error.innerText).slice(0, 250));
        const busy = /\buploading\b|đang tải lên/.test(pageText) || Array.from(scope.querySelectorAll('[role="progressbar"],.uppy-StatusBar.is-uploading')).some(el =>
            visible(el) && Number(el.getAttribute('aria-valuenow') || 0) < Number(el.getAttribute('aria-valuemax') || 100));
        if (busy) return result('UPLOADING');
        const selected = inputs.some(el => Array.from(el.files || []).some(f => norm(f.name) === fileName));
        const namedAttachment = attachments.some(el =>
            norm(el.innerText) === fileName || norm(el.getAttribute('title')) === fileName);
        if (selected || namedAttachment) {
            if (args.action !== 'submit') return result('UPLOADED');
            if (!editing) return result('WAIT');
            const honorCodes = Array.from(scope.querySelectorAll('input[type="checkbox"]')).filter(el => !hidden(el) && inContent(el)).filter(el =>
                el.id === 'agreement-checkbox-base' || /honor code|understand and agree|i agree|tôi.*đồng ý/.test(norm([
                    el.getAttribute('aria-label'), ...Array.from(el.labels || []).map(label => label.innerText)].join(' '))));
            if (honorCodes.length > 1) return result('SUBMIT_AMBIGUOUS', 'Multiple Honor Code confirmations');
            const buttons = scopedControls.filter(el => el.matches('button,[role="button"]')).filter(el =>
                /^(submit|submit assignment|submit project|nộp bài)$/.test(label(el)) && !el.closest('[role="dialog"],[role="alertdialog"]'));
            if (buttons.length > 1) return result('SUBMIT_AMBIGUOUS', 'Multiple primary submit buttons');
            if (!buttons.length) return result('SUBMIT_NOT_FOUND');
            if (honorCodes.length && !honorCodes[0].checked) {
                if (!enabled(honorCodes[0])) return result('SUBMIT_WAITING');
                honorCodes[0].click();
                return result('HONOR_CONFIRMATION_CLICKED');
            }
            if (!enabled(buttons[0]) || getComputedStyle(buttons[0]).pointerEvents === 'none') return result('SUBMIT_WAITING');
            buttons[0].scrollIntoView({ block: 'center' });
            buttons[0].click();
            return result('SUBMIT_CLICKED');
        }
        if (args.action === 'submit') return result('WAIT');
        if (args.action === 'verifyUpload') return result('WAIT');
    }
    if (addButtons.length > 1 || inputs.length > 1) return result('AMBIGUOUS_UPLOAD');
    if (args.action === 'upload' && addButtons.length === 1) {
        addButtons[0].scrollIntoView({ block: 'center' });
        addButtons[0].click();
        return result('ADD_FILE_CLICKED');
    }
    if (addButtons.length === 1) return result('ADD_FILE_READY');
    if (inputs.length === 1) {
        document.querySelectorAll('[data-acose-peer-upload]').forEach(el => el.removeAttribute('data-acose-peer-upload'));
        inputs[0].setAttribute('data-acose-peer-upload', 'true');
        return result('FILE_INPUT_READY');
    }
    if (editorHasAttachment) return result('EXISTING_UPLOAD');
    const textEditors = Array.from(scope.querySelectorAll('textarea,[contenteditable="true"]')).filter(visible);
    if (textEditors.length && !/upload.*pdf|pdf.*only/.test(pageText)) return result('NO_PDF_FORM');
    return result('WAIT');
})(__PEER_ARGUMENTS__);
