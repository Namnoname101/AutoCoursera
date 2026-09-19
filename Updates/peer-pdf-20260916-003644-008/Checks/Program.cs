using System.Text.Json;
using AutomationPlatform.Presentation.Services;
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
var page = await browser.NewPageAsync();
const string expectedUrl = "https://www.coursera.org/learn/course-a/peer/P1/personal-development-plan-pdp";
const string assignmentTitle = "Personal Development Plan (PDP)";
var expectedUri = new Uri(expectedUrl);
string html = "";
int passed = 0;
var failures = new List<string>();
await page.RouteAsync("**/*", route => route.FulfillAsync(new()
{
    Status = 200,
    ContentType = "text/html",
    Body = "<!doctype html><html><body>" + html + "</body></html>"
}));

string Tabs(bool selected = true) => "<div role='tablist'><button role='tab' aria-selected='" + (!selected).ToString().ToLowerInvariant()
    + "'>Instructions</button><button id='submission-tab' role='tab' aria-selected='" + selected.ToString().ToLowerInvariant()
    + "' onclick=\"window.tabClicks=(window.tabClicks||0)+1;this.setAttribute('aria-selected','true')\">My submission</button></div>";
string Document(string content, string extras = "", bool selected = true) => extras + "<main><h1>" + assignmentTitle + "</h1>" + Tabs(selected)
    + "<section role='tabpanel' aria-labelledby='submission-tab'>" + content + "</section></main>";
string PaneDocument(string content, string banner = "", string otherPane = "") => "<main><h1>" + assignmentTitle + "</h1>" + banner
    + Tabs().Replace("id='submission-tab'", "id='submission-tab' aria-controls='submission-pane'")
    + "<section id='submission-pane' role='tabpanel' aria-labelledby='submission-tab'>" + content + "</section>"
    + "<section id='instructions-pane' role='tabpanel'>" + otherPane + "</section></main>";
string UploadDialog(string content) => "<div role='dialog' aria-modal='true' aria-label='Upload File' class='uppy-Dashboard uppy-Dashboard--modal'><h2>Upload File</h2>"
    + "<div class='uppy-Dashboard-inner'><p>Drop files here or</p>" + content + "</div></div>";
string AddFile(string attributes = "", string handler = "window.uploadClicks=(window.uploadClicks||0)+1") =>
    "<div class='rc-UppyFileUploader'><button type='button' " + attributes + " onclick=\"" + handler + "\"><span>Add File</span></button></div>";
string Editing(string upload) => "<form><label>Project Title<input value='VIP'></label><h2>Upload (PDF file only)</h2>" + upload
    + "<label><input type='checkbox'>I understand and agree</label><button type='button' onclick='window.submitClicks=(window.submitClicks||0)+1'>Submit</button></form>";
string Submitted(string notice, string grade = "") => "<p>" + notice + "</p><h2>VIP</h2><p>Submitted on August 30, 2026</p>"
    + "<div>" + grade + "</div><h3>PROMPT 1</h3><p>Upload (PDF file only)</p><p>No uploaded file</p>"
    + "<button onclick='window.attemptClicks=(window.attemptClicks||0)+1'>Start new attempt</button>";
async Task Load(string content, string? url = null) { html = content; await page.GotoAsync(url ?? expectedUrl); }
async Task<string> Probe(string action = "inspect", string? file = null, Uri? expected = null)
{
    var json = await page.EvaluateAsync<string>(PeerPdfAutomation.BuildProbe(expected ?? expectedUri, action, file));
    using var result = JsonDocument.Parse(json);
    return result.RootElement.GetProperty("State").GetString()!;
}
void Pass(string name) { Console.WriteLine("PASS " + name); passed++; }
void Fail(string message) { Console.WriteLine("FAIL " + message); failures.Add(message); }
async Task AssertState(string name, string expected, string action = "inspect", string? file = null)
{
    string actual = await Probe(action, file);
    if (actual != expected) Fail($"{name}: {actual}, expected {expected}");
    else Pass(name);
}
async Task AssertCount(string name, string key, int expected)
{
    int actual = await page.EvaluateAsync<int>("window[" + JsonSerializer.Serialize(key) + "] || 0");
    if (actual != expected) Fail($"{name}: {actual} clicks, expected {expected}");
    else Pass(name);
}

