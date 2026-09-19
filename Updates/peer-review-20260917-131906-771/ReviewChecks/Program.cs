using System.Text.Json;
using AutomationPlatform.Presentation.Services;
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new()
{
    Channel = Environment.GetEnvironmentVariable("ACOSE_TEST_BROWSER") ?? "chrome", Headless = true,
    Args = ["--headless=new"], IgnoreDefaultArgs = ["--headless=old", "--unsafely-disable-devtools-self-xss-warnings"]
});
var page = await browser.NewPageAsync();
const string assignment = "https://www.coursera.org/learn/local-fixture/peer/ABC/project";
const string home = assignment + "/give-feedback";
string html = "";
await page.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = html }));
int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task Load(string body, string? url = null)
{
    html = "<!doctype html><html><body>" + body + "</body></html>";
    await page.GotoAsync(url ?? home);
}
async Task<PeerReviewPageState> Probe(string action = "probe", int? budget = null, string? expected = null) =>
    JsonSerializer.Deserialize<PeerReviewPageState>(await page.EvaluateAsync<string>(
        PeerReviewAutomation.BuildProbe(new Uri(expected ?? home), action, budget)))!;
async Task State(string expected, string action = "probe", int? budget = null)
{
    var actual = await Probe(action, budget);
    // Filling deliberately yields between individual DOM mutations so React can
    // commit controlled values and replace the controls before the next probe.
    // Do not retry terminal failure assertions or accidentally resubmit a review.
    if (expected == "FORM_FILLED" && action == "fill")
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (actual.State == "FORM_FILLING" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            actual = await Probe(action, budget);
        }
    }
    Check(actual.State == expected, action + " => " + expected +
        (actual.State == expected ? "" : "; actual " + actual.State + ": " + actual.Detail));
}
string Counter(int count) => $"<div data-testid='review-count'><h2>Reviews</h2>{count} left to complete</div>";
string Rubric(string extra = "", string submit = "", bool count = true) =>
    "<main>" + (count ? Counter(4) : "") + "<form onsubmit='event.preventDefault()'><fieldset><legend>Clarity</legend>" +
    "<label><input type='radio' name='clarity' value='a'>1 point: brief</label>" +
    "<label><input type='radio' name='clarity' value='b'>5 points: clear</label>" +
    "<label><input type='radio' name='clarity' value='c'>3 points: partial</label></fieldset>" +
    "<fieldset><legend>Evidence</legend><label><input type='radio' name='evidence' value='x'>10 points</label>" +
    "<label><input type='radio' name='evidence' value='y'>0 points</label></fieldset>" +
    "<label>Comments<textarea required></textarea></label>" + extra +
    "<button type='button' " + (submit.Length > 0 ? submit : "onclick='window.submits=(window.submits||0)+1'") + ">Submit review</button></form></main>";
// Structural fixture from the current Coursera DOM: no main/form/fieldset,
// grouped radio names, a file artifact, and an autosize measurement textarea.
string CourseraRubric(string? artifact = "submission-one.pdf?signature=first", int count = 4, string submit = "") =>
    "<textarea id='outside-peer'>Outside peer content</textarea>" +
    "<div class='rc-PeerItemPage'>" + Counter(count) +
    "<textarea id='outside-review'>Outside review form</textarea><div class='rc-SubmittedSubmissionView'>" +
    (artifact is null ? "" : "<div class='rc-SubmissionPartView'><a href='https://files.example.test/artifacts/" + artifact + "'>Download learner PDF</a></div>") +
    "<div><label for='real-low' id='real-low-label'>1 point: brief</label><input id='real-low' type='radio' name='peer-options-clarity' value='low' aria-labelledby='real-low-label'>" +
    "<label for='real-high' id='real-high-label'>5 points: clear</label><input id='real-high' type='radio' name='peer-options-clarity' value='high' aria-labelledby='real-high-label'></div>" +
    "<div><label for='evidence-high'>10 points</label><input id='evidence-high' type='radio' name='peer-options-evidence' value='high'>" +
    "<label for='evidence-low'>0 points</label><input id='evidence-low' type='radio' name='peer-options-evidence' value='low'></div>" +
    "<label for='real-comment'>Comments</label><textarea id='real-comment' required></textarea>" +
    "<textarea id='autosize' readonly aria-hidden='true' tabindex='-1' style='visibility:hidden;position:absolute;height:0'>Measurement only</textarea>" +
    "<div class='rc-FormSubmit'><button type='button' " +
    (submit.Length > 0 ? submit : "onclick='window.submits=(window.submits||0)+1'") +
    ">Submit Review</button></div></div></div>";
