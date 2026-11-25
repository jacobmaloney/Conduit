using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit.Mapping;

/// <summary>
/// THE value engine: one token grammar for every value either product builds from other values. It serves
/// IdentityCenter's Management Policies, Management Definitions and workflow steps, and the attribute
/// mappings of a sync project (see <see cref="MappingRowExpression"/>). Host-neutral and pure: the caller
/// supplies the attribute bags.
/// <list type="bullet">
/// <item><c>{Prop}</c> the whole value of an attribute; <c>{Prop:N}</c> its first N characters;
/// <c>{Prop:N,X}</c> first N characters padded with X when shorter; <c>{Prop:-N}</c> its last N characters
/// (<c>{Prop:-N,X}</c> padded on the left).</item>
/// <item><c>{FirstInitial}</c> / <c>{LastInitial}</c> keep working as aliases of <c>{FirstName:1}</c> / <c>{LastName:1}</c>.</item>
/// <item><c>{#}</c> a uniqueness number added only when the value is in use; <c>{#:always}</c> always;
/// <c>{#:3}</c> fixed width with leading zeroes; options combine (<c>{#:always,3}</c>).</item>
/// <item><c>{Date:yyyyMMdd}</c> the operation time in the given .NET format.</item>
/// <item><c>{Initiator.Prop}</c> an attribute of the person who initiated the operation.</item>
/// <item><c>{Org.EmailDomain}</c> / <c>{Org.UpnSuffix}</c> the organisation naming settings.</item>
/// <item>UC015-D02 function tokens, nestable: <c>{upper(LastName)}</c>, <c>lower</c>, <c>proper</c>, <c>trim</c>,
/// <c>{concat(FirstName, " ", LastName)}</c>, <c>{replace(Email, "@old.com", "@new.com")}</c>,
/// <c>{regex(Email, "@.*$", "")}</c>, <c>{substring(EmployeeId, 0, 4)}</c>, <c>{first(MobilePhone, PrimaryPhone)}</c>,
/// <c>{lookup(Department, "Sales=S;Engineering=E", "X")}</c>, <c>{if(Department, Department, "Unassigned")}</c>,
/// <c>{if(equals(Country, "US"), "+1", "")}</c>, <c>{date(Now, "+30d", "yyyy-MM-dd")}</c>. Arguments are attribute
/// names (also <c>Org.X</c>, <c>Initiator.X</c>, <c>Now</c>), quoted text or numbers. A function that cannot be
/// parsed, names an attribute that does not exist, or fails (a bad regex, a bad date) reports the whole token as
/// unknown; one that renders nothing reports it as empty. Side-effect free: no scripts, no lookups outside the bags.</item>
/// </list>
/// Pure and synchronous: the caller supplies the attribute bags and tests each candidate for uniqueness.
/// </summary>
public static class ValuePatternEngine
{
    private static readonly Regex TokenRegex = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private const char CounterMarker = '\u0001';

    /// <summary>The most candidates <see cref="Candidate"/> is asked for before a caller gives up.</summary>
    public const int MaxCandidates = 100;

    /// <summary>Everything a pattern may read. Attribute lookups are case-insensitive.</summary>
    public sealed record PatternContext(
        IReadOnlyDictionary<string, string?> Attributes,
        IReadOnlyDictionary<string, string?>? Organization = null,
        IReadOnlyDictionary<string, string?>? Initiator = null,
        DateTime? Now = null);

    /// <summary>Post-processing a rule applies to the rendered value.</summary>
    public sealed record PatternOptions(
        string Case = "None",
        int? MaxLength = null,
        string? RestrictedCharacters = null,
        bool StripDiacritics = false,
        bool CollapseWhitespace = true)
    {
        public const string CaseNone = "None";
        public const string CaseLower = "Lower";
        public const string CaseUpper = "Upper";

        public static readonly PatternOptions Default = new();
    }

