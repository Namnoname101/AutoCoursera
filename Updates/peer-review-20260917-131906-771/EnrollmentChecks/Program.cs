using System.Text.Json;
using AutomationPlatform.Presentation.Services;
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = Environment.GetEnvironmentVariable("ACOSE_TEST_BROWSER") ?? "msedge", Headless = true,
    Args = ["--headless=new"], IgnoreDefaultArgs = ["--headless=old", "--unsafely-disable-devtools-self-xss-warnings"] });
var page = await browser.NewPageAsync();
const string url = "https://www.coursera.org/learn/enrollment-fixture";
string html = "";
await page.RouteAsync("**/*", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<html><body>" + html + "</body></html>" }));
int passed = 0;
string button(string text) => $"<button onclick='window.clicks=(window.clicks||0)+1'>{text}</button>";
string org(string extra = "") => "<div role='dialog'><h1>Enroll through my organization</h1><p>This course is available in the learning program created by your organization, FPT University.</p>" + extra + button("Continue enrolling") + "</div>";
async Task Load(string body) { html = body; await page.GotoAsync(url); }
async Task<string> Probe(bool allow = true, string expected = url)
{
    string value = await page.EvaluateAsync<string>(CourseEnrollmentAutomation.BuildProbe(new Uri(expected), allow));
    return JsonDocument.Parse(value).RootElement.GetProperty("State").GetString()!;
}
async Task Assert(string name, string expected, int clicks)
{
    string actual = await Probe();
    int count = await page.EvaluateAsync<int>("window.clicks || 0");
    if (actual != expected || count != clicks) throw new Exception($"{name}: state {actual} (wanted {expected}), clicks {count} (wanted {clicks})");
    Console.WriteLine("PASS " + name); passed++;
}
await Load("<aside><a>Get Coursera Plus</a></aside><main><a href='/learn/enrollment-fixture/home/welcome'>Go to course</a></main>");
await Assert("Already enrolled enters course despite advertising", "OPEN_HREF", 0);
await Load(button("Enroll Starts Sep 15") + "<p>Included in your organization's plan</p><a>Get Coursera Plus</a>");
await Assert("Organization course enrollment with dated CTA", "ENROLL_CLICKED", 1);
await Assert("Initial CTA is not clicked twice", "WAIT", 1);
await Load(org());
await Assert("Actual FPT organization confirmation", "CONFIRM_CLICKED", 1);
await Assert("Confirmation is not resubmitted", "WAIT", 1);
await page.EvaluateAsync("document.body.innerHTML = " + JsonSerializer.Serialize(org()));
await Assert("React replacement does not resubmit confirmation", "WAIT", 1);
await Load("<div id='rc-TopLevelModal'>" + org() + "</div>");
await Assert("Nested modal wrappers are one dialog", "CONFIRM_CLICKED", 1);
await Load("<button>Continue</button><main>Course information</main>");
await Assert("Unrelated Continue is untouched", "NOT_FOUND", 0);
await Load("<div role='dialog'><p>Welcome</p>" + button("Continue") + "</div>" + button("Enroll"));
await Assert("Unknown dialog prevents background enrollment", "CONFIRMATION_REQUIRED", 0);
await Load(org("<p>Start free trial. Subscription billed $49 per month.</p>"));
await Assert("Trial confirmation is untouched", "PAID_OR_TRIAL", 0);
await Load("<div role='dialog'><p>Enroll for free. Credit card required after 7 day trial.</p>" + button("Enroll for free") + "</div>");
await Assert("Free label does not conceal trial", "PAID_OR_TRIAL", 0);
await Load("<div role='dialog'><p>This course is free</p>" + button("Enroll for free") + "</div>");
await Assert("Explicit free confirmation", "CONFIRM_CLICKED", 1);
await Load("<div role='button' aria-label='Enroll Starts Sep 15'>" + button("Enroll Starts Sep 15") + "</div>");
await Assert("Nested role wrapper clicks the real button", "ENROLL_CLICKED", 1);
await Load(button("Enroll") + button("Enroll"));
await Assert("Identical responsive CTAs click once", "ENROLL_CLICKED", 1);
await Load(button("Enroll") + button("Join for free"));
await Assert("Different enrollment choices are ambiguous", "ENROLL_AMBIGUOUS", 0);
await Load("<a href='/learn/some-other-course/home'>Go to course</a><a href='https://example.com'>Enroll</a>");
await Assert("Other course and external links are untouched", "NOT_FOUND", 0);
await Load("<div aria-hidden='true'>" + button("Enroll") + "</div><button disabled>Enroll</button><aside>" + button("Enroll") + "</aside>");
await Assert("Hidden disabled and sidebar controls are untouched", "NOT_FOUND", 0);
await Load(org() + org());
await Assert("Multiple dialogs are ambiguous", "ENROLL_AMBIGUOUS", 0);
await Load(button("Enroll"));
if (await Probe(false) != "WAIT" || await page.EvaluateAsync<int>("window.clicks || 0") != 0) throw new Exception("Disallowed enrollment start");
Console.WriteLine("PASS Enrollment start can be disabled"); passed++;
if (await Probe(true, url + "?old=1") != "STALE_DOCUMENT") throw new Exception("Stale document guard");
Console.WriteLine("PASS Stale document cannot act"); passed++;
await Load("<a data-e2e='go_to_course_button' href='/learn/enrollment-fixture/home/welcome'>Open learning</a>");
await Assert("Legacy course-entry attribute stays supported", "OPEN_HREF", 0);
await Load(button("Start free trial"));
await Assert("Paid landing reports the gate", "PAID_OR_TRIAL", 0);
await Load(button("Enroll Starts Sep 15"));
await Assert("New document starts enrollment", "ENROLL_CLICKED", 1);
await page.EvaluateAsync("document.body.innerHTML = " + JsonSerializer.Serialize(org()));
await Assert("Same page modal is handled after Enroll", "CONFIRM_CLICKED", 2);
await Load(button("Enroll Starts Sep 15") + "<p>Access provided by FPT University</p>");
await Assert("Organization switch reload allows next Enroll", "ENROLL_CLICKED", 1);
await page.EvaluateAsync("document.body.innerHTML = \"<a href='/learn/enrollment-fixture/home/welcome'>Go to course</a>\"");
await Assert("Enrollment resolves to the course entry", "OPEN_HREF", 1);
const string programUrl = "https://www.coursera.org/programs/fptu-fall-2026/learn/enrollment-fixture";
html = "<a href='/learn/enrollment-fixture/home/welcome'>Go to course</a>";
await page.GotoAsync(programUrl);
if (await Probe(true, programUrl) != "OPEN_HREF") throw new Exception("Program course route must open same course home");
Console.WriteLine("PASS Organization program course route is supported"); passed++;
Console.WriteLine($"All {passed} enrollment checks passed.");