const string reviewNext = assignment + "/review-next";

foreach (int n in new[] { 4, 3, 2, 1, 0 })
{
    await Load("<main>" + Counter(n) + "<button onclick='window.started=true'>Start reviewing</button></main>");
    var p = await Probe();
    Check(p.Remaining == n, $"exact user counter: Reviews {n} left to complete");
    Check(p.State == (n == 0 ? "REVIEWS_COMPLETE" : "START_READY"), "counter completion classification " + n);
    if (n == 0)
    {
        await State("REVIEWS_COMPLETE", "start");
        Check(!await page.EvaluateAsync<bool>("!!window.started"), "zero remaining never starts optional extra review");
    }
}
foreach (string text in new[] { "1 of 3 reviews completed", "Reviewed 1 of 3 assignments", "Reviews completed: 1/3", "1/3 reviews" })
{
    await Load("<main><div data-testid='review-count'>" + text + "</div><button>Start reviewing</button></main>");
    var p = await Probe();
    Check(p.Remaining == 2 && p.Completed == 1 && p.Required == 3, "fraction counter: " + text);
}
await Load("<main><h2>Reviews</h2><p>4 left to complete</p><button>Start reviewing</button></main>");
Check((await Probe()).Remaining == 4, "split heading and plain counter without test id");
await Load("<main><p>Reviews</p><button onclick='window.started=true'>Start reviewing</button></main>");
await State("COUNT_REQUIRED", "start");
Check(!await page.EvaluateAsync<bool>("!!window.started"), "unknown count does not start more reviews");
await Load("<main>" + Counter(2) + "No submissions available. Check back later.</main>");
await State("NO_REVIEWS_AVAILABLE");
await Load("<main><article>Reviews 0 left to complete. You have completed all required peer reviews.</article><button>Start reviewing</button></main>");
Check((await Probe()).Remaining is null && (await Probe()).State != "REVIEWS_COMPLETE", "student artifact cannot declare completion");
await Load("<main><div hidden>Reviews 0 left to complete</div>" + Counter(3) + "<button>Start reviewing</button></main>");
Check((await Probe()).Remaining == 3, "hidden zero counter ignored");

await Load(Rubric(), home + "/student1");
await State("FORM_READY");
await State("INCOMPLETE_FORM", "submit");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "cannot submit before filling");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<string>("document.querySelector('[name=clarity]:checked').value") == "b", "highest points is middle option");
Check(await page.EvaluateAsync<string>("document.querySelector('[name=evidence]:checked').value") == "x", "highest points can be first option");
Check(await page.EvaluateAsync<string>("document.querySelector('textarea').value") == "Thank you for sharing your work.", "default comment filled");
await State("SUBMIT_READY");
var firstKey = (await Probe()).ReviewKey;
Check(firstKey.Length > 0, "review has stable nonempty identity");
await State("SUBMIT_CLICKED", "submit");
await State("SUBMIT_PENDING", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "exactly one submit per review");
await page.EvaluateAsync("document.querySelector('form').outerHTML = document.querySelector('form').outerHTML");
await State("SUBMIT_PENDING", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "rerender same form never sends twice");

await Load(Rubric(submit: "disabled onclick='window.submits=1'"));
await page.EvaluateAsync("document.querySelector('textarea').addEventListener('input',()=>document.querySelector('button').disabled=false)");
await State("FORM_FILLED", "fill");
await State("SUBMIT_CLICKED", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "disabled submit does not prevent initial rubric discovery");
await Load(Rubric());
await page.EvaluateAsync("document.querySelector('textarea').value='Existing thoughtful comment'");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<string>("document.querySelector('textarea').value") == "Existing thoughtful comment", "existing comment preserved");
await Load(Rubric("<label>I certify my identity<textarea required></textarea></label>"));
await State("INCOMPLETE_FORM", "fill");
Check(await page.EvaluateAsync<int>("document.querySelectorAll(':checked').length") == 0, "identity fields are not fabricated");
await Load(Rubric("<label><input type='checkbox' required>I agree to the honor code</label>"));
await State("INCOMPLETE_FORM", "fill");
Check(!await page.EvaluateAsync<bool>("document.querySelector('[type=checkbox]').checked"), "honor declaration untouched");