    /// <summary>
    /// A rendered pattern. <see cref="EmptyTokens"/> names tokens that resolved to nothing (an unset
    /// organisation domain, a blank first name); <see cref="UnknownTokens"/> names tokens the grammar
    /// could not resolve at all. The caller decides whether either makes the value unusable.
    /// </summary>
    public sealed record Rendering(
        string Value,
        IReadOnlyList<string> EmptyTokens,
        IReadOnlyList<string> UnknownTokens,
        bool HasCounter)
    {
        public bool IsComplete => EmptyTokens.Count == 0 && UnknownTokens.Count == 0;
    }

    /// <summary>True when the pattern carries a uniqueness number token.</summary>
    public static bool HasCounter(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && TokenRegex.Matches(pattern).Any(m => IsCounterToken(m.Groups[1].Value));

    /// <summary>The attribute names a pattern reads (identity attributes only; Org/Initiator/Date excluded).</summary>
    public static IReadOnlySet<string> SourceAttributes(string? pattern)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(pattern)) return set;
        foreach (Match m in TokenRegex.Matches(pattern))
        {
            var body = m.Groups[1].Value.Trim();
            if (body.IndexOf('(') >= 0) { Expressions.CollectAttributeNames(body, set); continue; }
            if (IsCounterToken(body) || body.StartsWith("Date:", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("Org.", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("Initiator.", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = body.Split(':')[0].Trim();
            if (name.Equals("FirstInitial", StringComparison.OrdinalIgnoreCase)) name = "FirstName";
            else if (name.Equals("LastInitial", StringComparison.OrdinalIgnoreCase)) name = "LastName";
            set.Add(name);
        }
        return set;
    }

    /// <summary>
    /// Renders candidate <paramref name="index"/> of the pattern: index 0 is the value without a
    /// conflict number (or with number 1 when the counter is <c>always</c>); each further index adds
    /// one to the number. A pattern without a counter has only candidate 0; later indexes return it unchanged.
    /// <see cref="PatternOptions.MaxLength"/> is honoured by shortening the text before the number so the
    /// number always fits.
    /// </summary>
    public static Rendering Candidate(string pattern, PatternContext context, PatternOptions? options, int index)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(context);
        options ??= PatternOptions.Default;
        if (index < 0) index = 0;

        var empty = new List<string>();
        var unknown = new List<string>();
        var counterAlways = false;
        var counterWidth = 0;
        var hasCounter = false;

        var rendered = TokenRegex.Replace(pattern, m =>
        {
            var body = m.Groups[1].Value.Trim();
            if (IsCounterToken(body))
            {
                hasCounter = true;
                ParseCounterOptions(body, ref counterAlways, ref counterWidth);
                return CounterMarker.ToString();
            }
            if (body.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
            {
                var format = body["Date:".Length..].Trim();
                var now = context.Now ?? DateTime.UtcNow;
                try { return now.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture); }
                catch (FormatException) { unknown.Add(m.Value); return string.Empty; }
            }
            if (body.StartsWith("Org.", StringComparison.OrdinalIgnoreCase))
                return Lookup(context.Organization, body["Org.".Length..], m.Value, empty, unknown, allowUnknown: true);
            if (body.StartsWith("Initiator.", StringComparison.OrdinalIgnoreCase))
                return Lookup(context.Initiator, body["Initiator.".Length..], m.Value, empty, unknown, allowUnknown: true);

            // {fn(args)}: a function token (UC015-D02).
            if (body.IndexOf('(') >= 0)
                return Expressions.Evaluate(body, context, m.Value, empty, unknown);

            // {Prop}, {Prop:N}, {Prop:N,X}, {FirstInitial}, {LastInitial}
            var parts = body.Split(':', 2);
            var name = parts[0].Trim();
            int? take = null;
            char? fill = null;
            if (name.Equals("FirstInitial", StringComparison.OrdinalIgnoreCase)) { name = "FirstName"; take = 1; }
            else if (name.Equals("LastInitial", StringComparison.OrdinalIgnoreCase)) { name = "LastName"; take = 1; }
            if (parts.Length == 2)
            {
                var spec = parts[1].Split(',', 2);
                if (int.TryParse(spec[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0) take = n;
                else { unknown.Add(m.Value); return string.Empty; }
                if (spec.Length == 2 && spec[1].Length > 0) fill = spec[1][0];
            }
            if (!TryGet(context.Attributes, name, out var value)) { unknown.Add(m.Value); return string.Empty; }
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) { empty.Add(m.Value); return string.Empty; }
            if (take is int t && t > 0)
            {
                if (value.Length >= t) value = value[..t];
                else if (fill is char f) value = value.PadRight(t, f);
            }
            else if (take is int last && last < 0)
            {
                // {Prop:-N}: the last N characters; a shorter value is padded on the left when a fill is given.
                var count = -last;
                if (value.Length >= count) value = value[^count..];
                else if (fill is char f) value = value.PadLeft(count, f);
            }
            return value;
        });

        var counterText = string.Empty;
        if (hasCounter)
        {
            var number = counterAlways ? index + 1 : index;
            if (number > 0)
                counterText = counterWidth > 0
                    ? number.ToString(CultureInfo.InvariantCulture).PadLeft(counterWidth, '0')
                    : number.ToString(CultureInfo.InvariantCulture);
        }

        var markerAt = rendered.IndexOf(CounterMarker);
        string head, tail;
        if (markerAt >= 0) { head = rendered[..markerAt]; tail = rendered[(markerAt + 1)..].Replace(CounterMarker.ToString(), string.Empty); }
        else { head = rendered; tail = string.Empty; }

        head = PostProcess(head, options);
        tail = PostProcess(tail, options);

        if (options.MaxLength is int max && max > 0)
        {
            var room = max - counterText.Length - tail.Length;
            if (room < 0) room = 0;
            if (head.Length > room) head = head[..room];
        }

        var value = string.Concat(head, counterText, tail);
        if (options.CollapseWhitespace) value = value.Trim();
        return new Rendering(value, empty, unknown, hasCounter);
    }

    /// <summary>
    /// The first candidate <paramref name="isInUse"/> accepts, starting at <paramref name="startIndex"/>
    /// (the Generate button passes the number of values already shown), or null when
    /// <see cref="MaxCandidates"/> candidates are all in use. A pattern without a counter has one
    /// candidate: it is returned when free, otherwise null.
    /// </summary>
    public static async Task<Rendering?> FirstFreeAsync(
        string pattern, PatternContext context, PatternOptions? options,
        Func<string, Task<bool>> isInUse, int startIndex = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isInUse);
        var first = Candidate(pattern, context, options, startIndex);
        if (!first.HasCounter)
            return await isInUse(first.Value) ? null : first;
        for (var i = startIndex; i < startIndex + MaxCandidates; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = i == startIndex ? first : Candidate(pattern, context, options, i);
            if (candidate.Value.Length == 0) return candidate; // nothing to test; the caller reads EmptyTokens
            if (!await isInUse(candidate.Value)) return candidate;
        }
        return null;
    }

    /// <summary>Case, restricted characters, diacritics and whitespace, in that order of harm.</summary>
    public static string PostProcess(string value, PatternOptions options)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = value;
        if (options.StripDiacritics) text = RemoveDiacritics(text);
        if (!string.IsNullOrEmpty(options.RestrictedCharacters))
        {
            var restricted = options.RestrictedCharacters;
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) if (restricted.IndexOf(c) < 0) sb.Append(c);
            text = sb.ToString();
        }
        if (options.CollapseWhitespace) text = Regex.Replace(text, @"\s{2,}", " ");
        text = options.Case switch
        {
            PatternOptions.CaseLower => text.ToLowerInvariant(),
            PatternOptions.CaseUpper => text.ToUpperInvariant(),
            _ => text,
        };
        return text;
    }