await Load(Document(Submitted("Your grade is ready, but you have not reviewed enough peers yet. Review more peers to see your grade.", "Results pending")));
await AssertState("Submitted screenshot waits for required reviews, not an upload field", "SUBMITTED_PENDING_REVIEWS", "open");
await AssertCount("Already submitted never starts a new attempt", "attemptClicks", 0);
await Load(Document(Submitted("Your assignment has been submitted and is waiting for a grade.", "Results pending")));
await AssertState("Pending grade is distinct from an editable submission", "SUBMITTED_PENDING_GRADE");
await Load(Document(Submitted("You passed!", "Grade: 100%")));
await AssertState("Explicit passed result is recognized", "SUBMITTED_PASSED");
await Load(Document(Submitted("You have not passed this assignment.", "Grade: 60%")));
await AssertState("Failed submission is preserved for deliberate retry", "SUBMITTED_FAILED");
await Load(Document(Submitted("")));
await AssertState("Existing submission without a result is still already submitted", "ALREADY_SUBMITTED");
await Load(PaneDocument(Submitted(""), "<div>Your grade is ready, but you have not reviewed enough peers yet. Review more peers to see your grade.</div>"));
await AssertState("Review requirement above a linked submission pane is retained", "SUBMITTED_PENDING_REVIEWS");
await Load(PaneDocument(Submitted(""), "<div>Results pending</div>"));
await AssertState("Grade pending banner above a linked submission pane is retained", "SUBMITTED_PENDING_GRADE");
await Load(PaneDocument(Submitted(""), "<div>You passed! Grade: 100%</div>"));
await AssertState("Passed banner above a linked submission pane is retained", "SUBMITTED_PASSED");
await Load(PaneDocument(Submitted(""), "<div>You did not pass. Grade: 60%</div>"));
await AssertState("Failed banner above a linked submission pane is retained", "SUBMITTED_FAILED");
await Load(PaneDocument(Submitted(""), otherPane: "<p>You passed!</p>"));
await AssertState("A different tabpanel does not override this submission's result", "ALREADY_SUBMITTED");

string PanelDocument(string content, string banner = "", bool selected = true) =>
    "<main><h1>" + assignmentTitle + "</h1><div>" + banner + "</div>" +
    Tabs(selected).Replace("id='submission-tab'", "id='submission-tab' aria-controls='submission-panel'") +
    "<section id='submission-panel' role='tabpanel' aria-labelledby='submission-tab'>" + content + "</section></main>";
foreach (var banner in new[] {
    ("Your grade is ready, but you have not reviewed enough peers yet.", "SUBMITTED_PENDING_REVIEWS"),
    ("Results pending", "SUBMITTED_PENDING_GRADE"),
    ("You passed!", "SUBMITTED_PASSED"),
    ("You have not passed this assignment.", "SUBMITTED_FAILED") })
{
    await Load(PanelDocument(Submitted(""), banner.Item1));
    await AssertState("Assignment banner outside aria-controls panel: " + banner.Item2, banner.Item2);
}
await Load(PanelDocument(Editing(AddFile()), "After submitting, complete your required peer reviews."));
await AssertState("Review instructions outside editor do not mark it submitted", "ADD_FILE_READY");
await Load(PanelDocument("<p>Complete your required peer reviews after submitting</p>", selected: false));
await AssertState("Instructions pane opens My submission before detecting status", "MY_SUBMISSION_CLICKED", "open");
await Load(PanelDocument("<p>The assignment form is loading</p>"));
await AssertState("Empty pane is not a submitted assignment", "WAIT");
await Load(PanelDocument(Editing("<input type='file'><input type='file'>")));
await AssertState("Two direct file destinations need user selection", "AMBIGUOUS_UPLOAD", "upload");
await Load(Document(Editing(AddFile())), expectedUrl + "/give-feedback");
await AssertState("Review path cannot receive submission upload", "STALE_DOCUMENT", "upload");

