using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenQA.Selenium;

namespace ResilientFormTests;

/// <summary>Everything we know about one form control, gathered in a single JS pass.</summary>
public sealed class Candidate
{
    public IWebElement Element;
    public int[] FramePath = Array.Empty<int>();   // iframe indexes needed to reach the element
    public string Tag = "", Type = "", Id = "", Name = "", Cls = "", Placeholder = "";
    public string Label = "";        // <label for>, wrapping <label>, aria-label, aria-labelledby, title
    public string OptionText = "";   // for radios: the visible text of this option
    public string Value = "";
    public List<string> Near = new();    // text of nearest ancestors (small -> large)
    public List<string> Before = new();  // text of preceding siblings (small -> large)
    public List<string> OptionTexts = new(), OptionValues = new(); // <select> options
    public bool Visible, Enabled, ReadOnly, Checked, Multiple;
    public int ShadowDepth;
    public double Score;
}

public sealed class FieldNotFoundException : Exception
{
    public FieldNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Locates fields by meaning (visible label / placeholder / surrounding text / control type)
/// instead of by a fixed id, name, class or XPath. Attributes are only a weak tie-breaker.
/// </summary>
public sealed class FieldFinder
{
    private readonly IWebDriver _driver;
    private readonly TimeSpan _timeout;
    private string _diag = "";

    public List<string> Warnings { get; } = new();

    public FieldFinder(IWebDriver driver, TimeSpan timeout)
    {
        _driver = driver;
        _timeout = timeout;
    }

    // ---------------------------------------------------------------- public API

    public Candidate FindFirstName() => Retry<Candidate>("First Name", () =>
    {
        foreach (bool withFrames in new[] { false, true })   // main document first, iframes only as fallback
        {
            var cands = Gather("input", withFrames)
                .Where(c => c.Type == "" || c.Type == "text" || c.Type == "search").ToList();
            var best = Best(cands, Scorer.FirstName, 8, "First Name");
            if (best != null) return best;
        }
        return null;
    });

    /// <summary>Finds the gender radio group and returns the option matching <paramref name="desired"/>.</summary>
    public (Candidate Target, List<Candidate> Group) FindGender(string desired)
    {
        List<Candidate> group = Retry<List<Candidate>>("Gender", () =>
        {
            foreach (bool withFrames in new[] { false, true })
            {
                var radios = Gather("input", withFrames).Where(c => c.Type == "radio").ToList();
                var groups = radios
                    .GroupBy(r => string.Join(",", r.FramePath) + "|" + r.ShadowDepth + "|" +
                                  (r.Name != "" ? "n:" + r.Name : "i:" + r.Id))
                    .Select(g => g.ToList())
                    .Where(g => g.Count >= 2)
                    .ToList();
                List<Candidate> bestGroup = null;
                double bestScore = double.NegativeInfinity;
                var diag = new List<string>();
                foreach (var g in groups)
                {
                    double s = Scorer.GenderGroup(g);
                    diag.Add($"group[{string.Join("/", g.Select(x => x.OptionText))}] score={s:0.#}");
                    if (s > bestScore) { bestScore = s; bestGroup = g; }
                }
                _diag = "Gender: " + string.Join(" ; ", diag);
                if (bestGroup != null && bestScore >= 12) return bestGroup;
            }
            return null;
        });

        var target = Scorer.PickRadio(group, desired);
        if (target == null)
            throw new FieldNotFoundException(
                $"Gender group found, but no option matches '{desired}'. Available: " +
                string.Join(", ", group.Select(g => g.OptionText)));
        return (target, group);
    }

    public Candidate FindState() => Retry<Candidate>("State", () =>
    {
        foreach (bool withFrames in new[] { false, true })
        {
            var cands = Gather("select", withFrames).Where(c => !c.Multiple).ToList();
            var best = Best(cands, Scorer.State, 8, "State");
            if (best != null) return best;
        }
        return null;
    });

    /// <summary>Switch the driver into the frame that contains the candidate (call before interacting).</summary>
    public void Enter(Candidate c)
    {
        _driver.SwitchTo().DefaultContent();
        foreach (int idx in c.FramePath)
        {
            var frames = _driver.FindElements(By.CssSelector("iframe,frame"));
            if (idx >= frames.Count) throw new NoSuchFrameException("frame index " + idx + " disappeared");
            _driver.SwitchTo().Frame(frames[idx]);
        }
    }

