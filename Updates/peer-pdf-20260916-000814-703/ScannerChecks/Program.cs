using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

// Execute the actual JavaScript embedded in MainWindow, rather than duplicating
// its detection rules in these fixtures.
string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScannerSource.cs.txt"));
string Extract(string signature, string declaration, bool raw = false)
{
    int method = source.IndexOf(signature, StringComparison.Ordinal);
    if (method < 0) throw new Exception("Method missing: " + signature);
    int start = source.IndexOf(declaration, method, StringComparison.Ordinal) + declaration.Length;
    if (start < declaration.Length) throw new Exception("Script missing: " + signature);
    if (raw) return source[start..source.IndexOf("\"\"\"", start, StringComparison.Ordinal)];
    var result = new StringBuilder();
    for (int i = start; i < source.Length; i++)
    {
        if (source[i] != '"') { result.Append(source[i]); continue; }
        if (i + 1 < source.Length && source[i + 1] == '"') { result.Append('"'); i++; continue; }
        return result.ToString();
    }
    throw new Exception("Unterminated script: " + signature);
}
string lessonsScript = Extract("private async Task CheckLessonsAsync(", "string jsCode = @\"");
string modulesScript = Extract("private async Task CheckModulesAsync()", "string jsCode = @\"");
string progressScript = Extract("private async Task<CourseProgressSnapshot?> ReadCourseProgressSnapshotAsync()", "string script = \"\"\"", true);
string Build(string script, int[]? scanned = null, string[]? pending = null, string[]? discussions = null, string[]? passedPeers = null) => script
    .Replace("__SKIP_GRADED__", "true").Replace("__SKIP_PRACTICE__", "true")
    .Replace("__SKIP_PEER__", "true")
    .Replace("__PENDING_RESULT_PATHS__", JsonSerializer.Serialize(pending ?? []))
    .Replace("__SUBMITTED_DISCUSSION_PATHS__", JsonSerializer.Serialize(discussions ?? []))
    .Replace("__SKIPPED_LAUNCH_APP_PATHS__", "[]")
    .Replace("__VERIFIED_PASSED_PEER_PATHS__", JsonSerializer.Serialize(passedPeers ?? []))
    .Replace("__AUTOMATION_COMPLETED_MODULES__", JsonSerializer.Serialize(scanned ?? []));

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
var page = await browser.NewPageAsync();
string html = "";
await page.RouteAsync("**/*", route => route.FulfillAsync(new()
{
    Status = 200, ContentType = "text/html", Body = "<html><body>" + html + "</body></html>"
}));
const string root = "/learn/scanner-fixture";
const string peer = root + "/peer/ABC/personal-development-plan";
string Module(int number, bool selected = false, bool complete = true) =>
    $"<a data-testid='rc-WeekNavigationItem' aria-label='Module {number}{(complete ? " Completed" : "")}' {(selected ? "aria-current='page'" : "")} href='{root}/home/week/{number}' onclick='event.preventDefault();window.clickedModule={number}'>Module {number}</a>";
string Lesson(string path, string label, bool icon = false, string outside = "") =>
    $"<li data-testid='WeekSingleItemDisplay-fixture'><a data-click-key='open_course_home.period_page.click.item_link' href='{path}' onclick='event.preventDefault();window.clickedLesson=this.getAttribute(\"href\")'><span>{(icon ? "<svg></svg>" : "<span>Type icon</span>")}</span><span data-test='rc-ItemName'>{label}</span></a>{outside}</li>";
async Task Load(string body, int week = 1)
{
    html = body;
    await page.GotoAsync("https://www.coursera.org" + root + "/home/week/" + week);
}
int passed = 0;
void Verify(bool condition, string name, string? detail = null)
{
    if (!condition) throw new Exception(name + ": " + detail);
    passed++;
    Console.WriteLine("PASS " + name);
}
async Task<string> Scan(int[]? scanned = null, string[]? pending = null, string[]? discussions = null) =>
    await page.EvaluateAsync<string>(Build(lessonsScript, scanned, pending, discussions));
async Task Clicked(string name, string expected)
{
    string actual = await page.EvaluateAsync<string>("window.clickedLesson || ''");
    Verify(actual == expected, name, actual);
}