await Load(Document(Editing(AddFile())));
await AssertState("Lazy Uppy Add File is ready before a file input exists", "ADD_FILE_READY", "open");
await AssertCount("Opening the selected submission tab does not upload", "uploadClicks", 0);
await Load(Document(Editing("<input type='file' accept='.pdf' style='display:none'>")));
await AssertState("Hidden real file input can accept a CDP upload", "FILE_INPUT_READY");
await Load(Document(Editing(AddFile()), "<aside><p>Submitted on August 1, 2026</p><button>Start new attempt</button><p>You passed!</p></aside>"));
await AssertState("Unrelated sidebar submission does not override active form", "ADD_FILE_READY");
await Load(Document(Editing(AddFile()) + "<div hidden><p>Submitted on August 1, 2026</p><button>Start new attempt</button></div>"));
await AssertState("Hidden historical result does not override active form", "ADD_FILE_READY");
await Load(Document("<p>Previous attempt: Submitted on August 1, 2026</p>" + Editing(AddFile())));
await AssertState("Editing a new attempt overrides previous submitted date", "ADD_FILE_READY");
await Load(Document("<p>Assignment instructions</p>", selected: false));
await AssertState("Open action switches to My submission", "MY_SUBMISSION_CLICKED", "open");
await AssertCount("My submission is clicked once", "tabClicks", 1);
await Probe("open");
await AssertCount("Selected tab is not repeatedly clicked", "tabClicks", 1);
await Load(Document("<p>Assignment instructions</p>", selected: false));
await Probe();
await AssertCount("Inspect never changes the selected tab", "tabClicks", 0);

await Load(Document(Editing(AddFile())), expectedUrl + "/submission?attempt=2");
await AssertState("Same assignment submission route and query are allowed", "ADD_FILE_READY");
await Load(Document(Editing(AddFile())), expectedUrl.Replace("/P1/", "/P2/"));
await AssertState("Different peer identity blocks upload", "STALE_DOCUMENT", "upload");
await AssertCount("Stale activity has no upload side effect", "uploadClicks", 0);
await Load(Document(Editing(AddFile())), expectedUrl.Replace("course-a", "course-b"));
await AssertState("Different course identity blocks upload", "STALE_DOCUMENT", "upload");
await Load(Document(Editing(AddFile())), expectedUrl.Replace("www.coursera.org", "example.com"));
await AssertState("External page blocks upload", "STALE_DOCUMENT", "upload");

await Load(Document(Editing(AddFile() + AddFile())));
await AssertState("Two upload destinations require a choice", "AMBIGUOUS_UPLOAD", "upload");
await AssertCount("Ambiguous upload buttons are untouched", "uploadClicks", 0);
await Load(PaneDocument(Editing(AddFile()) + Editing(AddFile())));
await AssertState("Two empty PDF forms are ambiguous before choosing a file", "AMBIGUOUS_UPLOAD", "upload");
await AssertCount("Duplicate empty PDF forms have no upload side effect", "uploadClicks", 0);
await Load(PaneDocument(Editing("<input type='file'><input type='file'>")));
await AssertState("Two direct file inputs are ambiguous before choosing a file", "AMBIGUOUS_UPLOAD", "upload");
await Load(PaneDocument(Editing("<p>No uploaded file</p>")));
await AssertState("Empty form without its uploader stays pending", "WAIT");
await Load(Document(Editing(AddFile("disabled") + AddFile("style='display:none'"))));
string inactiveState = await Probe("upload");
if (inactiveState is "ADD_FILE_CLICKED" or "ADD_FILE_READY") throw new Exception("Inactive upload controls treated as ready");
await AssertCount("Disabled and hidden upload controls are untouched", "uploadClicks", 0);
await Load(Document(Editing(AddFile()) + "<p>pdp.pdf</p>"));
await AssertState("A filename in instructions is not proof of an attachment", "WAIT", "verifyUpload", "pdp.pdf");
await Load(Document(Editing(AddFile()) + "<a class='uppy-Dashboard-Item-name' href='https://files.example.test/other.pdf'>other.pdf</a>"));
await AssertState("A different attachment does not confirm the selected PDF", "WAIT", "verifyUpload", "pdp.pdf");
await Load(Document(Editing(AddFile()) + "<div role='progressbar' aria-valuenow='35' aria-valuemax='100'></div><a class='uppy-Dashboard-Item-name' href='https://files.example.test/pdp.pdf'>pdp.pdf</a>"));
await AssertState("An upload in progress is not treated as finished", "UPLOADING", "verifyUpload", "pdp.pdf");
await Load(Document(Editing(AddFile()) + "<div role='alert'>Upload failed: network error</div><a class='uppy-Dashboard-Item-name' href='https://files.example.test/pdp.pdf'>pdp.pdf</a>"));
await AssertState("An upload error overrides an attachment name", "UPLOAD_FAILED", "verifyUpload", "pdp.pdf");

