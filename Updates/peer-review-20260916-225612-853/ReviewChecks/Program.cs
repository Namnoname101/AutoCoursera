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
async Task State(string expected, string action = "probe", int? budget = null) =>
    Check((await Probe(action, budget)).State == expected, action + " => " + expected);
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
Check(PeerReviewAutomation.AssignmentPath(new Uri(home + "/id")) == new Uri(assignment).AbsolutePath, "canonical submission path strips review suffix");
Check(PeerReviewAutomation.AssignmentPath(new Uri("https://example.com/learn/local-fixture/peer/ABC/project")) is null, "foreign host rejected");
Check(PeerReviewAutomation.ReviewHome(new Uri(home + "/id"))?.AbsoluteUri == home, "review landing strips individual review id");
Console.WriteLine($"All {checks} review checks passed. No live coursework was accessed.");