    private static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsCounterToken(string body) => body == "#" || body.StartsWith("#:", StringComparison.Ordinal);

    private static void ParseCounterOptions(string body, ref bool always, ref int width)
    {
        if (!body.StartsWith("#:", StringComparison.Ordinal)) return;
        foreach (var raw in body[2..].Split(','))
        {
            var opt = raw.Trim();
            if (opt.Equals("always", StringComparison.OrdinalIgnoreCase)) always = true;
            else if (opt.Equals("optional", StringComparison.OrdinalIgnoreCase)) always = false;
            else if (int.TryParse(opt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 && w <= 9) width = w;
        }
    }

    private static string Lookup(IReadOnlyDictionary<string, string?>? bag, string key, string token,
        List<string> empty, List<string> unknown, bool allowUnknown)
    {
        key = key.Trim();
        if (bag is null || !TryGet(bag, key, out var value))
        {
            if (allowUnknown) { empty.Add(token); return string.Empty; }
            unknown.Add(token); return string.Empty;
        }
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0) { empty.Add(token); return string.Empty; }
        return value;
    }

    private static bool TryGet(IReadOnlyDictionary<string, string?> bag, string key, out string? value)
    {
        if (bag.TryGetValue(key, out value)) return true;
        foreach (var kv in bag)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }

    /// <summary>The functions a token may call (UC015-D02), for help text and the editor's completion.</summary>
    public static readonly IReadOnlyList<string> Functions = new[]
    {
        "upper", "lower", "proper", "trim", "concat", "replace", "regex", "substring", "first", "lookup", "if", "equals", "date",
    };

    /// <summary>
    /// The function grammar inside one token: <c>name(arg, ...)</c> where an argument is an attribute name,
    /// quoted text (<c>"..."</c> or <c>'...'</c>, backslash escapes the quote), an integer, or another call.
    /// Pure: it reads the context bags and nothing else. Every failure is a <see cref="FormatException"/> that
    /// <see cref="Evaluate"/> turns into an unknown token, so a bad expression can never write a partial value.
    /// </summary>
    private static class Expressions
    {
        private enum Kind { Identifier, Text, Call }

        private sealed record Node(Kind Kind, string Text, IReadOnlyList<Node> Args);

        private static readonly IReadOnlyList<Node> NoArgs = Array.Empty<Node>();
        private static readonly Regex OffsetRegex = new(@"^([+-]\d{1,5})\s*([dmyDMY])$", RegexOptions.Compiled);
        private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

        public static string Evaluate(string body, PatternContext context, string token, List<string> empty, List<string> unknown)
        {
            Node ast;
            try { ast = Parse(body); }
            catch (FormatException) { unknown.Add(token); return string.Empty; }

            var unknownNames = new List<string>();
            string value;
            try { value = Eval(ast, context, unknownNames); }
            catch (FormatException) { unknown.Add(token); return string.Empty; }
            if (unknownNames.Count > 0) { unknown.Add(token); return string.Empty; }

            value = value.Trim();
            if (value.Length == 0) { empty.Add(token); return string.Empty; }
            return value;
        }

        /// <summary>The identity attribute names an expression reads (Org, Initiator and Now excluded).</summary>
        public static void CollectAttributeNames(string body, ISet<string> into)
        {
            Node ast;
            try { ast = Parse(body); }
            catch (FormatException) { return; }
            Walk(ast);

            void Walk(Node n)
            {
                if (n.Kind == Kind.Identifier && !IsSpecialName(n.Text)) into.Add(n.Text);
                foreach (var a in n.Args) Walk(a);
            }
        }