    public static string Describe(Candidate c) =>
        $"<{c.Tag}{(c.Type != "" ? " type=" + c.Type : "")}> label='{c.Label}' placeholder='{c.Placeholder}' " +
        $"id='{c.Id}' name='{c.Name}' score={c.Score:0.#}" +
        (c.ShadowDepth > 0 ? $" shadowDepth={c.ShadowDepth}" : "") +
        (c.FramePath.Length > 0 ? $" frame={string.Join("/", c.FramePath)}" : "");

    // ---------------------------------------------------------------- internals

    private T Retry<T>(string what, Func<T> attempt) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var r = attempt();
                if (r != null) return r;
            }
            catch (Exception e) when (e is StaleElementReferenceException || e is NoSuchFrameException
                                      || e is NoSuchElementException)
            {
                // page is still changing; try again
            }
            if (sw.Elapsed > _timeout)
                throw new FieldNotFoundException($"Could not confidently locate the '{what}' field within " +
                                                 $"{_timeout.TotalSeconds:0}s. {_diag}");
            Thread.Sleep(500);
        }
    }

    private Candidate Best(List<Candidate> cands, Func<Candidate, double> scorer, double threshold, string what)
    {
        foreach (var c in cands) c.Score = scorer(c);
        var ranked = cands.Where(c => !double.IsNegativeInfinity(c.Score))
                          .OrderByDescending(c => c.Score).ToList();   // OrderBy is stable: DOM order breaks ties
        _diag = what + " top candidates: " + string.Join(" ; ", ranked.Take(3).Select(Describe));
        if (ranked.Count == 0 || ranked[0].Score < threshold) return null;
        if (ranked.Count > 1 && ranked[1].Score >= threshold && ranked[0].Score - ranked[1].Score < 1.0)
            Warnings.Add($"{what}: ambiguous match, two near-equal candidates. Using the first: {Describe(ranked[0])}");
        return ranked[0];
    }

    /// <summary>Collect candidate controls (including inside open shadow roots and, optionally, iframes).</summary>
    private List<Candidate> Gather(string css, bool withFrames)
    {
        var all = new List<Candidate>();
        var queue = new Queue<int[]>();
        queue.Enqueue(Array.Empty<int>());
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (path.Length > 0 && !withFrames) continue;
            try
            {
                SwitchToPath(path);
                var raw = ((IJavaScriptExecutor)_driver).ExecuteScript(Scripts.Collect, css);
                if (raw is IEnumerable<object> items)
                    foreach (var item in items)
                        if (item is Dictionary<string, object> d) all.Add(ToCandidate(d, path));
                if (withFrames && path.Length < 2)
                {
                    int n = _driver.FindElements(By.CssSelector("iframe,frame")).Count;
                    for (int i = 0; i < n; i++) queue.Enqueue(path.Append(i).ToArray());
                }
            }
            catch (WebDriverException)
            {
                // unreachable frame: skip it
            }
        }
        _driver.SwitchTo().DefaultContent();
        return all;
    }

    private void SwitchToPath(int[] path)
    {
        _driver.SwitchTo().DefaultContent();
        foreach (int idx in path)
        {
            var frames = _driver.FindElements(By.CssSelector("iframe,frame"));
            _driver.SwitchTo().Frame(frames[idx]);
        }
    }

    private static Candidate ToCandidate(Dictionary<string, object> d, int[] path) => new()
    {
        Element = (IWebElement)d["el"],
        FramePath = path,
        Tag = S(d, "tag"), Type = S(d, "type"), Id = S(d, "id"), Name = S(d, "name"), Cls = S(d, "cls"),
        Placeholder = S(d, "placeholder"), Label = S(d, "label"), OptionText = S(d, "optionText"),
        Value = S(d, "value"),
        Near = L(d, "near"), Before = L(d, "before"),
        OptionTexts = L(d, "optionTexts"), OptionValues = L(d, "optionValues"),
        Visible = B(d, "visible"), Enabled = B(d, "enabled"), ReadOnly = B(d, "readOnly"),
        Checked = B(d, "checked"), Multiple = B(d, "multiple"),
        ShadowDepth = d.TryGetValue("depth", out var dep) && dep != null ? Convert.ToInt32(dep) : 0
    };

    private static string S(Dictionary<string, object> d, string k) =>
        d.TryGetValue(k, out var v) && v != null ? v.ToString() : "";

    private static bool B(Dictionary<string, object> d, string k) =>
        d.TryGetValue(k, out var v) && v is bool b && b;

    private static List<string> L(Dictionary<string, object> d, string k)
    {
        var r = new List<string>();
        if (d.TryGetValue(k, out var v) && v is IEnumerable<object> e)
            foreach (var x in e) r.Add(x?.ToString() ?? "");
        return r;
    }
}

