using System.IO;
using System.Text.Json;
using AutomationPlatform.Presentation.Services;
using Microsoft.Web.WebView2.Core;

namespace AutomationPlatform.Presentation;

public partial class MainWindow
{
    private string _peerPdfActionReason = "Kiểm tra bài nộp trong My submission rồi chạy lại khóa.";

    private async Task<PeerPdfPageState> ProbePeerPdfAsync(Uri peerUri, string action = "inspect", string? fileName = null)
    {
        if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported) return new() { State = "STOPPED" };
        if (!IsSamePeerAssignmentActivity(peerUri)) return new() { State = "STALE_DOCUMENT" };
        string raw = await MainWebView.ExecuteScriptAsync(PeerPdfAutomation.BuildProbe(peerUri, action, fileName));
        return JsonSerializer.Deserialize<PeerPdfPageState>(DecodeWebViewString(raw)) ?? new();
    }

    private static bool PeerPdfWasSubmitted(string state) =>
        state == "ALREADY_SUBMITTED" || state.StartsWith("SUBMITTED_", StringComparison.Ordinal);

    private bool ReportExistingPeerSubmission(PeerPdfPageState page, Uri peerUri)
    {
        _viewModel.StatusText = page.State switch
        {
            "SUBMITTED_PASSED" => "✅ Bài Peer-graded đã nộp và đạt yêu cầu.",
            "SUBMITTED_PENDING_REVIEWS" => "⏸️ Bài đã nộp; Coursera yêu cầu hoàn tất đánh giá peer để xem kết quả.",
            "SUBMITTED_FAILED" => "⏸️ Bài đã có kết quả chưa đạt. Cần xem phản hồi trước khi nộp lại.",
            _ => "⏸️ Bài đã nộp; đang chờ kết quả/xác nhận đạt từ Coursera."
        };
        _peerPdfActionReason = page.State == "SUBMITTED_PENDING_REVIEWS"
            ? "Mở kiểm tra và hoàn tất Review your peers. PDF đã nộp được giữ nguyên."
            : "Mở My submission để kiểm tra kết quả. Chỉ tạo lần nộp mới khi bạn muốn sửa bài.";
        if (page.State != "SUBMITTED_PASSED") return false;
        string[] path = peerUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int peerIndex = Array.FindIndex(path, segment => segment.Equals("peer", StringComparison.OrdinalIgnoreCase));
        if (peerIndex >= 0) _verifiedPassedPeerPaths.Add("/" + string.Join('/', path.Take(peerIndex + 3)));
        return true;
    }

    private IEnumerable<string> PeerPdfLibraryRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("ACOSE_PREPARED_PDF_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) yield return Path.GetFullPath(configured);
        string engineRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? installRoot = Path.GetDirectoryName(engineRoot);
        if (installRoot != null && File.Exists(Path.Combine(installRoot, ".acose-customer-manager-install")))
            yield return Path.Combine(installRoot, "PreparedPdfs");
        yield return Path.Combine(engineRoot, "PreparedPdfs");
        yield return Path.Combine(GetDownloadsDirectory(), "Acoursera PDFs");
    }

    private async Task<bool> HandlePreparedPeerPdfAsync(Uri peerUri)
    {
        _peerPdfActionReason = "Kiểm tra My submission rồi chạy lại khóa.";
        await DismissAnyGlobalPopupsAsync();
        PeerPdfPageState page = new();
        for (int attempt = 1; attempt <= 24; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, "open");
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            if (page.State is "FILE_INPUT_READY" or "ADD_FILE_READY" or "EXISTING_UPLOAD") break;
            if (page.State is "STOPPED" or "STALE_DOCUMENT" or "AMBIGUOUS_UPLOAD" or "NO_PDF_FORM") break;
            _viewModel.StatusText = $"⏳ Đang mở My submission ({attempt}/24)...";
            await Task.Delay(500);
        }
        if (page.State is not ("FILE_INPUT_READY" or "ADD_FILE_READY" or "EXISTING_UPLOAD"))
        {
            _viewModel.StatusText = $"⏸️ Chưa xác định được một ô nộp PDF ({page.State}).";
            return false;
        }

        PreparedPdfResolution pdf = PreparedPdfLibrary.Find(page.Title, peerUri, PeerPdfLibraryRoots());
        if (pdf.Path == null)
        {
            string name = GetPeerAssignmentSlug(peerUri) ?? page.Title;
            _viewModel.StatusText = pdf.Status == "AMBIGUOUS"
                ? $"⏸️ Có nhiều PDF cùng khớp bài {name}; cần chọn lại file trong kho."
                : $"⏸️ Chưa có PDF soạn sẵn cho bài {name}.";
            _peerPdfActionReason = $"Thư mục: {pdf.ExpectedDirectory}. Đặt một PDF tên {name}.pdf (hoặc đúng tên bài), rồi chạy lại khóa.";
            return false;
        }
        string fileName = Path.GetFileName(pdf.Path);
        _viewModel.StatusText = $"📤 Đang tải lên {fileName}...";
        PeerPdfPageState beforeUpload = await ProbePeerPdfAsync(peerUri, "inspect", fileName);
        if (PeerPdfWasSubmitted(beforeUpload.State)) return ReportExistingPeerSubmission(beforeUpload, peerUri);
        if (beforeUpload.State == "STALE_DOCUMENT") return false;
        if (beforeUpload.State != "UPLOADED" && beforeUpload.State != "UPLOADING")
        {
            if (!await AttachPreparedPeerPdfAsync(peerUri, pdf.Path, beforeUpload.State))
            {
                _viewModel.StatusText = "⏸️ Chưa gắn được PDF vào ô Add File của bài này.";
                return false;
            }
        }

        bool uploaded = false;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, "verifyUpload", fileName);
            if (page.State == "UPLOADED") { uploaded = true; break; }
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            if (page.State is "STOPPED" or "UPLOAD_FAILED" or "STALE_DOCUMENT") break;
            _viewModel.StatusText = $"⏳ Đang chờ Coursera nhận {fileName} ({attempt + 1}/120)...";
            await Task.Delay(500);
        }
        if (!uploaded)
        {
            _viewModel.StatusText = $"⏸️ Chưa xác nhận upload PDF ({page.State}). {page.Detail}";
            return false;
        }

        // Populate only the blank title of this editor; keep a title the learner has entered.
        string titleScript = """
            (function(args) {
                const current = new URL(location.href), expected = new URL(args.url);
                if (current.origin !== expected.origin || current.pathname !== expected.pathname) return;
                const root = document.querySelector('main,[role="main"]') || document.body;
                const fields = Array.from(root.querySelectorAll('input[type="text"],input:not([type])'))
                    .filter(el => el.getClientRects().length && !el.disabled &&
                        !el.closest('aside,nav,[aria-hidden="true"]'))
                    .filter(el => /project title|tiêu đề/i.test([
                        el.getAttribute('aria-label'), el.placeholder,
                        ...Array.from(el.labels || []).map(label => label.innerText)].join(' ')));
                if (fields.length !== 1 || fields[0].value.trim()) return;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                setter.call(fields[0], args.title);
                fields[0].dispatchEvent(new Event('input', { bubbles: true }));
                fields[0].dispatchEvent(new Event('change', { bubbles: true }));
            })(__ARGS__);
            """.Replace("__ARGS__", JsonSerializer.Serialize(new
            {
                url = MainWebView.Source.AbsoluteUri,
                title = string.IsNullOrWhiteSpace(page.Title) ? GetPeerAssignmentSlug(peerUri) : page.Title
            }), StringComparison.Ordinal);
        if (!IsSamePeerAssignmentActivity(peerUri)) return false;
        await MainWebView.ExecuteScriptAsync(titleScript);
        if (!IsSamePeerAssignmentActivity(peerUri)) return false;
        _viewModel.StatusText = "📤 PDF đã tải lên. Đang nộp bài...";
        string submit = "WAIT";
        for (int attempt = 0; attempt < 48; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri, "submit", fileName);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            submit = page.State;
            if (submit is "SUBMIT_CLICKED" or "SUBMIT_AMBIGUOUS" or "AMBIGUOUS_UPLOAD" or "UPLOAD_FAILED" or "STALE_DOCUMENT" or "STOPPED") break;
            await Task.Delay(250);
        }
        if (submit != "SUBMIT_CLICKED")
        {
            page = await ProbePeerPdfAsync(peerUri);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            _viewModel.StatusText = $"⏸️ PDF đã tải lên nhưng chưa nộp được ({submit}).";
            return false;
        }
        // A click alone is not acceptance. Coursera may submit directly or show one confirmation dialog.
        bool confirmationClicked = false;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            page = await ProbePeerPdfAsync(peerUri);
            if (PeerPdfWasSubmitted(page.State)) return ReportExistingPeerSubmission(page, peerUri);
            if (page.State is "STALE_DOCUMENT" or "STOPPED") break;
            if (!confirmationClicked)
            {
                if (!IsSamePeerAssignmentActivity(peerUri)) return false;
                string confirmation = await ConfirmOwnedCourseraSubmissionAsync();
                confirmationClicked = confirmation == "CLICKED";
            }
            await Task.Delay(500);
        }
        _viewModel.StatusText = "⏸️ Đã bấm nộp PDF nhưng chưa thấy Coursera xác nhận đã nộp.";
        _peerPdfActionReason = "Mở kiểm tra để xác nhận trạng thái bài trước khi chạy lại; tránh tạo lần nộp trùng.";
        return false;
    }

    private async Task<bool> AttachPreparedPeerPdfAsync(Uri peerUri, string filePath, string state)
    {
        var core = MainWebView.CoreWebView2;
        if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported) return false;
        if (!IsSamePeerAssignmentActivity(peerUri)) return false;
        if (state == "FILE_INPUT_READY")
        {
            using var document = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
            int root = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            using var target = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                JsonSerializer.Serialize(new { nodeId = root, selector = "input[type=file][data-acose-peer-upload=true]" })));
            int node = target.RootElement.GetProperty("nodeId").GetInt32();
            if (node == 0 || !IsSamePeerAssignmentActivity(peerUri)) return false;
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new
            {
                nodeId = node, files = new[] { Path.GetFullPath(filePath) }
            }));
            return true;
        }
        if (state != "ADD_FILE_READY") return false;
        var receiver = core.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
        var chosenNode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChooser(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (data.RootElement.TryGetProperty("backendNodeId", out var node) && node.TryGetInt32(out int id) && id > 0)
                    chosenNode.TrySetResult(id);
            }
            catch (JsonException) { chosenNode.TrySetResult(0); }
        }
        receiver.DevToolsProtocolEventReceived += OnChooser;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");
            if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported || !IsSamePeerAssignmentActivity(peerUri)) return false;
            // CDP's user gesture also supports uploaders that call showPicker().
            using var click = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = PeerPdfAutomation.BuildProbe(peerUri, "upload"), userGesture = true, returnByValue = true })));
            if (!click.RootElement.GetProperty("result").TryGetProperty("value", out var value)) return false;
            PeerPdfPageState? clicked = JsonSerializer.Deserialize<PeerPdfPageState>(value.GetString() ?? "{}");
            if (clicked?.State != "ADD_FILE_CLICKED") return false;
            if (await Task.WhenAny(chosenNode.Task, Task.Delay(10000)) != chosenNode.Task || !IsSamePeerAssignmentActivity(peerUri)) return false;
            if (await chosenNode.Task <= 0) return false;
            if (_workerClosing || _coursePauseInProgress || _courseJobCompletionReported) return false;
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new
                {
                backendNodeId = await chosenNode.Task, files = new[] { Path.GetFullPath(filePath) }
            }));
            return true;
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= OnChooser;
            try { await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":false}"); }
            catch { /* The page may have closed while uploading. */ }
        }
    }
}
