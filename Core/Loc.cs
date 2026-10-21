using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;

namespace ArdUi;

// Interface language: Chinese (the source language of every message in the code) or English. English is produced from a table of the
// Chinese source texts (Loc.Strings.cs): an exact match first, then templates for interpolated texts ("{0}" stands for a value), then
// phrase by phrase. A hook translates what is put on screen (text, tooltips, watermarks, window titles), so the code keeps writing
// Chinese and nothing has to be wrapped. Text that belongs to the user or the other side (chat messages, names) is marked with Loc.Keep.
static partial class Loc
{
    public static bool English { get; private set; }

    static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    static readonly Regex Cjk = new(@"[　-〿一-鿿＀-￯]", RegexOptions.Compiled);
    static readonly ConcurrentDictionary<string, string> Cache = new();
    static Dictionary<string, string> exact = new();
    static List<(Regex Whole, string En)> templates = new();
    static List<(string Zh, Regex? Part, string En)> phrases = new();

    // "auto" follows the Windows display language; "zh" and "en" are explicit.
    public static bool Resolve(string? preference) => (Environment.GetEnvironmentVariable("ARDUI_LANG") ?? preference) switch
    {
        "zh" => false,
        "en" => true,
        _ => !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase),
    };

    public static void Init(string? preference) => Init(Resolve(preference));

    public static void Init(bool english)
    {
        English = english; Cache.Clear();
        if (!english) return;
        exact = new(); templates = new(); phrases = new();
        foreach (var (zh, en) in Table)
        {
            if (!Placeholder.IsMatch(zh)) { exact[zh] = en; phrases.Add((zh, null, en)); continue; }
            var pieces = Placeholder.Split(zh).Select(Regex.Escape).ToArray();
            templates.Add((new Regex("^" + string.Join("(.*?)", pieces) + "$", RegexOptions.Singleline | RegexOptions.Compiled), en));
            phrases.Add((zh, new Regex(string.Join(@"(\S+?)", pieces), RegexOptions.Singleline | RegexOptions.Compiled), en));
        }
        phrases = phrases.OrderByDescending(p => p.Zh.Length).ToList();
        Install();
    }

    public static bool NeedsTranslation(string? text) => English && !string.IsNullOrEmpty(text) && Cjk.IsMatch(text);

    // The text in the interface language. Anything without Chinese in it, and everything in Chinese mode, is returned as it is.
    public static string T(string text)
    {
        if (!NeedsTranslation(text)) return text;
        return Cache.GetOrAdd(text, Translate);
    }

    static string Translate(string text)
    {
        if (exact.TryGetValue(text, out var hit)) return hit;
        foreach (var (whole, en) in templates)
        {
            var match = whole.Match(text);
            if (match.Success) return Fill(en, match);
        }
        var result = text;
        foreach (var (zh, part, en) in phrases)
        {
            if (part == null) { if (result.Contains(zh, StringComparison.Ordinal)) result = result.Replace(zh, en, StringComparison.Ordinal); }
            else result = part.Replace(result, m => Fill(en, m));
        }
        return Punctuation(result);
    }

    static string Fill(string en, Match match)
    {
        var filled = Regex.Replace(en, @"\{(\d+)\}", m => int.TryParse(m.Groups[1].Value, out var n) && n + 1 < match.Groups.Count ? T(match.Groups[n + 1].Value) : m.Value);
        return Punctuation(filled);
    }

    // Chinese punctuation that survives in an English sentence.
    static string Punctuation(string text)
    {
        if (!Cjk.IsMatch(text)) return text;
        var s = text.Replace("：", ": ").Replace("，", ", ").Replace("、", ", ").Replace("；", "; ").Replace("（", " (").Replace("）", ")").Replace("。", ". ")
            .Replace("？", "?").Replace("！", "!").Replace("“", "\"").Replace("”", "\"").Replace("　", " ");
        return Regex.Replace(s, @" +([,.;:?!)])", "$1").TrimEnd();
    }

    // ---- the hook ---------------------------------------------------------------------------------------------------------------
    // Marks text that must never be translated: what the user typed and what the other side sent.
    public static readonly AttachedProperty<bool> KeepProperty = AvaloniaProperty.RegisterAttached<TextBlock, TextBlock, bool>("Keep");
    public static readonly AttachedProperty<bool> KeepRunProperty = AvaloniaProperty.RegisterAttached<Run, Run, bool>("Keep");
    public static TextBlock Keep(TextBlock block) { block.SetValue(KeepProperty, true); return block; }
    static bool installed;

    // Call once after Avalonia has started and before the first window is created.
    public static void Install()
    {
        if (!English || installed) return;
        installed = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (e.NewValue is string text && NeedsTranslation(text) && !block.GetValue(KeepProperty)) block.Text = T(text);
        });
        Run.TextProperty.Changed.AddClassHandler<Run>((run, e) => { if (e.NewValue is string text && NeedsTranslation(text) && !run.GetValue(KeepRunProperty)) run.Text = T(text); });
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((box, e) => { if (e.NewValue is string text && NeedsTranslation(text)) box.Watermark = T(text); });
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) => { if (e.NewValue is string text && NeedsTranslation(text)) ToolTip.SetTip(control, T(text)); });
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) => { if (e.NewValue is string text && NeedsTranslation(text)) window.Title = T(text); });
    }
}