/// <summary>Scoring rules. Higher = more likely to be the field we want. -Infinity = excluded.</summary>
internal static class Scorer
{
    private static readonly Regex Camel = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex PlaceholderOpt =
        new(@"^(please )?(select|choose|pick)\b|^-+", RegexOptions.Compiled);

    /// <summary>Lower-case, split camelCase (FirstName -> first name), strip punctuation.</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Camel.Replace(s, "$1 $2");
        return NonAlnum.Replace(s.ToLowerInvariant(), " ").Trim();
    }

    private static string[] Tokens(string norm) =>
        norm.Length == 0 ? Array.Empty<string>() : norm.Split(' ');

    private static bool Has(string norm, string phrase) => (" " + norm + " ").Contains(" " + phrase + " ");

    private static bool HasAny(string norm, params string[] phrases) => phrases.Any(p => Has(norm, p));

    private static double CommonPenalties(Candidate c)
    {
        double p = 0;
        if (!c.Visible) p -= 6;
        if (!c.Enabled || c.ReadOnly) p -= 3;
        p -= c.ShadowDepth;                // prefer the real page over nested/decoy contexts
        p -= c.FramePath.Length * 1.5;
        return p;
    }

    // ---- First Name -------------------------------------------------------

    public static double FirstName(Candidate c)
    {
        string[] phrases = { "first name", "given name", "forename", "fname" };
        string label = Norm(c.Label), ph = Norm(c.Placeholder);
        string before = Norm(c.Before.FirstOrDefault());
        string nearest = Norm(c.Near.FirstOrDefault());
        double s = 0;

        bool labelHit = HasAny(label, phrases);
        if (labelHit)
        {
            s += 10;
            int extra = Tokens(label).Length - 2;           // "First Name *" -> exact; "First Name (Spouse)" -> penalised
            s += extra <= 0 ? 4 : -Math.Min(extra, 4);
        }
        if (HasAny(ph, phrases)) s += 9;
        if (HasAny(before, phrases)) s += 5;                // plain text/label sibling right before the input
        if (HasAny(nearest, phrases)) s += Tokens(nearest).Length <= 3 ? 4 : 2;

        var at = Tokens(Norm(c.Id + " " + c.Name + " " + c.Cls));   // weak signal only
        bool attrHit = (at.Contains("first") || at.Contains("given") || at.Contains("fname")) &&
                       (at.Contains("name") || at.Contains("fname"));
        if (attrHit) s += 3;

        string[] bad = { "last", "surname", "family", "middle", "spouse", "full", "user", "company", "nick" };
        var idText = Tokens(label + " " + ph + " " + before);
        if (!labelHit && idText.Any(t => bad.Contains(t))) s -= 8;

        return s + CommonPenalties(c);
    }

    // ---- Gender -----------------------------------------------------------

    public static double GenderGroup(List<Candidate> g)
    {
        var opts = g.Select(r => Norm(r.OptionText)).ToList();
        bool male = opts.Contains("male"), female = opts.Contains("female");
        double s = 0;
        if (male && female) s += 8;
        else if (opts.Any(o => new[] { "male", "female", "other", "transgender", "non binary", "nonbinary",
                                        "prefer not to say" }.Contains(o))) s += 3;
        if (!male && !female && opts.Any(o => o == "yes" || o == "no")) s -= 10;

        string ctx = Norm(string.Join(" ",
            g.SelectMany(r => r.Before.Take(2)).Concat(g.SelectMany(r => r.Near.Take(3)))));
        var ct = Tokens(ctx);
        if (ct.Contains("gender")) s += 10;
        else if (ct.Contains("sex")) s += 6;

        var at = Tokens(Norm(string.Join(" ", g.Select(r => r.Name + " " + r.Id))));
        if (at.Contains("gender") || at.Contains("sex")) s += 4;

        if (!g.Any(r => r.Visible)) s -= 4;   // custom-styled radios hide the real input; still allowed
        return s;
    }

    /// <summary>Exact option text/value match first ('male' must not match 'female').</summary>
    public static Candidate PickRadio(List<Candidate> g, string desired)
    {
        string want = Norm(desired);
        return g.FirstOrDefault(r => Norm(r.OptionText) == want)
            ?? g.FirstOrDefault(r => Norm(r.Value) == want)
            ?? g.FirstOrDefault(r => Tokens(Norm(r.OptionText)).Contains(want));
    }

    // ---- State ------------------------------------------------------------

    public static double State(Candidate c)
    {
        string label = Norm(c.Label);
        string before = Norm(c.Before.FirstOrDefault());
        string nearest = Norm(c.Near.FirstOrDefault());
        double s = 0;

        var lt = Tokens(label);
        if (lt.Contains("state")) s += 10;
        else if (lt.Contains("province") || lt.Contains("region")) s += 5;
        if (Tokens(before).Contains("state")) s += 6;
        if (Tokens(nearest).Contains("state") && Tokens(nearest).Length <= 3) s += 4;

        var at = Tokens(Norm(c.Id + " " + c.Name + " " + c.Cls));
        if (at.Contains("state") || at.Contains("province")) s += 3;

        // A "country"-labelled select is not the State field.
        if ((lt.Contains("country") || Tokens(before).Contains("country")) && !lt.Contains("state")) s -= 8;

        return s + CommonPenalties(c);
    }

    public static bool IsPlaceholderOption(string text, string value) =>
        string.IsNullOrWhiteSpace(value) || PlaceholderOpt.IsMatch(Norm(text)) || text.TrimStart().StartsWith("--");
}

