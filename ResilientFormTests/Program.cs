using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace ResilientFormTests;

public sealed class Config
{
    public string Url = Environment.GetEnvironmentVariable("FORM_URL")
                        ?? "https://app.cloudqa.io/home/AutomationPracticeForm";
    public string FirstName = "Alex";
    public string Gender = "Female";
    public string State = null;          // null => first real option in the dropdown
    public bool Headless;
    public int TimeoutSeconds = 15;
    public int PauseMs;                  // slow-down for screen recordings

    public static Config Parse(string[] args)
    {
        var c = new Config();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i]);
            switch (args[i])
            {
                case "--url": c.Url = Next(); break;
                case "--first-name": c.FirstName = Next(); break;
                case "--gender": c.Gender = Next(); break;
                case "--state": c.State = Next(); break;
                case "--timeout": c.TimeoutSeconds = int.Parse(Next()); break;
                case "--pause-ms": c.PauseMs = int.Parse(Next()); break;
                case "--headless": c.Headless = true; break;
                case "-h": case "--help": Usage(); Environment.Exit(0); break;
                default: throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }
        // Allow a plain file path for the local fixtures.
        if (!c.Url.Contains("://") && File.Exists(c.Url)) c.Url = new Uri(Path.GetFullPath(c.Url)).AbsoluteUri;
        else if (!c.Url.Contains("://")) c.Url = "https://" + c.Url;
        // Only web pages or local files may be opened (no javascript:, data:, chrome: ...)
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var u) ||
            (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "file"))
            throw new ArgumentException("--url must be an http(s) URL or a local .html file path");
        if (c.TimeoutSeconds < 1 || c.TimeoutSeconds > 300) throw new ArgumentException("--timeout must be 1-300");
        return c;
    }

    public static void Usage() => Console.WriteLine("""
        Usage: dotnet run -- [options]
          --url <url|path>     Page to test (or env FORM_URL). Default: CloudQA practice form
          --first-name <text>  Value to type          (default: Alex)
          --gender <text>      Radio option to select (default: Female)
          --state <text>       Dropdown option to select (default: first real option)
          --timeout <sec>      How long to wait for fields to appear (default: 15)
          --headless           Run Chrome without a window
          --pause-ms <ms>      Pause between steps (handy for screen recording)
        Exit code: 0 = all three checks passed, 1 = at least one failed, 2 = could not start.
        """);
}

public sealed record FieldResult(string Field, bool Passed, string Detail);

public sealed class FormTester
{
    private readonly IWebDriver _driver;
    private readonly FieldFinder _finder;
    private readonly Config _cfg;

    public FormTester(IWebDriver driver, FieldFinder finder, Config cfg)
    {
        _driver = driver; _finder = finder; _cfg = cfg;
    }

    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    private void Pause() { if (_cfg.PauseMs > 0) Thread.Sleep(_cfg.PauseMs); }

    private void ScrollTo(IWebElement e)
    {
        try { Js.ExecuteScript("arguments[0].scrollIntoView({block:'center'});", e); } catch (WebDriverException) { }
    }

    public FieldResult TestFirstName()
    {
        try
        {
            var c = _finder.FindFirstName();
            Console.WriteLine("  located: " + FieldFinder.Describe(c));
            _finder.Enter(c);
            ScrollTo(c.Element);
            try { c.Element.Clear(); } catch (WebDriverException) { }
            c.Element.SendKeys(_cfg.FirstName);
            string actual = c.Element.GetAttribute("value");
            if (actual != _cfg.FirstName)
            {
                // fallback for inputs that swallow key events
                Js.ExecuteScript(
                    "arguments[0].value = arguments[1];" +
                    "arguments[0].dispatchEvent(new Event('input',{bubbles:true}));" +
                    "arguments[0].dispatchEvent(new Event('change',{bubbles:true}));",
                    c.Element, _cfg.FirstName);
                actual = c.Element.GetAttribute("value");
            }
            Pause();
            return actual == _cfg.FirstName
                ? new FieldResult("First Name", true, $"typed '{actual}'")
                : new FieldResult("First Name", false, $"expected '{_cfg.FirstName}' but field contains '{actual}'");
        }
        catch (Exception e) when (e is WebDriverException || e is FieldNotFoundException)
        {
            return new FieldResult("First Name", false, e.Message);
        }
    }