await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment", true, "<button>Resume</button>"));
await Scan(); await Clicked("Resume outside anchor overrides green icon", peer);
await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment · Results pending", true));
await Scan(); await Clicked("Results pending peer reaches submission inspection", peer);
await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment · Submitted", true));
await Scan(); await Clicked("Submitted peer reaches submission inspection", peer);
foreach (string status in new[] { "Failed", "Not passed", "Try again", "Retry" })
{
    await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment", true, "<span>" + status + "</span>"));
    await Scan(); await Clicked(status + " overrides completion icon", peer);
}
await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment"));
await Scan(); await Clicked("Fresh peer assignment is no longer skipped", peer);
await Load(Module(1, true) + Lesson(peer + "-review", "Peer-reviewed Assignment"));
await Scan(); await Clicked("Assignment slug containing review is still a submission", peer + "-review");
foreach (string action in new[] { "review", "reviews", "give-feedback" })
{
    await Load(Module(1, true) + Lesson(peer + "/" + action, "Peer assessment") + Lesson(peer, "Peer-graded Assignment"));
    await Scan(); await Clicked(action + " action skipped while submission is opened", peer);
}
await Load(Module(1, true) + Lesson(peer, "Review Your Peers") + Lesson(root + "/lecture/next", "Next lesson"));
await Scan(); await Clicked("Review Your Peers label stays manual", root + "/lecture/next");
await Load(Module(1, true) + Lesson(peer + "/review", "Review Your Peers", true));
string completedReview = await Scan();
Verify(completedReview.Contains("🏆") && !completedReview.Contains("Peer/Review"), "Completed review is not reported as skipped", completedReview);

await Load(Module(1, true) + Lesson(root + "/quiz/pending?tracking=1", "Results pending") + Lesson(peer, "Peer-graded Assignment"));
await Scan(pending: [root + "/quiz/pending"]); await Clicked("Pending quiz recovery stays skipped", peer);
await Load(Module(1, true) + Lesson(root + "/discussion/sent", "Submitted discussion") + Lesson(peer, "Peer-graded Assignment"));
await Scan(discussions: [root + "/discussion/sent"]); await Clicked("Submitted discussion recovery stays skipped", peer);
await Load(Module(1, true) + Lesson(root + "/gradedLti/item", "Graded App Item") + Lesson(peer, "Peer-graded Assignment"));
await Scan(); await Clicked("Launch App skip policy remains intact", peer);

await Load(Module(1) + Module(2, true), 2);
string modules = await page.EvaluateAsync<string>(Build(modulesScript));
Verify(modules.StartsWith("MODULE_CLICKED|1|"), "All green badges still require an initial lesson sweep", modules);
await Load(Module(1, true) + Module(2) + Lesson(root + "/lecture/done", "Completed lesson", true));
string next = await Scan();
Verify(next.StartsWith("MODULE_SCANNED|1|NEXT_MODULE|"), "Next green module is verified instead of ending early", next);
await Load(Module(1) + Module(2, true) + Lesson(root + "/lecture/done", "Completed lesson", true), 2);
string wrapped = await Scan();
Verify(wrapped.StartsWith("MODULE_SCANNED|2|NEXT_MODULE|"), "Sweep revisits earlier unverified green modules", wrapped);
await Load(Module(1) + Module(2, true) + Lesson(root + "/lecture/done", "Completed lesson", true), 2);
string finalModule = await Scan(scanned: [1]);
Verify(finalModule.Contains("🏆") && !finalModule.Contains("NEXT_MODULE"), "Course completes after every module is scanned", finalModule);
await Load(Module(1, true));
Verify(await Scan() == "LESSONS_NOT_FOUND", "Missing lesson DOM does not complete the course");
await Load(Module(1, true) + Module(2) + Lesson(peer, "Peer-graded Assignment · Results pending", true));
string progress = await page.EvaluateAsync<string>(Build(progressScript));
Verify(JsonDocument.Parse(progress).RootElement.GetProperty("Progress").GetInt32() < 100,
    "Green badges and submitted peer do not report 100%", progress);
await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment · Submitted", true, "<button>Resume</button>") + Lesson(root + "/lecture/next", "Next lesson"));
await page.EvaluateAsync<string>(Build(lessonsScript, passedPeers: [peer]));
await Clicked("A verified passed peer is not reopened because of Resume", root + "/lecture/next");
await Load(Module(1, true) + Lesson(peer, "Peer-graded Assignment · Submitted", true, "<button>Resume</button>"));
string passedProgress = await page.EvaluateAsync<string>(Build(progressScript, passedPeers: [peer]));
Verify(JsonDocument.Parse(passedProgress).RootElement.GetProperty("Progress").GetInt32() == 100,
    "Verified passed peer counts as completed in live progress", passedProgress);
Console.WriteLine($"All {passed} scanner checks passed.");
