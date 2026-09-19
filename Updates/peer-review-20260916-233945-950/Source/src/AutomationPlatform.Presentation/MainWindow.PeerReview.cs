using System.Diagnostics;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private readonly HashSet<string> _pendingPeerSubmissionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _submittedPeerReviewKeys = new(StringComparer.Ordinal);

    private bool PeerReviewCanAct(Uri expected) => !_workerClosing && !_coursePauseInProgress &&
        !_courseJobCompletionReported && IsCourseraUri(MainWebView.Source) &&
        PeerReviewAutomation.AssignmentPath(expected) is { } path &&
        string.Equals(path, PeerReviewAutomation.AssignmentPath(MainWebView.Source), StringComparison.OrdinalIgnoreCase);

    private async Task<PeerReviewPageState> ProbePeerReviewAsync(Uri expected, string action = "probe", int? budget = null)
    {
        if (!PeerReviewCanAct(expected)) return new() { State = "STOPPED" };
        string raw = await MainWebView.ExecuteScriptAsync(PeerReviewAutomation.BuildProbe(expected, action, budget));
        return JsonSerializer.Deserialize<PeerReviewPageState>(DecodeWebViewString(raw)) ?? new();
    }

    private async Task PausePeerReviewAsync(string detail)
    {
        _viewModel.StatusText = "⏸️ Chưa hoàn tất Review Your Peers. " + detail;
        await PauseCourseJobAsync(_viewModel.StatusText,
            _viewModel.StatusText + " Các đánh giá đã gửi được giữ nguyên; không tự gửi lại.",
            "PEER_REVIEW_ACTION_REQUIRED");
    }

    private async Task ContinueSubmittedPeerToReviewsAsync(Uri submissionUri)
    {
        if (!PeerReviewCanAct(submissionUri)) return;
        string? path = PeerReviewAutomation.AssignmentPath(submissionUri);
        if (path is null) return;
        _pendingPeerSubmissionPaths.Add(path);
        _pendingGradedResultPaths.Add(path);
        _courseHasPendingGradedResults = true;
        // Read the actual review link. Do not click Start new attempt or re-upload an accepted PDF.
        string script = """
            (function(expected) {
                const visible = e => e.getClientRects().length && !e.closest('[hidden],[aria-hidden="true"]');
                const links = Array.from(document.querySelectorAll('a[href]')).filter(visible).map(e => {
                    try { return new URL(e.href, location.href); } catch (_) { return null; }
                }).filter(u => u && u.origin === location.origin &&
                    u.pathname.startsWith(expected + '/') && /\/(give-feedback|reviews?|review-next)(\/|$)/.test(u.pathname.slice(expected.length)));
                return links.length ? links[0].href : '';
            })(__PATH__);
            """;
        string candidate = DecodeWebViewString(await MainWebView.ExecuteScriptAsync(
            script.Replace("__PATH__", JsonSerializer.Serialize(path), StringComparison.Ordinal)));
        Uri? review = Uri.TryCreate(candidate, UriKind.Absolute, out var linked) &&
            PeerReviewAutomation.AssignmentPath(linked) == path && CourseAutomationPolicy.IsPeerReviewPath(linked.AbsolutePath)
            ? linked : PeerReviewAutomation.ReviewHome(submissionUri);
        if (review is null) { await PausePeerReviewAsync("Không tìm thấy đường dẫn review của bài này."); return; }
        _viewModel.StatusText = "➡️ PDF đã nộp; đang mở Review Your Peers, giữ nguyên bài nộp.";
        if (!await NavigatePeerPageAsync(submissionUri, review)) return;
        // Keep the existing peer guard during SPA navigation; handle this destination here.
        await AutoReviewPeersAsync(review);
    }

    private async Task<bool> NavigatePeerPageAsync(Uri expected, Uri destination)
    {
        if (!PeerReviewCanAct(expected)) return false;
        if (MainWebView.Source.AbsoluteUri == destination.AbsoluteUri) MainWebView.Reload();
        else MainWebView.Source = destination;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            if (!PeerReviewCanAct(expected)) return false;
            if (string.Equals(MainWebView.Source.AbsolutePath.TrimEnd('/'), destination.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(500); return PeerReviewCanAct(expected); }
        }
        await PausePeerReviewAsync("Coursera chưa chuyển đến trang cần xử lý.");
        return false;
    }

    private async Task AutoReviewPeersAsync(Uri peerActivityUri)
    {
        Uri? reviewHome = PeerReviewAutomation.ReviewHome(peerActivityUri);
        if (reviewHome is null) return;
        int? remaining = null, initialBudget = null;
        int sent = 0, idle = 0, dialogWaits = 0;
        bool awaitingSubmission = false, awaitingCounter = false;
        int? beforeSubmit = null;
        var timeout = Stopwatch.StartNew();
        while (PeerReviewCanAct(peerActivityUri) && timeout.Elapsed < TimeSpan.FromMinutes(8))
        {
            var page = await ProbePeerReviewAsync(peerActivityUri, budget: remaining);
            if (page.State == "STOPPED") return;
            if (page.State == "STALE_DOCUMENT")
            {
                if (++idle > 30) { await PausePeerReviewAsync("Trang review chưa tải xong hoặc đã chuyển sang nội dung khác."); return; }
                await Task.Delay(500);
                continue;
            }
            if (page.Remaining is >= 0)
            {
                remaining = page.Remaining;
                initialBudget ??= remaining;
            }
            if (page.State == "REVIEWS_COMPLETE")
            {
                await FinishPeerReviewsAsync(peerActivityUri);
                return;
            }
            if (page.State == "NO_REVIEWS_AVAILABLE")
            {
                await PausePeerReviewAsync($"Coursera chưa có bài để đánh giá{(remaining.HasValue ? $"; còn thiếu {remaining} bài" : "")}.");
                return;
            }
            if (page.State == "BLOCKING_DIALOG")
            {
                if (++dialogWaits <= 12)
                {
                    _viewModel.StatusText = "⏳ Đang chờ popup trên trang review đóng lại...";
                    await Task.Delay(500);
                    continue;
                }
                await PausePeerReviewAsync("Có popup thực sự đang mở trên trang. " + page.Detail);
                return;
            }
            dialogWaits = 0;
            if (page.State is "AMBIGUOUS_FORM" or "UNSUPPORTED_RUBRIC" or "ACTION_LIMIT")
            { await PausePeerReviewAsync($"Không xác định được biểu mẫu/điểm tối đa ({page.State}). {page.Detail}"); return; }

            if (awaitingSubmission)
            {
                if (page.SubmissionConfirmed || (page.Remaining.HasValue && page.Remaining < beforeSubmit))
                {
                    awaitingSubmission = false;
                    awaitingCounter = true;
                    idle = 0;
                    if (!await NavigatePeerPageAsync(peerActivityUri, reviewHome)) return;
                    continue;
                }
                if (++idle > 80) { await PausePeerReviewAsync("Đã bấm gửi nhưng chưa thấy xác nhận; dừng để tránh gửi trùng."); return; }
                await Task.Delay(500);
                continue;
            }
            if (awaitingCounter)
            {
                if (page.Remaining.HasValue && page.Remaining < beforeSubmit) { awaitingCounter = false; idle = 0; }
                else
                {
                    if (++idle > 40) { await PausePeerReviewAsync("Review đã gửi nhưng bộ đếm chưa giảm; cần kiểm tra lại trước khi chấm thêm."); return; }
                    await Task.Delay(500);
                    continue;
                }
            }
            if (remaining is null)
            {
                // A direct link to an individual review may omit the top-level remaining counter.
                if (MainWebView.Source.AbsolutePath.TrimEnd('/') != reviewHome.AbsolutePath.TrimEnd('/'))
                { if (!await NavigatePeerPageAsync(peerActivityUri, reviewHome)) return; continue; }
                if (++idle > 30) { await PausePeerReviewAsync("Chưa đọc được dòng Reviews / left to complete, nên chưa tự chấm thêm."); return; }
                await Task.Delay(500);
                continue;
            }
            if (remaining <= 0 || sent >= initialBudget)
            { await PausePeerReviewAsync("Đã tới giới hạn số review cần làm, nhưng chưa xác nhận đủ từ Coursera."); return; }

            _viewModel.StatusText = $"📝 Review Your Peers: còn {remaining} bài cần đánh giá.";
            string? action = page.State switch
            {
                "START_READY" => "start", "NEXT_READY" => "next",
                "FORM_READY" => "fill", "SUBMIT_READY" => "submit", _ => null
            };
            if (action == "submit")
            {
                if (string.IsNullOrWhiteSpace(page.ReviewKey) || _submittedPeerReviewKeys.Contains(page.ReviewKey))
                { await PausePeerReviewAsync("Không xác định được review mới hoặc review này đã bấm gửi trong lượt chạy."); return; }
                var submitted = await ProbePeerReviewAsync(peerActivityUri, "submit", remaining);
                if (submitted.State == "SUBMIT_CLICKED")
                {
                    _submittedPeerReviewKeys.Add(page.ReviewKey);
                    sent++; beforeSubmit = remaining; awaitingSubmission = true; idle = 0;
                    _viewModel.StatusText = $"📤 Đã gửi review; đang chờ Coursera cập nhật số bài còn lại ({remaining}).";
                }
                else if (submitted.State == "INCOMPLETE_FORM")
                { await PausePeerReviewAsync("Còn trường chưa hợp lệ trong review. " + submitted.Detail); return; }
                else idle++;
            }
            else if (action is not null)
            {
                var acted = await ProbePeerReviewAsync(peerActivityUri, action, remaining);
                if (acted.State == "INCOMPLETE_FORM")
                { await PausePeerReviewAsync("Không điền được đầy đủ review. " + acted.Detail); return; }
                idle = acted.State is "START_CLICKED" or "NEXT_CLICKED" or "FORM_FILLED" ? 0 : idle + 1;
            }
            else idle++;
            if (idle > 50) { await PausePeerReviewAsync("Trang review chưa sẵn sàng hoặc không có bước tiếp theo rõ ràng."); return; }
            await Task.Delay(500);
        }
        if (PeerReviewCanAct(peerActivityUri)) await PausePeerReviewAsync("Hết thời gian chờ review; giữ lại tiến độ đã gửi.");
    }

    private async Task FinishPeerReviewsAsync(Uri current)
    {
        string? path = PeerReviewAutomation.AssignmentPath(current);
        if (path is null || !PeerReviewCanAct(current)) return;
        foreach (string suffix in new[] { "review", "reviews", "review-next", "give-feedback" })
            _verifiedPassedPeerPaths.Add(path + "/" + suffix);
        _viewModel.StatusText = "✅ Đủ số bài review Coursera yêu cầu. Đang kiểm tra điểm bài nộp...";
        var submission = new Uri(current.GetLeftPart(UriPartial.Authority) + path);
        if (!await NavigatePeerPageAsync(current, submission)) return;
        PeerPdfPageState page = new();
        for (int attempt = 0; attempt < 24 && PeerReviewCanAct(current); attempt++)
        {
            page = await ProbePeerPdfAsync(submission, "open");
            if (page.State is "SUBMITTED_PASSED" or "SUBMITTED_FAILED") break;
            await Task.Delay(500);
        }
        if (!PeerReviewCanAct(current)) return;
        if (page.State == "SUBMITTED_FAILED")
        {
            await PauseCourseJobAsync("⏸️ Đã đủ review nhưng bài nộp chưa đạt điểm.",
                "Mở My submission để xem phản hồi trước khi sửa hoặc tạo lần nộp mới.", "PEER_ASSIGNMENT_USER_WORK_REQUIRED");
            return;
        }
        if (page.State == "SUBMITTED_PASSED") ReportExistingPeerSubmission(page, submission);
        else
        {
            _pendingPeerSubmissionPaths.Add(path);
            _pendingGradedResultPaths.Add(path);
            _courseHasPendingGradedResults = true;
        }
        if (TryGetCourseHomeUri(submission, out Uri? home) && home != null)
        {
            _viewModel.StatusText = page.State == "SUBMITTED_PASSED"
                ? "✅ Review đã đủ, bài nộp đã đạt. Đang tiếp tục khóa..."
                : "⏳ Review đã đủ; bài nộp còn chờ điểm. Đang xử lý nội dung khác...";
            MainWebView.Source = home;
        }
    }
}