internal static class Scripts
{
    /// <summary>
    /// arguments[0] = CSS selector. Returns one dictionary per matching element (element in "el"),
    /// walking the main DOM and all open shadow roots.
    /// </summary>
    public const string Collect = """
        const SEL = arguments[0];
        const out = [];
        const txt = n => ((n && n.textContent) || '').replace(/\s+/g, ' ').trim();
        const vis = e => {
          const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
          return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
        };
        function labelText(e) {
          const parts = [];
          const add = t => { t = (t || '').replace(/\s+/g, ' ').trim(); if (t && !parts.includes(t)) parts.push(t); };
          if (e.labels) for (const l of e.labels) add(txt(l));
          const root = e.getRootNode ? e.getRootNode() : document;
          if (e.id && root.querySelectorAll) {
            try { root.querySelectorAll('label[for="' + CSS.escape(e.id) + '"]').forEach(l => add(txt(l))); } catch (x) {}
          }
          add(e.getAttribute('aria-label'));
          const lb = e.getAttribute('aria-labelledby');
          if (lb) lb.split(/\s+/).forEach(id => {
            let n = null; try { n = root.querySelector('#' + CSS.escape(id)); } catch (x) {}
            add(txt(n));
          });
          add(e.getAttribute('title'));
          return parts.join(' | ');
        }
        function optionText(e) {
          const own = labelText(e);
          if (own) return own;
          let n = e.nextSibling;
          while (n && ((n.nodeType === 3 && !n.textContent.trim()) || n.nodeType === 8)) n = n.nextSibling;
          if (n) { const t = txt(n); if (t && t.length < 40) return t; }
          return e.getAttribute('value') || '';
        }
        function context(e) {
          const near = [], before = [];
          let n = e;
          for (let i = 0; i < 4 && n; i++) {
            let ps = n.previousSibling;
            while (ps && ((ps.nodeType === 3 && !ps.textContent.trim()) || ps.nodeType === 8)) ps = ps.previousSibling;
            if (ps) { const t = txt(ps); if (t && t.length < 80) before.push(t); }
            const p = n.parentElement || (n.getRootNode && n.getRootNode().host) || null;
            if (!p) break;
            const pt = txt(p);
            if (pt && pt.length < 160) near.push(pt);
            n = p;
          }
          return { near: near, before: before };
        }
        function push(e, depth) {
          const c = context(e);
          const isSel = e.tagName === 'SELECT';
          out.push({
            el: e, tag: e.tagName.toLowerCase(), type: (e.getAttribute('type') || '').toLowerCase(),
            id: e.id || '', name: e.getAttribute('name') || '', cls: e.getAttribute('class') || '',
            placeholder: e.getAttribute('placeholder') || '', label: labelText(e),
            optionText: optionText(e), value: e.value == null ? '' : String(e.value),
            visible: vis(e), enabled: !e.disabled, readOnly: !!e.readOnly, checked: !!e.checked,
            multiple: !!e.multiple, depth: depth, near: c.near, before: c.before,
            optionTexts: isSel ? Array.from(e.options).map(o => o.text.trim()) : [],
            optionValues: isSel ? Array.from(e.options).map(o => o.value) : []
          });
        }
        function collect(root, depth) {
          root.querySelectorAll(SEL).forEach(e => push(e, depth));
          root.querySelectorAll('*').forEach(h => { if (h.shadowRoot) collect(h.shadowRoot, depth + 1); });
        }
        collect(document, 0);
        return out;
        """;
}