await Load(Rubric().Replace("5 points: clear", "3 points: clear"));
await State("UNSUPPORTED_RUBRIC", "fill");
await Load(Rubric().Replace("5 points: clear", "Excellent"));
await State("UNSUPPORTED_RUBRIC", "fill");
await Load(Rubric().Replace("5 points: clear", "5 points (from 10 points)"));
await State("UNSUPPORTED_RUBRIC", "fill");
await Load(Rubric().Replace("1 point: brief", "0.5 points").Replace("5 points: clear", "2,5 points").Replace("3 points: partial", "1.5 points"));
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<string>("document.querySelector('[name=clarity]:checked').value") == "b", "decimal scores are numeric");
await Load("<main>" + Counter(2) + "<form><div role='radiogroup'><div role='radio' tabindex='0' aria-checked='false' onclick='pick(this)'>2 points</div>" +
    "<div role='radio' tabindex='0' aria-checked='false' onclick='pick(this)'>8 points</div></div><div contenteditable='true' role='textbox' aria-label='Comment'></div>" +
    "<button type='button'>Submit review</button></form></main><script>function pick(el){for(const e of el.parentElement.children)e.setAttribute('aria-checked',String(e===el))}</script>");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<string>("document.querySelector('[aria-checked=true]').textContent") == "8 points", "custom radio selects maximum");
Check(await page.EvaluateAsync<string>("document.querySelector('[contenteditable]').textContent") == "Thank you for sharing your work.", "contenteditable comment filled");
await Load(Rubric(count: false));
await State("COUNT_REQUIRED", "fill");
await State("FORM_FILLED", "fill", 2);
await Load(Rubric(), assignment);
await State("STALE_DOCUMENT", "fill");
await Load(Rubric(), "https://www.coursera.org/learn/foreign-course/peer/ABC/project/give-feedback");
await State("STALE_DOCUMENT", "fill");
await Load(Rubric() + "<div role='dialog'>Confirm identity</div>");
await State("BLOCKING_DIALOG", "fill");

string reviewLanding = "<main>" + Counter(4) + "<button onclick='window.starts=(window.starts||0)+1'>Start Reviewing</button></main>";
foreach (var dialog in new[] {
    ("empty live bai dialog", "<div role='dialog' class='bai-styleReset-b718e393 bai-StyleReset' style='width:100%;height:0'></div>"),
    ("zero width dialog", "<div role='dialog' style='width:0;height:40px'></div>"),
    ("opacity zero dialog", "<div role='dialog' style='opacity:0'>Not painted</div>"),
    ("opacity zero ancestor", "<div style='opacity:0'><div role='dialog'>Not painted</div></div>"),
    ("visibility hidden dialog", "<div role='dialog' style='visibility:hidden'>Not painted</div>"),
    ("display none dialog", "<div role='dialog' style='display:none'>Not painted</div>"),
    ("offscreen dialog", "<div role='dialog' style='position:fixed;top:-10000px;left:10px;width:100px;height:40px'>Not in viewport</div>") })
{
    await Load(dialog.Item2 + reviewLanding);
    Check((await Probe()).State == "START_READY", dialog.Item1 + " does not block review landing");
    await State("START_CLICKED", "start");
    Check(await page.EvaluateAsync<int>("window.starts") == 1, dialog.Item1 + " allows Start Reviewing exactly once");
}
foreach (var dialog in new[] {
    ("visible dialog", "<div role='dialog'>Confirm identity</div>"),
    ("visible alert dialog", "<div role='alertdialog'>Important confirmation</div>"),
    ("visible aria-modal", "<div aria-modal='true'>Modal confirmation</div>"),
    ("visible child overflowing zero-area dialog", "<div role='dialog' style='width:0;height:0'><div style='position:fixed;left:20px;top:20px;width:100px;height:40px'>Confirmation</div></div>") })
{
    await Load(dialog.Item2 + reviewLanding);
    await State("BLOCKING_DIALOG", "start");
    Check(!await page.EvaluateAsync<bool>("!!window.starts"), dialog.Item1 + " keeps background Start Reviewing untouched");
}