await Load(Document("<form><label for='project-title'>Project Title</label><input id='project-title' value=''><label for='profile-name'>Profile name</label><input id='profile-name' value='Customer Name'>" + AddFile() + "</form>"));
await page.EvaluateAsync("""
    (() => {
        const input = document.getElementById('project-title');
        Object.defineProperty(input, 'value', {get() {return HTMLInputElement.prototype.__lookupGetter__('value').call(this);}, set(value) { window.directSetterCalls=(window.directSetterCalls||0)+1; }});
        input.addEventListener('input', () => window.titleInputEvents=(window.titleInputEvents||0)+1);
        input.addEventListener('change', () => window.titleChangeEvents=(window.titleChangeEvents||0)+1);
    })()
    """);
await Probe("prepareTitle");
string preparedTitle = await page.Locator("#project-title").InputValueAsync();
if (preparedTitle != assignmentTitle) Fail("Blank project title uses the assignment heading: " + preparedTitle); else Pass("Blank project title uses the assignment heading");
await AssertCount("Project title uses native value setter", "directSetterCalls", 0);
await AssertCount("Project title dispatches input event", "titleInputEvents", 1);
await AssertCount("Project title dispatches change event", "titleChangeEvents", 1);
if (await page.Locator("#profile-name").InputValueAsync() != "Customer Name") Fail("Profile name was changed by project title preparation"); else Pass("Profile name is preserved during project title preparation");
await Probe("prepareTitle");
await AssertCount("Already populated project title is preserved", "titleInputEvents", 1);
await Load(Document(Editing(AddFile())));
await Probe("prepareTitle");
if (await page.Locator("form input:not([type])").InputValueAsync() != "VIP") Fail("Existing VIP project title was replaced"); else Pass("Existing author-entered project title is preserved");
await Load(Document(Editing(AddFile()).Replace("value='VIP'", "value=''")).Replace("<h1>" + assignmentTitle + "</h1>", ""));
await Probe("prepareTitle");
if (await page.Locator("form input:not([type])").InputValueAsync() != "okkkook") Fail("Missing assignment heading did not use requested fallback title"); else Pass("Missing assignment heading uses fallback project title");

async Task OpenDialog(string dialogContent, bool hideMain = true, string? mainContent = null)
{
    await Load(PaneDocument(mainContent ?? Editing(AddFile())));
    await page.EvaluateAsync("""
        (args) => {
            document.querySelector('.rc-UppyFileUploader button').addEventListener('click', () => {
                document.body.insertAdjacentHTML('beforeend', args.html);
                if (args.hideMain) document.querySelector('main').setAttribute('aria-hidden', 'true');
            });
        }
        """, new { html = UploadDialog(dialogContent), hideMain });
    await AssertState("Add File opens the HTML upload dialog", "ADD_FILE_CLICKED", "upload");
}
await OpenDialog("<button class='uppy-Dashboard-browse' onclick='window.browseClicks=(window.browseClicks||0)+1'>browse files</button>");
await AssertState("Upload dialog is detected outside aria-hidden main", "UPLOAD_DIALOG_READY");
await Probe("submit", "pdp.pdf");
await AssertCount("Background submission is untouched while upload dialog is open", "submitClicks", 0);
await OpenDialog("<button class='uppy-Dashboard-browse'>browse files</button>", hideMain: false,
    mainContent: Editing(AddFile() + "<a class='uppy-Dashboard-Item-name' href='https://files.example.test/pdp.pdf'>pdp.pdf</a>"));
