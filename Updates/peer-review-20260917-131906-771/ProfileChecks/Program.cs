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
string fixture = "";
await page.RouteAsync("**/*", route => route.FulfillAsync(new()
{
    Status = 200, ContentType = "text/html", Body = "<html><body>" + fixture + "</body></html>"
}));
int passed = 0;
async Task Check(string name, string body, string user, string expectedId, string expectedName,
    string path = "/account-settings")
{
    fixture = body;
    await page.GotoAsync("https://www.coursera.org" + path);
    await page.EvaluateAsync("window.coursera = { user: " + user + " }; window.changes=0; " +
        "document.addEventListener('input',()=>window.changes++); " +
        "document.addEventListener('change',()=>window.changes++); " +
        "document.addEventListener('click',()=>window.changes++);");
    string before = await page.ContentAsync();
    string value = await page.EvaluateAsync<string>(CourseraProfileIdentityProbe.Script);
    using var result = JsonDocument.Parse(value);
    string id = result.RootElement.GetProperty("userId").GetString()!;
    string fullName = result.RootElement.GetProperty("fullName").GetString()!;
    if (id != expectedId || fullName != expectedName) throw new Exception($"{name}: unexpected identity");
    if (await page.ContentAsync() != before || await page.EvaluateAsync<int>("window.changes") != 0)
        throw new Exception(name + ": reader mutated page or dispatched an action");
    passed++;
    Console.WriteLine("PASS " + name);
}

await Check("Authenticated global identity", "", "{id:123,full_name:'Test Learner'}", "123", "Test Learner");
await Check("Camel-case global name", "", "{id:'123',fullName:'Test Learner'}", "123", "Test Learner");
await Check("Global identity works on course route", "", "{id:'123',full_name:'Test Learner'}",
    "123", "Test Learner", "/learn/fixture/home/welcome");
await Check("Header profile accessible name", "<button data-e2e='header-profile' aria-label='User dropdown menu for Test Learner'></button>",
    "{id:'123'}", "123", "Test Learner");
await Check("Visible account Full name label", "<label for='name'>Full name</label><input id='name' value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner");
await Check("Account field via aria-labelledby", "<span id='label'>Full name</span><input aria-labelledby='label' value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner");
await Check("Read-only account field", "<input aria-label='Full name' readonly value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner");
await Check("Whitespace and NFC normalize", "<input aria-label='Full name' value='  Te&#769;st&nbsp;  Learner  '>",
    "{id:'123'}", "123", "Tést Learner");
await Check("Name does not invent account ID", "<input aria-label='Full name' value='Test Learner'>", "{}", "", "Test Learner");
await Check("Email never substitutes account ID", "<input aria-label='Email address' value='example@example.test'>", "{}", "", "");
await Check("Unrelated course name field ignored", "<input aria-label='Full name' value='Wrong Name'>", "{id:'123'}",
    "123", "", "/learn/fixture/peer/abc/submission");
await Check("Ambiguous account name inputs ignored", "<input aria-label='Full name' value='First'><input aria-label='Full name' value='Second'>",
    "{id:'123'}", "123", "");
await Check("Hidden clone ignored", "<div style='display:none'><input aria-label='Full name' value='Wrong'></div><input aria-label='Full name' value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner");
await Check("Zero-area clone ignored", "<input aria-label='Full name' style='width:0;height:0;border:0;padding:0' value='Wrong'><input aria-label='Full name' value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner");
await Check("Aria-hidden ancestor ignored", "<div aria-hidden='true'><input aria-label='Full name' value='Wrong'></div>",
    "{id:'123'}", "123", "");
await Check("Invisible ancestor ignored", "<div style='opacity:0'><input aria-label='Full name' value='Wrong'></div>",
    "{id:'123'}", "123", "");
await Check("Trailing slash profile accepted", "<input aria-label='Full name' value='Test Learner'>",
    "{id:'123'}", "123", "Test Learner", "/account-settings/");
await Check("Authenticated name takes priority over field", "<input aria-label='Full name' value='Unsaved Edit'>",
    "{id:'123',full_name:'Test Learner'}", "123", "Test Learner");
foreach (string invalidId in new[] { "", " ", "abc\u0001", new string('a', 201) })
{
    if (CourseraProfileIdentityCache.TryNormalizeUserId(invalidId) != null) throw new Exception("Invalid ID accepted");
    passed++;
}
foreach (string invalidName in new[] { "", "profile", "account", "example@example.test", "1", "abc\u0001" })
{
    if (CourseraProfileIdentityCache.TryNormalizeFullName(invalidName) != null) throw new Exception("Invalid name accepted");
    passed++;
}
Console.WriteLine($"Profile checks passed: {passed}");