await Load(CourseraRubric(), reviewNext);
await State("FORM_READY");
var realBefore = await Probe();
Check(realBefore.GroupCount == 2 && realBefore.Remaining == 4, "real DOM named radio groups and counter recognized without main/form");
Check(!realBefore.ReviewKey.Contains("submission-one.pdf") && !realBefore.ReviewKey.Contains("signature="),
    "review identity does not expose artifact path or signed query");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<int>("document.querySelectorAll('input[value=high]:checked').length") == 2,
    "real DOM numeric labels select each maximum");
Check(await page.Locator("#real-comment").InputValueAsync() == "Thank you for sharing your work.", "real review textarea receives comment");
Check(await page.Locator("#autosize").InputValueAsync() == "Measurement only", "hidden readonly autosize textarea stays unchanged");
Check(await page.Locator("#outside-peer").InputValueAsync() == "Outside peer content", "textarea outside peer content stays unchanged");
Check(await page.Locator("#outside-review").InputValueAsync() == "Outside review form", "textarea outside submitted review form stays unchanged");
await State("SUBMIT_READY");
await State("SUBMIT_CLICKED", "submit");
await State("SUBMIT_PENDING", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "real DOM review is submitted at most once");
await Load(CourseraRubric("submission-one.pdf?signature=renewed&expires=tomorrow"), reviewNext);
Check((await Probe()).ReviewKey == realBefore.ReviewKey, "renewed signed query preserves review identity across navigation");
await Load(CourseraRubric("submission-two.pdf?signature=first"), reviewNext);
Check((await Probe()).ReviewKey != realBefore.ReviewKey, "distinct artifact has distinct identity on shared review-next route");
await Load(CourseraRubric(null), reviewNext);
await State("AMBIGUOUS_FORM", "fill");
Check(await page.EvaluateAsync<int>("document.querySelectorAll(':checked').length") == 0, "unidentified shared-route review never receives scores");
Check(await page.Locator("#real-comment").InputValueAsync() == "", "unidentified shared-route review never receives comments");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "unidentified shared-route review never submits");

// React can replace every rubric control during each change handler. A fill
// action must stop after a single mutation and inspect freshly rendered nodes
// on the next tick instead of clicking or verifying detached references.
await Load(CourseraRubric(), reviewNext);
await page.EvaluateAsync("""
    () => {
        const form = document.querySelector('.rc-SubmittedSubmissionView');
        const template = form.innerHTML;
        const saved = { comment: '' };
        window.detachedMutations = 0;
        window.renderMutations = 0;
        function render() {
            form.innerHTML = template;
            for (const radio of form.querySelectorAll('input[type=radio]')) {
                radio.checked = saved[radio.name] === radio.value;
                radio.addEventListener('change', event => {
                    if (!event.currentTarget.isConnected) { window.detachedMutations++; return; }
                    saved[radio.name] = radio.value;
                    window.renderMutations++;
                    render();
                });
            }
            const editor = form.querySelector('#real-comment');
            editor.value = saved.comment;
            editor.addEventListener('input', event => {
                if (!event.currentTarget.isConnected) { window.detachedMutations++; return; }
                saved.comment = event.currentTarget.value;
                window.renderMutations++;
                render();
            });
        }
        render();
    }
    """);
var firstIncremental = await Probe("fill");
Check(firstIncremental.State == "FORM_FILLING", "initial rubric waits for layout to settle before success");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "initial fill never submits while form is settling");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<int>("window.detachedMutations") == 0, "incremental fill never mutates detached rerendered controls");
Check(await page.EvaluateAsync<int>("window.renderMutations") == 3, "rerendered two-group form performs exactly two selections and one comment mutation");
Check(await page.EvaluateAsync<int>("document.querySelectorAll('input[value=high]:checked').length") == 2,
    "both maximum selections persist in newly rendered DOM");
Check(await page.Locator("#real-comment").InputValueAsync() == "Thank you for sharing your work.",
    "comment survives its own synchronous whole-form rerender");
