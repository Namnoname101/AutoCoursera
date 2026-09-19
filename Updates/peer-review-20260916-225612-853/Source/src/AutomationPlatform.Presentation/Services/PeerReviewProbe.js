(function(args) {
    const norm = value => String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
    const identity = raw => {
        try {
            const url = new URL(raw);
            if (url.protocol !== 'https:' || !/^(www\.)?coursera\.org$/i.test(url.hostname)) return null;
            const match = url.pathname.match(/^\/learn\/([^/]+)\/peer\/([^/]+)\/([^/]+)(?:\/(.*))?\/?$/);
            if (!match) return null;
            return { key: match.slice(1, 4).join('|'), review: /^(review|reviews|give-feedback)(\/|$)/.test(match[4] || '') };
        } catch (_) { return null; }
    };
    let remaining = null, completed = null, required = null, groupCount = 0, filledComments = 0;
    let reviewKey = '', submissionConfirmed = false;
    const result = (State, Detail = '') => JSON.stringify({ State, Detail, Remaining: remaining,
        Completed: completed, Required: required, GroupCount: groupCount, FilledComments: filledComments,
        ReviewKey: reviewKey, SubmissionConfirmed: submissionConfirmed });
    const expected = identity(args.expectedUrl), actual = identity(location.href);
    if (!expected || !actual || actual.key !== expected.key || !actual.review) return result('STALE_DOCUMENT');
    const action = args.action || 'probe';
    if (!['probe', 'start', 'fill', 'submit', 'next'].includes(action)) return result('WAIT', 'Unsupported action.');
    const hidden = el => {
        for (let p = el; p && p.nodeType === 1; p = p.parentElement) {
            const style = getComputedStyle(p);
            if (p.hidden || p.hasAttribute('inert') || p.getAttribute('aria-hidden') === 'true' ||
                style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return true;
        }
        return false;
    };
    const visible = el => !!el && el.isConnected && el.getClientRects().length > 0 && !hidden(el);
    const enabled = el => !el.disabled && !el.matches(':disabled') && !el.closest('[aria-disabled="true"]');
    const inContent = el => !el.closest('aside,nav,[role="navigation"],header,footer');
    const text = el => norm(el?.innerText || el?.textContent);
    const accessibleLabel = el => norm([
        el.getAttribute('aria-label'),
        ...(el.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.innerText || ''),
        ...Array.from(el.labels || []).map(label => label.innerText),
        el.getAttribute('placeholder'), el.innerText
    ].filter(Boolean).join(' '));
    if (Array.from(document.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog[open]')).some(visible))
        return result('BLOCKING_DIALOG');
    const mains = Array.from(document.querySelectorAll('main,[role="main"]')).filter(visible);
    const roots = mains.filter(el => !mains.some(other => other !== el && el.contains(other)));
    if (roots.length > 1) return result('AMBIGUOUS_FORM', 'Multiple visible content regions.');
    const root = roots[0] || document.body;
    const contentText = (element, counterOnly = false) => {
        const parts = [], walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) {
            const el = walker.currentNode.parentElement;
            if (el && visible(el) && inContent(el) && !el.closest('script,style,textarea,input,[contenteditable="true"]') &&
                (!counterOnly || !el.closest('form,fieldset,[role="radiogroup"],article,[data-testid*="submission"],.rc-Submission')))
                parts.push(walker.currentNode.textContent);
        }
        return norm(parts.join(' '));
    };
    const controls = Array.from(root.querySelectorAll('button,a,[role="button"]')).filter(visible).filter(inContent);
    const buttons = controls.filter(el => !controls.some(other => other !== el && el.contains(other)));
    const named = (regex, enabledOnly = true) => buttons.filter(el => !enabledOnly || enabled(el)).filter(el => regex.test(norm(el.getAttribute('aria-label') || el.innerText || el.textContent)));
    const reviewSubmit = named(/^(submit review|submit feedback|send feedback|submit your review|gửi (đánh giá|nhận xét))$/, false);
    const plainSubmit = named(/^(submit|gửi)$/, false);
    const rawRadios = Array.from(root.querySelectorAll('input[type="radio"],[role="radio"]')).filter(inContent);
    const radioVisible = el => visible(el) || (el.matches('input[type="radio"]') && !hidden(el.parentElement) &&
        Array.from(el.labels || []).some(label => visible(label) && root.contains(label) && inContent(label)));
    const radios = rawRadios.filter(radioVisible).filter(el =>
        !(!el.matches('input') && Array.from(el.querySelectorAll('input[type="radio"]')).some(radioVisible)));
    const hasRadios = radios.length > 0;
    // Completion text in the student's artifact or inside an active grading form is not proof.
    const pageText = contentText(root);
    const countNodes = Array.from(root.querySelectorAll('[data-testid="review-count"],[data-test="review-count"],[data-testid*="review-progress"]'))
        .filter(visible).filter(inContent).filter(el => !el.closest('article,[data-testid*="submission"],.rc-Submission'));
    const statusText = contentText(root, true);
    const countText = countNodes.length ? countNodes.map(el => contentText(el)).join(' ') : statusText;
    {
        const countPatterns = [
            /(?:you (?:have )?(?:completed|reviewed)|completed|reviewed)\s*(\d+)\s*(?:of|out of|\/)\s*(\d+)\s*(?:required\s*)?(?:peer\s*)?(?:reviews|assignments)/,
            /(\d+)\s*(?:of|out of|\/)\s*(\d+)\s*(?:required\s*)?(?:peer\s*)?reviews(?:\s*(?:completed|done))?/,
            /(?:reviews completed|completed reviews)\s*:?\s*(\d+)\s*(?:of|out of|\/)\s*(\d+)/
        ];
        for (const pattern of countPatterns) {
            const match = countText.match(pattern);
            if (match) { completed = Number(match[1]); required = Number(match[2]); remaining = Math.max(0, required - completed); break; }
        }
        const left = countText.match(/(?:you (?:still )?need to (?:review|complete)|review)\s+(\d+)\s+more\s+(?:(?:peer\s*)?(?:assignments|reviews|peers))/) ||
            countText.match(/(\d+)\s+(?:required\s*)?(?:peer\s*)?reviews?\s+(?:left|remaining)/) ||
            countText.match(/reviews?\s*:?\s*(\d+)\s+left to complete/) ||
            (countNodes.length ? countText.match(/(\d+)\s+left to complete/) : null);
        if (left) remaining = Number(left[1]);
        const zeroLeft = remaining === 0 && (required === null || required > 0);
        const completePhrase = /(?:you have|you've|you) completed (?:all (?:of )?(?:your |the )?|(?:your |the ))required peer reviews|(?:all (?:of )?)?(?:your |the )required peer reviews (?:are )?(?:complete|completed)|you (?:have )?reviewed (?:the|all) required (?:number of )?(?:peer )?assignments/.test(countText);
        if (zeroLeft || (completePhrase && !(remaining > 0))) return result('REVIEWS_COMPLETE');
        if (!hasRadios && /no (?:peer )?(?:assignments|submissions|reviews) (?:are )?(?:currently )?available|there (?:are no|aren't any) (?:peer )?(?:assignments|submissions|reviews) (?:available|to review)|check back later(?: to review)?|no peers? to review/.test(statusText))
            return result('NO_REVIEWS_AVAILABLE');
    }
    const memoryKey = '__acosePeerReviews_' + expected.key;
    const memory = window[memoryKey] || (window[memoryKey] = { cycle: 0, count: 0,
        clicked: new Set(), filled: '', submitted: new Set() });
    const budget = remaining ?? (Number.isInteger(args.remainingBudget) ? args.remainingBudget : null);
    if (action !== 'probe' && !(budget > 0)) return result('COUNT_REQUIRED', 'Read the remaining review count before taking an action.');
    const clickOnce = (button, state) => {
        const token = action + '|' + location.pathname + '|' + (remaining ?? args.remainingBudget) + '|' + reviewKey;
        if (memory.count >= 40) return result('ACTION_LIMIT');
        if (memory.clicked.has(token)) return result('WAIT', 'Already clicked this control.');
        memory.clicked.add(token); memory.count++;
        button.scrollIntoView({ block: 'center' }); button.click();
        return result(state);
    };
    if (!hasRadios) {
        const submitted = /(?:your (?:peer )?review|your feedback) (?:has been |was )?(?:successfully )?(?:submitted|saved)|thank you for (?:submitting your review|reviewing)|review submitted(?: successfully)?/.test(statusText);
        submissionConfirmed = submitted;
        const next = named(/^(review (?:another|next)(?: (?:assignment|submission|peer))?|next (?:assignment|review)|đánh giá bài (?:khác|tiếp theo))$/);
        if (next.length > 1) return result('AMBIGUOUS_FORM', 'Multiple next-review controls.');
        if (next.length === 1) {
            if (action === 'next') {
                // A next-review button is explicit navigation, but never resubmit the prior form.
                const response = clickOnce(next[0], 'NEXT_CLICKED');
                if (JSON.parse(response).State === 'NEXT_CLICKED') {
                    memory.cycle++; memory.filled = '';
                }
                return response;
            }
            return result('NEXT_READY');
        }
        if (submitted) return result('REVIEW_SUBMITTED');
        const starts = named(/^(start reviewing|start (?:peer )?review|review assignments|review peers|review (?:your|fellow) peers|begin reviewing|bắt đầu đánh giá|đánh giá bài tập)$/);
        if (starts.length > 1) return result('AMBIGUOUS_FORM', 'Multiple start-review controls.');
        if (starts.length === 1) return action === 'start' ? clickOnce(starts[0], 'START_CLICKED') : result('START_READY');
        if (reviewSubmit.length) return result('UNSUPPORTED_RUBRIC', 'Review form has no identifiable radio rubric.');
        return result('WAIT');
    }
    // A rubric alone is not enough: require a unique review submit control in the same form.
    const submitCandidates = reviewSubmit.length ? reviewSubmit : plainSubmit;
    if (submitCandidates.length !== 1) return result('AMBIGUOUS_FORM', 'Expected one review submit control.');
    const submit = submitCandidates[0];
    const form = submit.closest('form') || root;
    if (radios.some(el => !form.contains(el))) return result('AMBIGUOUS_FORM', 'Radio controls outside the review form.');
    const groups = new Map();
    const nodeIds = new Map();
    const nodeId = el => { if (!nodeIds.has(el)) nodeIds.set(el, nodeIds.size + 1); return nodeIds.get(el); };
    for (const radio of radios) {
        const container = radio.closest('[role="radiogroup"],fieldset');
        const name = radio.matches('input') ? radio.name : '';
        if (!container && !name) return result('UNSUPPORTED_RUBRIC', 'A radio option has no unambiguous group.');
        const key = container ? 'container:' + nodeId(container) + (name ? '|name:' + name : '') : 'name:' + name;
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(radio);
    }
    groupCount = groups.size;
    const selections = [];
    for (const group of groups.values()) {
        if (group.length < 2 || group.some(radio => !enabled(radio)))
            return result('UNSUPPORTED_RUBRIC', 'A rubric group is incomplete or disabled.');
        const options = group.map(radio => {
            const label = accessibleLabel(radio);
            const scores = Array.from(label.matchAll(/(-?\d+(?:[.,]\d+)?)\s*(?:points?|pts?\.?|điểm)\b|\b(?:points?|score)\s*:\s*(-?\d+(?:[.,]\d+)?)/g))
                .map(match => Number((match[1] || match[2]).replace(',', '.')));
            const unique = [...new Set(scores)];
            return { radio, label, score: unique.length === 1 ? unique[0] : null };
        });
        if (options.some(option => option.score === null || !Number.isFinite(option.score)))
            return result('UNSUPPORTED_RUBRIC', 'Each option must display one explicit numeric point score.');
        const max = Math.max(...options.map(option => option.score));
        const best = options.filter(option => option.score === max);
        if (best.length !== 1) return result('UNSUPPORTED_RUBRIC', 'Maximum score is tied or ambiguous.');
        selections.push({ best: best[0], options });
    }
    const selected = radio => radio.matches('input') ? radio.checked : radio.getAttribute('aria-checked') === 'true';
    const editors = Array.from(form.querySelectorAll('textarea,[contenteditable="true"]')).filter(visible).filter(inContent);
    const editable = editors.filter(el => enabled(el) && !el.readOnly);
    const declared = label => /honou?r|academic integrity|i (?:certify|declare|confirm|agree|acknowledge)|identity|signature|full name|email|tôi (?:cam kết|xác nhận|đồng ý)|họ và tên/.test(label);
    if (editable.some(el => declared(accessibleLabel(el)))) return result('INCOMPLETE_FORM', 'An identity or honor declaration needs manual input.');
    const unsupportedRequired = Array.from(form.querySelectorAll('input,select,textarea,[contenteditable="true"]'))
        .filter(el => visible(el) && enabled(el) && (el.required || el.getAttribute('aria-required') === 'true'))
        .filter(el => !radios.includes(el) && !editable.includes(el))
        .filter(el => el.matches('input[type="checkbox"]') ? !el.checked : !String(el.value || '').trim());
    if (unsupportedRequired.length) return result('INCOMPLETE_FORM', 'A required non-review field needs manual input.');
    // Neither arbitrary radio values nor option position is used to infer the grade.
    const submissionId = root.querySelector('[data-review-id],[data-submission-id]');
    const identityToken = submissionId?.getAttribute('data-review-id') || submissionId?.getAttribute('data-submission-id') || '';
    const signature = location.pathname.replace(/\/+$/, '') + '|' + identityToken + '|' + selections.map(group => group.options.map(option => option.label).join('~')).join('|');
    reviewKey = signature;
    if (memory.submitted.has(signature)) return result('SUBMIT_PENDING', 'Waiting for confirmation of the submitted review.');
    const valueOf = el => String(el.matches('textarea') ? el.value : el.textContent || '').trim();
    if (action === 'fill') {
        for (const group of selections) {
            const radio = group.best.radio;
            if (!selected(radio)) radio.click();
        }
        for (const editor of editable) {
            if (valueOf(editor)) continue;
            const comment = 'Thank you for sharing your work.';
            if (editor.matches('textarea')) {
                const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
                if (!setter) return result('INCOMPLETE_FORM', 'Textarea setter is unavailable.');
                setter.call(editor, comment);
            } else editor.textContent = comment;
            editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: comment }));
            editor.dispatchEvent(new Event('change', { bubbles: true }));
            filledComments++;
        }
        if (!selections.every(group => selected(group.best.radio) && group.options.filter(option => selected(option.radio)).length === 1))
            return result('INCOMPLETE_FORM', 'The page has not accepted the chosen rubric values.');
        if (editable.some(el => !valueOf(el))) return result('INCOMPLETE_FORM', 'The page has not accepted the comments.');
        memory.filled = signature;
        return result('FORM_FILLED');
    }
    const scoresReady = selections.every(group => selected(group.best.radio) && group.options.filter(option => selected(option.radio)).length === 1);
    const commentsReady = editable.every(el => !!valueOf(el));
    if (action === 'submit') {
        if (!enabled(submit)) return result('INCOMPLETE_FORM', 'Submit review is still disabled.');
        if (memory.filled !== signature || !scoresReady || !commentsReady) return result('INCOMPLETE_FORM', 'Fill and verify this rubric before submitting.');
        if (Array.from(form.querySelectorAll('input,select,textarea')).filter(visible).some(el => enabled(el) && el.willValidate && !el.checkValidity()))
            return result('INCOMPLETE_FORM', 'The browser reports an invalid field.');
        const response = clickOnce(submit, 'SUBMIT_CLICKED');
        if (JSON.parse(response).State === 'SUBMIT_CLICKED') memory.submitted.add(signature);
        return response;
    }
    return result(memory.filled === signature && scoresReady && commentsReady ? 'SUBMIT_READY' : 'FORM_READY');
})(__PEER_REVIEW_ARGUMENTS__);