        private static bool IsSpecialName(string name) =>
            name.Equals("Now", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Org.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Initiator.", StringComparison.OrdinalIgnoreCase);

        // ---- parser -------------------------------------------------------------------------------------

        private static Node Parse(string body)
        {
            var pos = 0;
            var node = ParseExpr(body, ref pos);
            SkipSpace(body, ref pos);
            if (pos != body.Length) throw new FormatException("Unexpected text after the expression.");
            return node;
        }

        private static Node ParseExpr(string s, ref int pos)
        {
            SkipSpace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("The expression ends early.");
            var c = s[pos];
            if (c == '"' || c == '\'') return new Node(Kind.Text, ParseString(s, ref pos), NoArgs);
            if (char.IsDigit(c) || (c == '-' && pos + 1 < s.Length && char.IsDigit(s[pos + 1])))
            {
                var start = pos;
                pos++;
                while (pos < s.Length && char.IsDigit(s[pos])) pos++;
                return new Node(Kind.Text, s[start..pos], NoArgs);
            }
            if (!(char.IsLetter(c) || c == '_')) throw new FormatException($"Unexpected character '{c}'.");
            var nameStart = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '.')) pos++;
            var name = s[nameStart..pos];
            SkipSpace(s, ref pos);
            if (pos >= s.Length || s[pos] != '(') return new Node(Kind.Identifier, name, NoArgs);

            pos++;
            var args = new List<Node>();
            SkipSpace(s, ref pos);
            if (pos < s.Length && s[pos] == ')') { pos++; return new Node(Kind.Call, name, args); }
            while (true)
            {
                args.Add(ParseExpr(s, ref pos));
                SkipSpace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("A closing parenthesis is missing.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ')') { pos++; break; }
                throw new FormatException($"Expected ',' or ')' but found '{s[pos]}'.");
            }
            return new Node(Kind.Call, name, args);
        }

        private static string ParseString(string s, ref int pos)
        {
            var quote = s[pos++];
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                var c = s[pos++];
                if (c == '\\' && pos < s.Length) { sb.Append(s[pos++]); continue; }
                if (c == quote) return sb.ToString();
                sb.Append(c);
            }
            throw new FormatException("A quoted text is not closed.");
        }

        private static void SkipSpace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        // ---- evaluator ----------------------------------------------------------------------------------