await State("SUBMIT_READY");
await State("SUBMIT_CLICKED", "submit");
await State("SUBMIT_PENDING", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "rerendered form still submits exactly once after live verification");

// Controlled/custom radios may acknowledge clicks in a later event-loop turn.
// Keep the delay above one production polling interval to catch eager retries.
await Load("<main>" + Counter(2) +
    "<form><div role='radiogroup'><div role='radio' aria-checked='false' onclick='delayedPick(this)'>2 points</div>" +
    "<div role='radio' aria-checked='false' onclick='delayedPick(this)'>8 points</div></div>" +
    "<label>Comment<textarea required></textarea></label><button type='button' onclick='window.submits=(window.submits||0)+1'>Submit review</button></form></main>" +
    "<script>function delayedPick(el){window.radioClicks=(window.radioClicks||0)+1;setTimeout(()=>{for(const e of el.parentElement.children)e.setAttribute('aria-checked',String(e===el))},600)}</script>");
await State("FORM_FILLED", "fill");
Check(await page.EvaluateAsync<string>("document.querySelector('[aria-checked=true]').textContent") == "8 points",
    "delayed controlled radio eventually acknowledges maximum score");
Check(await page.EvaluateAsync<int>("window.radioClicks") == 1, "pending radio acknowledgement is not clicked again prematurely");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "delayed controlled values do not trigger an early submit");
await State("SUBMIT_READY");

// Some forms enable Submit only after asynchronous server/client validation.
await Load(Rubric(submit: "disabled onclick='window.submits=(window.submits||0)+1'"));
await page.EvaluateAsync("""
    () => {
        document.querySelector('textarea').addEventListener('input', () => {
            setTimeout(() => document.querySelector('button').disabled = false, 1500);
        });
    }
    """);
var disabledSubmit = await Probe("submit");
Check(disabledSubmit.State == "FORM_FILLING", "disabled submit is a recoverable settling state rather than immediate form failure");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "disabled submit cannot be activated during validation");
await State("FORM_FILLED", "fill");
var enableDeadline = DateTime.UtcNow.AddSeconds(5);
while (await page.Locator("button").IsDisabledAsync() && DateTime.UtcNow < enableDeadline) await Task.Delay(50);
Check(!await page.Locator("button").IsDisabledAsync(), "delayed review validation eventually enables submit");
await State("SUBMIT_CLICKED", "submit");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "submit happens only after delayed validation enables the control");

// A permanently rejected click must stop after a finite retry budget, with no
// review sent and no false FORM_FILLED/SUBMIT_READY state.
await Load("<main>" + Counter(2) +
    "<form><div role='radiogroup'><div role='radio' aria-checked='false' onclick='window.rejectedClicks=(window.rejectedClicks||0)+1'>1 point</div>" +
    "<div role='radio' aria-checked='false' onclick='window.rejectedClicks=(window.rejectedClicks||0)+1'>9 points</div></div>" +
    "<button type='button' onclick='window.submits=(window.submits||0)+1'>Submit review</button></form></main>");
var rejected = await Probe("fill");
var rejectionDeadline = DateTime.UtcNow.AddSeconds(15);
while (rejected.State == "FORM_FILLING" && DateTime.UtcNow < rejectionDeadline)
{
    await Task.Delay(50);
    rejected = await Probe("fill");
}
Check(rejected.State == "INCOMPLETE_FORM", "permanently rejected radio reaches a bounded actionable failure");
Check(await page.EvaluateAsync<int>("window.rejectedClicks||0") == 3, "permanently rejected radio gets at most three click attempts");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "rejected radio never sends an incomplete review");

// The shared review-next route can display a different artifact without a full
// page load. A previously filled marker must not authorize that new review.
await Load(CourseraRubric(), reviewNext);
await State("FORM_FILLED", "fill");
var filledArtifact = await Probe();
await page.EvaluateAsync("document.querySelector('.rc-SubmissionPartView a').href='https://files.example.test/artifacts/new-learner.pdf?signature=new'");
var changedArtifact = await Probe();
Check(changedArtifact.ReviewKey != filledArtifact.ReviewKey, "artifact replacement changes review identity without navigation");
Check(changedArtifact.State != "SUBMIT_READY", "new artifact invalidates prior filled review authorization");
var changedSubmit = await Probe("submit");
Check(changedSubmit.State != "SUBMIT_CLICKED", "artifact replacement cannot reuse the prior review's submit readiness");
Check(!await page.EvaluateAsync<bool>("!!window.submits"), "no review is sent during artifact identity transition");