await page.Locator("main input[type=checkbox]").CheckAsync();
await Probe("submit", "pdp.pdf");
await AssertCount("Even an enabled attached background form cannot submit through modal", "submitClicks", 0);
await Load(PaneDocument(Editing(AddFile())) + "<div role='dialog' aria-label='Profile settings'><h2>Profile settings</h2><button onclick='window.wrongBrowse=(window.wrongBrowse||0)+1'>browse files</button></div>");
await Probe("chooseFile", "pdp.pdf");
await AssertCount("Unrelated dialog browse button is untouched", "wrongBrowse", 0);
await OpenDialog("<button class='uppy-Dashboard-browse' onclick='window.browseClicks=(window.browseClicks||0)+1'>browse files</button><button class='uppy-Dashboard-browse' onclick='window.browseClicks=(window.browseClicks||0)+1'>browse files</button>");
await AssertState("Duplicate browse destinations inside upload dialog are ambiguous", "AMBIGUOUS_UPLOAD", "chooseFile");
await AssertCount("Ambiguous dialog has no browse side effect", "browseClicks", 0);
const string modalFileWithFinish = "<span class='uppy-Dashboard-Item-name'>pdp.pdf</span><button onclick='window.doneClicks=(window.doneClicks||0)+1'>Done</button><button class='uppy-Dashboard-close' onclick='window.closeClicks=(window.closeClicks||0)+1'>Close</button>";
await OpenDialog(modalFileWithFinish);
await AssertState("Queued file cannot close the modal without upload completion", "FILE_SELECTED", "verifyUpload", "pdp.pdf");
await AssertCount("Queued file does not click Done", "doneClicks", 0);
await AssertCount("Queued file does not click Close", "closeClicks", 0);
await OpenDialog(modalFileWithFinish + "<div class='uppy-StatusBar is-complete'>Upload complete</div>");
await AssertState("Completed modal containing Done and Close is closed", "UPLOAD_DIALOG_CLOSED", "verifyUpload", "pdp.pdf");
await AssertCount("Completed modal prefers Done", "doneClicks", 1);
await AssertCount("Completed modal does not also click Close", "closeClicks", 0);