        private static string Eval(Node n, PatternContext ctx, List<string> unknownNames)
        {
            if (n.Kind == Kind.Text) return n.Text;
            if (n.Kind == Kind.Identifier) return Identifier(n.Text, ctx, unknownNames);

            var fn = n.Text.ToLowerInvariant();
            var a = n.Args;
            string Arg(int i) => Eval(a[i], ctx, unknownNames);
            void Arity(int min, int max)
            {
                if (a.Count < min || a.Count > max)
                    throw new FormatException($"{fn} has the wrong number of arguments.");
            }

            switch (fn)
            {
                case "upper": Arity(1, 1); return Arg(0).ToUpperInvariant();
                case "lower": Arity(1, 1); return Arg(0).ToLowerInvariant();
                case "proper": Arity(1, 1); return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Arg(0).ToLowerInvariant());
                case "trim": Arity(1, 1); return Arg(0).Trim();
                case "concat":
                {
                    Arity(1, int.MaxValue);
                    var sb = new StringBuilder();
                    for (var i = 0; i < a.Count; i++) sb.Append(Arg(i));
                    return sb.ToString();
                }
                case "replace":
                {
                    Arity(3, 3);
                    var text = Arg(0);
                    var old = Arg(1);
                    return old.Length == 0 ? text : text.Replace(old, Arg(2), StringComparison.OrdinalIgnoreCase);
                }
                case "regex":
                {
                    Arity(3, 3);
                    try { return Regex.Replace(Arg(0), Arg(1), Arg(2), RegexOptions.CultureInvariant, RegexBudget); }
                    catch (ArgumentException ex) { throw new FormatException(ex.Message); }
                    catch (RegexMatchTimeoutException ex) { throw new FormatException(ex.Message); }
                }
                case "substring":
                {
                    Arity(2, 3);
                    var text = Arg(0);
                    var start = Int(Arg(1));
                    var length = a.Count == 3 ? Int(Arg(2)) : int.MaxValue;
                    if (start < 0) start = Math.Max(0, text.Length + start);
                    if (start >= text.Length || length <= 0) return string.Empty;
                    return text.Substring(start, Math.Min(length, text.Length - start));
                }
                case "first":
                {
                    Arity(1, int.MaxValue);
                    for (var i = 0; i < a.Count; i++)
                    {
                        var v = Arg(i);
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    return string.Empty;
                }
                case "lookup":
                {
                    Arity(2, 3);
                    var key = Arg(0).Trim();
                    foreach (var pair in Arg(1).Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = pair.IndexOf('=');
                        if (eq < 0) continue;
                        if (pair[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return pair[(eq + 1)..].Trim();
                    }
                    return a.Count == 3 ? Arg(2) : string.Empty;
                }
                case "if": Arity(2, 3); return Truthy(Arg(0)) ? Arg(1) : a.Count == 3 ? Arg(2) : string.Empty;
                case "equals": Arity(2, 2); return string.Equals(Arg(0).Trim(), Arg(1).Trim(), StringComparison.OrdinalIgnoreCase) ? "true" : string.Empty;
                case "date":
                {
                    Arity(1, 3);
                    var text = Arg(0).Trim();
                    if (text.Length == 0) return string.Empty;
                    if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        throw new FormatException($"'{text}' is not a date.");
                    var format = "yyyy-MM-dd";
                    for (var i = 1; i < a.Count; i++)
                    {
                        var opt = Arg(i).Trim();
                        var shift = OffsetRegex.Match(opt);
                        if (!shift.Success) { format = opt; continue; }
                        var amount = int.Parse(shift.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        d = shift.Groups[2].Value.ToLowerInvariant() switch
                        {
                            "d" => d.AddDays(amount),
                            "m" => d.AddMonths(amount),
                            _ => d.AddYears(amount),
                        };
                    }
                    return d.ToString(format, CultureInfo.InvariantCulture);   // a bad format throws FormatException
                }
                default: throw new FormatException($"'{n.Text}' is not a function.");
            }
        }

        private static string Identifier(string name, PatternContext ctx, List<string> unknownNames)
        {
            if (name.Equals("Now", StringComparison.OrdinalIgnoreCase))
                return (ctx.Now ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            if (name.StartsWith("Org.", StringComparison.OrdinalIgnoreCase))
                return ctx.Organization is not null && TryGet(ctx.Organization, name["Org.".Length..], out var o) ? (o ?? string.Empty).Trim() : string.Empty;
            if (name.StartsWith("Initiator.", StringComparison.OrdinalIgnoreCase))
                return ctx.Initiator is not null && TryGet(ctx.Initiator, name["Initiator.".Length..], out var i) ? (i ?? string.Empty).Trim() : string.Empty;
            if (name.Equals("FirstInitial", StringComparison.OrdinalIgnoreCase)) return Initial("FirstName");
            if (name.Equals("LastInitial", StringComparison.OrdinalIgnoreCase)) return Initial("LastName");
            if (!TryGet(ctx.Attributes, name, out var value)) { unknownNames.Add(name); return string.Empty; }
            return (value ?? string.Empty).Trim();

            string Initial(string of)
            {
                if (!TryGet(ctx.Attributes, of, out var v)) { unknownNames.Add(of); return string.Empty; }
                v = (v ?? string.Empty).Trim();
                return v.Length == 0 ? string.Empty : v[..1];
            }
        }

        private static bool Truthy(string v) =>
            !string.IsNullOrWhiteSpace(v) && !v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        private static int Int(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new FormatException($"'{text}' is not a whole number.");
    }
}