// The user-reported 4-left flow: accept one review, observe the server counter,
// then open a distinct review; zero prevents a fifth submission.
int accepted = 0;
var keys = new HashSet<string>();
for (int remaining = 4; remaining > 0; remaining--)
{
    int after = remaining - 1;
    string fixture = Rubric(submit: "onclick='window.submits=(window.submits||0)+1;document.querySelector(\"main\").innerHTML=\"<h2>Reviews</h2><div data-testid=review-count>" + after + " left to complete</div><p>Your review has been submitted</p><button>Review another assignment</button>\"'")
        .Replace("4 left to complete", remaining + " left to complete");
    await Load(fixture, home + "/student" + remaining);
    Check((await Probe()).Remaining == remaining, "flow counter before " + remaining);
    await State("FORM_FILLED", "fill");
    var ready = await Probe();
    Check(keys.Add(ready.ReviewKey), "next review has distinct key " + remaining);
    await State("SUBMIT_CLICKED", "submit");
    var done = await Probe();
    Check(done.Remaining == after, "accepted review decreases remaining to " + after);
    Check(after == 0 ? done.State == "REVIEWS_COMPLETE" : done.SubmissionConfirmed, "review acceptance is observed " + remaining);
    accepted += await page.EvaluateAsync<int>("window.submits");
}
Check(accepted == 4, "four required reviews submitted, not five");
await State("REVIEWS_COMPLETE", "next");
Check(await page.EvaluateAsync<int>("window.submits") == 1, "zero-left page no further action");

// The production /review-next pathname is shared; artifacts, not route or rubric,
// identify the four distinct reviews across navigation back to this same URL.
accepted = 0;
keys.Clear();
for (int remaining = 4; remaining > 0; remaining--)
{
    int after = remaining - 1;
    string submit = "onclick='window.submits=(window.submits||0)+1;document.querySelector(\".rc-PeerItemPage\").innerHTML=\"<h2>Reviews</h2><div data-testid=review-count>" +
        after + " left to complete</div><p>Your review has been submitted</p><button onclick=window.extraReview=true>Review another assignment</button>\"'";
    await Load(CourseraRubric("learner-" + remaining + ".pdf?signature=temporary", remaining, submit), reviewNext);
    var before = await Probe();
    Check(before.Remaining == remaining, "shared route counter before " + remaining);
    Check(keys.Add(before.ReviewKey), "shared route artifact identifies distinct review " + remaining);
    await State("FORM_FILLED", "fill");
    await State("SUBMIT_CLICKED", "submit");
    var done = await Probe();
    Check(done.Remaining == after, "shared route accepted review decreases counter to " + after);
    Check(after == 0 ? done.State == "REVIEWS_COMPLETE" : done.SubmissionConfirmed,
        "shared route review acceptance observed " + remaining);
    accepted += await page.EvaluateAsync<int>("window.submits");
}
Check(accepted == 4, "shared route submits four required reviews only");
await State("REVIEWS_COMPLETE", "next");
Check(!await page.EvaluateAsync<bool>("!!window.extraReview"), "shared route zero-left blocks optional fifth review");
Check(PeerReviewAutomation.AssignmentPath(new Uri(home + "/id")) == new Uri(assignment).AbsolutePath, "canonical submission path strips review suffix");
Check(PeerReviewAutomation.AssignmentPath(new Uri("https://example.com/learn/local-fixture/peer/ABC/project")) is null, "foreign host rejected");
Check(PeerReviewAutomation.ReviewHome(new Uri(home + "/id"))?.AbsoluteUri == home, "review landing strips individual review id");
Check(PeerReviewAutomation.ReviewHome(new Uri(reviewNext))?.AbsoluteUri == home,
    "review-next returns to give-feedback rather than claiming another learner");
Console.WriteLine($"All {checks} review checks passed. No live coursework was accessed.");