    public FieldResult TestGender()
    {
        try
        {
            var (target, group) = _finder.FindGender(_cfg.Gender);
            Console.WriteLine($"  located group: [{string.Join(" | ", group.Select(g => g.OptionText))}] -> choosing '{target.OptionText}'");
            _finder.Enter(target);
            ScrollTo(target.Element);
            try
            {
                if (!target.Element.Selected) target.Element.Click();
            }
            catch (WebDriverException)
            {
                // radio hidden behind a styled label, or click intercepted: go through the label / JS
                Js.ExecuteScript(
                    "const e=arguments[0]; if(e.labels && e.labels.length){e.labels[0].click();} else {e.click();}",
                    target.Element);
            }
            if (!target.Element.Selected) Js.ExecuteScript("arguments[0].click();", target.Element);
            Pause();

            int selectedCount = group.Count(g => g.Element.Selected);
            bool ok = target.Element.Selected && selectedCount == 1;
            return ok
                ? new FieldResult("Gender", true, $"selected '{target.OptionText}' (only option checked in group)")
                : new FieldResult("Gender", false,
                    $"'{target.OptionText}' selected={target.Element.Selected}, {selectedCount} option(s) checked in group");
        }
        catch (Exception e) when (e is WebDriverException || e is FieldNotFoundException)
        {
            return new FieldResult("Gender", false, e.Message);
        }
    }

    public FieldResult TestState()
    {
        try
        {
            var c = _finder.FindState();
            Console.WriteLine("  located: " + FieldFinder.Describe(c));
            _finder.Enter(c);
            ScrollTo(c.Element);

            int index = -1;
            if (_cfg.State != null)
            {
                string want = Scorer.Norm(_cfg.State);
                index = c.OptionTexts.FindIndex(t => Scorer.Norm(t) == want);
                if (index < 0) index = c.OptionTexts.FindIndex(t => Scorer.Norm(t).Contains(want));
                if (index < 0)
                    return new FieldResult("State", false,
                        $"option '{_cfg.State}' not found. Options: {string.Join(", ", c.OptionTexts.Take(20))}");
            }
            else
            {
                index = c.OptionTexts.Select((t, i) => (t, i))
                    .Where(x => !Scorer.IsPlaceholderOption(x.t, c.OptionValues[x.i]))
                    .Select(x => x.i).DefaultIfEmpty(-1).First();
                if (index < 0) return new FieldResult("State", false, "dropdown has no selectable options");
            }

            string expected = c.OptionTexts[index];
            var options = c.Element.FindElements(By.TagName("option"));
            try { options[index].Click(); }
            catch (WebDriverException)
            {
                Js.ExecuteScript(
                    "arguments[0].selectedIndex = arguments[1];" +
                    "arguments[0].dispatchEvent(new Event('input',{bubbles:true}));" +
                    "arguments[0].dispatchEvent(new Event('change',{bubbles:true}));",
                    c.Element, index);
            }
            string actual = (string)Js.ExecuteScript(
                "const s=arguments[0]; return s.selectedIndex<0 ? '' : s.options[s.selectedIndex].text.trim();",
                c.Element);
            Pause();
            return actual == expected
                ? new FieldResult("State", true, $"selected '{actual}'")
                : new FieldResult("State", false, $"expected '{expected}' but dropdown shows '{actual}'");
        }
        catch (Exception e) when (e is WebDriverException || e is FieldNotFoundException)
        {
            return new FieldResult("State", false, e.Message);
        }
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        Config cfg;
        try { cfg = Config.Parse(args); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); Config.Usage(); return 2; }

        var options = new ChromeOptions();
        if (cfg.Headless) options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1400,1000");
        options.AddArgument("--disable-notifications");

        IWebDriver driver;
        try { driver = new ChromeDriver(options); }   // Selenium Manager fetches the matching driver
        catch (Exception e) { Console.Error.WriteLine("Could not start Chrome: " + e.Message); return 2; }

        var results = new List<FieldResult>();
        try
        {
            Console.WriteLine("Testing: " + cfg.Url);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);   // never hang on a dead page
            driver.Navigate().GoToUrl(cfg.Url);
            var finder = new FieldFinder(driver, TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            var tester = new FormTester(driver, finder, cfg);

            Console.WriteLine("[1/3] First Name");
            results.Add(tester.TestFirstName());
            Console.WriteLine("[2/3] Gender");
            results.Add(tester.TestGender());
            Console.WriteLine("[3/3] State");
            results.Add(tester.TestState());

            foreach (var w in finder.Warnings) Console.WriteLine("WARNING: " + w);

            if (results.Any(r => !r.Passed))
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                    ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile("failure.png");
                    Console.WriteLine("Saved screenshot: failure.png");
                }
                catch (Exception) { }
            }
        }
        catch (WebDriverException e)
        {
            Console.Error.WriteLine("Browser error: " + e.Message);
            return 2;
        }
        finally
        {
            driver.Quit();
        }

        Console.WriteLine();
        Console.WriteLine("==== RESULTS ====");
        foreach (var r in results)
            Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Field,-11} {r.Detail}");
        bool allPassed = results.All(r => r.Passed);
        Console.WriteLine(allPassed ? "OVERALL: PASS" : "OVERALL: FAIL");
        return allPassed ? 0 : 1;
    }
}