var scratch = Path.Combine(Path.GetTempPath(), "acose-peer-pdf-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    // All files are test-owned. A header is sufficient for library validation; no customer file is read.
    string MakePdf(string relative, string content = "%PDF-1.7\n% fixture\n%%EOF\n")
    {
        string path = Path.GetFullPath(Path.Combine(scratch, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }
    var uploadPdf = MakePdf("upload/pdp.pdf");
    var cdp = await page.Context.NewCDPSessionAsync(page);
    var chooser = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    cdp.Event("Page.fileChooserOpened").OnEvent += (_, value) => { if (value.HasValue) chooser.TrySetResult(value.Value.Clone()); };
    await cdp.SendAsync("Page.enable");
    await cdp.SendAsync("Page.setInterceptFileChooserDialog", new() { ["enabled"] = true });
    try
    {
        const string onUpload = "window.uploadClicks=(window.uploadClicks||0)+1;let input=document.createElement('input');input.type='file';input.accept='.pdf';input.style.display='none';this.parentElement.append(input);input.onchange=()=>{let a=document.createElement('a');a.href='https://files.example.test/pdp.pdf';a.textContent=input.files[0].name;a.className='uppy-Dashboard-Item-name';this.parentElement.append(a);input.remove();};input.click();";
        await Load(Document(Editing(AddFile(handler: onUpload))));
        await AssertState("Upload action opens lazy Uppy file chooser", "ADD_FILE_CLICKED", "upload");
        JsonElement chooserEvent = await chooser.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!chooserEvent.TryGetProperty("backendNodeId", out var node) || node.GetInt32() <= 0)
            throw new Exception("Native Chromium chooser omitted backendNodeId");
        await cdp.SendAsync("DOM.setFileInputFiles", new() { ["files"] = new[] { uploadPdf }, ["backendNodeId"] = node.GetInt32() });
        await page.GetByText("pdp.pdf", new() { Exact = true }).WaitForAsync();
        if (await page.Locator("input[type=file]").CountAsync() != 0) throw new Exception("Fixture should reset the Uppy file input");
        await AssertState("Upload confirmation survives Uppy removing its file input", "UPLOADED", "verifyUpload", "pdp.pdf");
        string repeatState = await Probe("upload", "pdp.pdf");
        if (repeatState != "UPLOADED") throw new Exception("Existing matching attachment must prevent duplicate upload: " + repeatState);
        await AssertCount("A matching attachment is never uploaded twice", "uploadClicks", 1);
    }
    finally
    {
        await cdp.SendAsync("Page.setInterceptFileChooserDialog", new() { ["enabled"] = false });
        await cdp.DetachAsync();
    }

    const string attachedLink = "<a class='uppy-Dashboard-Item-name' href='https://files.example.test/pdp.pdf'>pdp.pdf</a>";
    await Load(PanelDocument(Editing(attachedLink)));
    await AssertState("Existing attachment without Add File still resumes its editor", "EXISTING_UPLOAD", "open");
    await page.EvaluateAsync("() => { document.querySelector('form button').onclick=()=>{window.submitClicks=(window.submitClicks||0)+1;document.querySelector('form').outerHTML='<p>Submitted on September 16, 2026</p><p>Results pending</p><button>Start new attempt</button>';}; }");
    await AssertState("Submission first confirms the form Honor Code", "HONOR_CONFIRMATION_CLICKED", "submit", "pdp.pdf");
    await AssertCount("Honor confirmation alone is not a submission", "submitClicks", 0);
    await AssertState("Peer Submit without quiz-specific test ID is clicked", "SUBMIT_CLICKED", "submit", "pdp.pdf");
    await AssertState("Direct-submit acceptance is recognized without a confirmation dialog", "SUBMITTED_PENDING_GRADE");
    await AssertState("Accepted attempt cannot be submitted twice", "SUBMITTED_PENDING_GRADE", "submit", "pdp.pdf");
    await AssertCount("Primary submission is clicked exactly once", "submitClicks", 1);
    await Load(PanelDocument(Editing(AddFile())));
    await AssertState("No attachment means no submission", "WAIT", "submit", "pdp.pdf");
    await Load(PanelDocument(Editing(attachedLink).Replace("type='button' onclick='window.submit", "type='button' disabled onclick='window.submit")));
    await Probe("submit", "pdp.pdf");
    await AssertState("Disabled primary waits after Honor Code", "SUBMIT_WAITING", "submit", "pdp.pdf");
    await Load(PanelDocument(Editing(attachedLink) + "<button>Submit</button>"));
    await AssertState("Multiple visible Submit buttons are not guessed", "SUBMIT_AMBIGUOUS", "submit", "pdp.pdf");
    await Load(PanelDocument(Editing(attachedLink)), expectedUrl.Replace("course-a", "course-b"));
    await AssertState("Submit is bound to the original course", "STALE_DOCUMENT", "submit", "pdp.pdf");

    var dialogCdp = await page.Context.NewCDPSessionAsync(page);
    var dialogChooser = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    dialogCdp.Event("Page.fileChooserOpened").OnEvent += (_, value) => { if (value.HasValue) dialogChooser.TrySetResult(value.Value.Clone()); };
    await dialogCdp.SendAsync("Page.enable");
    await dialogCdp.SendAsync("Page.setInterceptFileChooserDialog", new() { ["enabled"] = true });
    try
    {
        await OpenDialog("<button class='uppy-Dashboard-browse'>browse files</button>");
        await page.EvaluateAsync("""
            (() => {
                document.querySelector('.uppy-Dashboard-browse').addEventListener('click', () => {
                    window.browseClicks=(window.browseClicks||0)+1;
                    const input = document.createElement('input'); input.type='file'; input.accept='.pdf'; input.style.display='none';
                    document.querySelector('[role=dialog]').append(input);
                    input.addEventListener('change', () => {
                        const filename = input.files[0].name;
                        input.remove();
                        document.querySelector('.uppy-Dashboard-inner').innerHTML = '<span class="uppy-Dashboard-Item-name">' + filename + '</span><button class="uppy-StatusBar-actionBtn--upload">Upload 1 file</button>';
                        document.querySelector('.uppy-StatusBar-actionBtn--upload').addEventListener('click', event => {
                            window.startUploadClicks=(window.startUploadClicks||0)+1;
                            event.target.remove();
                            const progress=document.createElement('div');progress.setAttribute('role','progressbar');progress.setAttribute('aria-valuenow','25');progress.setAttribute('aria-valuemax','100');
                            progress.textContent='Uploading';document.querySelector('.uppy-Dashboard-inner').append(progress);
                        });
                    });
                    input.click();
                });
            })()
            """);
        await AssertState("Dialog browse opens its lazy native file chooser", "BROWSE_FILES_CLICKED", "chooseFile", "pdp.pdf");
        JsonElement dialogChooserEvent = await dialogChooser.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await dialogCdp.SendAsync("DOM.setFileInputFiles", new() { ["files"] = new[] { uploadPdf }, ["backendNodeId"] = dialogChooserEvent.GetProperty("backendNodeId").GetInt32() });
        await page.GetByText("Upload 1 file", new() { Exact = true }).WaitForAsync();
        await AssertState("A queued modal filename is selected but not yet uploaded", "FILE_SELECTED", "inspect", "pdp.pdf");
        await AssertCount("Inspect cannot start queued upload", "startUploadClicks", 0);
        await AssertState("Queued Uppy file triggers explicit upload action", "UPLOAD_CLICKED", "verifyUpload", "pdp.pdf");
        await AssertCount("Explicit Uppy upload starts once", "startUploadClicks", 1);
        await AssertState("Uppy progress stays pending after selecting the file", "UPLOADING", "verifyUpload", "pdp.pdf");
        await Probe("submit", "pdp.pdf");
        await AssertCount("Uploading dialog prevents background final submission", "submitClicks", 0);
        await page.EvaluateAsync("""
            (() => {
                document.querySelector('[role=dialog]').remove();
                document.querySelector('main').removeAttribute('aria-hidden');
                document.querySelector('form').insertAdjacentHTML('beforeend','<a class="uppy-Dashboard-Item-name" href="https://files.example.test/pdp.pdf">pdp.pdf</a>');
            })()
            """);
        await AssertState("Closed upload dialog resolves to actual form attachment", "UPLOADED", "verifyUpload", "pdp.pdf");
        await Probe("upload", "pdp.pdf");
        await AssertCount("Completed modal upload does not reopen Add File", "uploadClicks", 1);

        await OpenDialog("<input type='file' accept='.pdf' style='display:none'><button class='uppy-Dashboard-browse' onclick='window.browseClicks=(window.browseClicks||0)+1'>browse files</button>");
        await page.EvaluateAsync("""
            (() => {
                document.querySelector('[role=dialog] input').addEventListener('change', event => {
                    window.receivedDialogFile=event.target.files[0].name;
                    document.querySelector('[role=dialog]').remove();
                    document.querySelector('main').removeAttribute('aria-hidden');
                    document.querySelector('form').insertAdjacentHTML('beforeend','<a class="uppy-Dashboard-Item-name" href="https://files.example.test/pdp.pdf">pdp.pdf</a>');
                });
            })()
            """);
        await AssertState("Existing dialog file input is usable while main is aria-hidden", "FILE_INPUT_READY", "chooseFile", "pdp.pdf");
        await AssertCount("Existing dialog input avoids unnecessary browse click", "browseClicks", 0);
        JsonElement dialogDom = (await dialogCdp.SendAsync("DOM.getDocument"))!.Value;
        JsonElement inputNode = (await dialogCdp.SendAsync("DOM.querySelector", new() { ["nodeId"] = dialogDom.GetProperty("root").GetProperty("nodeId").GetInt32(), ["selector"] = "[role=dialog] input[data-acose-peer-upload]" }))!.Value;
        if (inputNode.GetProperty("nodeId").GetInt32() == 0) throw new Exception("Dialog input must be marked for direct CDP upload");
        await dialogCdp.SendAsync("DOM.setFileInputFiles", new() { ["files"] = new[] { uploadPdf }, ["nodeId"] = inputNode.GetProperty("nodeId").GetInt32() });
        await AssertState("Direct CDP dialog upload resolves after modal removal", "UPLOADED", "verifyUpload", "pdp.pdf");
        if (await page.EvaluateAsync<string>("window.receivedDialogFile") != "pdp.pdf") Fail("Dialog file input did not receive expected PDF"); else Pass("Dialog file input receives expected PDF via CDP");
    }
    finally
    {
        await dialogCdp.SendAsync("Page.setInterceptFileChooserDialog", new() { ["enabled"] = false });
        await dialogCdp.DetachAsync();
    }

    void AssertLibrary(string name, string status, string title, string[] roots, string? exactPath = null)
    {
        var result = PreparedPdfLibrary.Find(title, expectedUri, roots);
        if (result.Status != status || (exactPath != null && !string.Equals(result.Path, exactPath, StringComparison.OrdinalIgnoreCase)))
            Fail($"{name}: {result.Status}/{result.Path}, expected {status}/{exactPath}");
        else if (string.IsNullOrWhiteSpace(result.ExpectedDirectory)) Fail(name + ": expected directory is missing");
        else Pass(name);
    }
    string Root(string name) => Path.Combine(scratch, name);
    string titlePdf = MakePdf("title/course-a/Graded Assignment Personal Development Plan (PDP).pdf");
    AssertLibrary("Normalized assignment title matches within its course", "FOUND", assignmentTitle, [Root("title")], titlePdf);
    string slugPdf = MakePdf("slug/course-a/personal-development-plan-pdp.pdf");
    AssertLibrary("Assignment slug filename matches within its course", "FOUND", "Untranslated assignment heading", [Root("slug")], slugPdf);
    string idPdf = MakePdf("id/course-a/P1.pdf");
    AssertLibrary("Explicit peer assignment ID filename matches", "FOUND", "Other title", [Root("id")], idPdf);
    MakePdf("wrong-course/course-b/Graded Assignment Personal Development Plan (PDP).pdf");
    AssertLibrary("Another course's identical title is never selected", "MISSING", assignmentTitle, [Root("wrong-course")]);
    MakePdf("global/Graded Assignment Personal Development Plan (PDP).pdf");
    AssertLibrary("Global unscoped PDF is never selected", "MISSING", assignmentTitle, [Root("global")]);
    MakePdf("invalid/course-a/personal-development-plan-pdp.pdf", "this is not a PDF");
    AssertLibrary("Renamed non-PDF is rejected", "MISSING", assignmentTitle, [Root("invalid")]);
    MakePdf("duplicates/course-a/Personal Development Plan (PDP).pdf");
    MakePdf("duplicates/course-a/Graded Assignment Personal Development Plan (PDP).pdf");
    AssertLibrary("Duplicate assignment matches are ambiguous", "AMBIGUOUS", assignmentTitle, [Root("duplicates")]);
    AssertLibrary("Same directory supplied twice does not fabricate ambiguity", "FOUND", assignmentTitle, [Root("title"), Root("title")], titlePdf);
    AssertLibrary("Distinct matching PDFs across roots are ambiguous", "AMBIGUOUS", assignmentTitle, [Root("title"), Root("slug")]);
    AssertLibrary("Missing library remains actionable", "MISSING", assignmentTitle, [Root("absent")]);
}
finally
{
    // The path is an absolute, unique directory created by this test, never a caller-supplied root.
    Directory.Delete(scratch, recursive: true);
}

Console.WriteLine($"Peer PDF checks: {passed} passed, {failures.Count} failed.");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
