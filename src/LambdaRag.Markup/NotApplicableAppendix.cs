using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LambdaRag.Core.Domain;

namespace LambdaRag.Markup;

/// <summary>Why a rule resolved to N/A, expressed as a stable category.</summary>
public enum NotApplicableCategory
{
    ExcludedTechNotRequired,
    ExcludedNotRequired,
    ProcessOrGovernance,
    ConditionNotTriggered,
    OptionalGuidance,
    RequiredInformationNotStated,
    ScopeNotEstablished,
    TopicNotAddressed,
}

/// <summary>One row of the N/A appendix table.</summary>
public sealed record NotApplicableRow(
    string RuleId,
    string Definition,
    string ProbableSection,
    string Category,
    string Reason);

/// <summary>
/// Builds and appends an "N/A rules" appendix to a reviewed .docx. Pure code
/// and fully deterministic: categories come from regex/keyword heuristics over
/// the rule text and the verdict's diagnostic tag, and the probable section
/// comes from lexical overlap against the document's own headings. No LLM, no
/// clock, no randomness.
/// </summary>
public static class NotApplicableAppendix
{
    public const string AppendixHeadingText = "Appendix: Rules Not Applicable to This Document";
    public const string SubsectionHeadingText = "Rules Not Applicable";
    public const string NoSectionText = "None identified";

    private static readonly Regex TechNounPattern = new(
        @"\b(policy engine|cluster|kubernetes|container|registry|service mesh|gateway|firewall|load balancer|"
        + @"database|data warehouse|message queue|broker|cache|hsm|vpn|waf|siem|api management|"
        + @"key vault|ci/cd|pipeline|hypervisor|virtual machine|serverless|kafka|redis)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProperNounPattern = new(
        @"(?<=[A-Za-z0-9,—–\-)]\s+)(?:[A-Z][a-z0-9]+[A-Z][A-Za-z0-9]*|[A-Z][a-z0-9]{3,})\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FactsTagPattern = new(
        @"fact_mode:missing_required_facts(?:_any)?:(?<f>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> NonProductWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHALL", "MUST", "SHOULD", "MAY", "NOT", "AND", "THE", "ALL", "ANY", "FOR", "WITH",
        "Each", "Every", "Where", "When", "Unless", "Production", "Workloads", "Workload",
        "Mandatory", "Controls", "Control", "Policy", "Standard", "Standards", "Chapter",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "shall", "must", "should", "will", "with", "that", "this", "from", "have", "been", "into",
        "each", "every", "such", "than", "then", "their", "there", "which", "where", "when",
        "including", "include", "includes", "required", "requirements", "requirement", "also",
        "document", "solution", "other", "between", "under", "over", "using", "used", "only",
    };

    /// <summary>
    /// Rules whose every non-skipped verdict is N/A, in rule-id ordinal order.
    /// </summary>
    public static IReadOnlyList<NotApplicableRow> BuildRows(
        ComplianceReport report,
        IReadOnlyDictionary<string, Rule> rules,
        IReadOnlyList<DocumentSection> sections)
    {
        var rows = new List<NotApplicableRow>();
        foreach (var group in report.Verdicts
            .Where(v => v.Outcome != VerdictOutcome.Skipped)
            .GroupBy(v => v.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!group.All(v => v.Outcome == VerdictOutcome.NotApplicable)) continue;
            rules.TryGetValue(group.Key, out var rule);
            var tag = group.Select(v => v.ErrorMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            var (category, reason) = Classify(rule, tag);
            rows.Add(new NotApplicableRow(
                group.Key,
                rule?.NaturalLanguage ?? string.Empty,
                FindProbableSection(rule, sections),
                CategoryLabel(category),
                reason));
        }
        return rows;
    }

    public static string CategoryLabel(NotApplicableCategory c) => c switch
    {
        NotApplicableCategory.ExcludedTechNotRequired => "EXCLUDED_TECH_NOT_REQUIRED",
        NotApplicableCategory.ExcludedNotRequired => "EXCLUDED_NOT_REQUIRED",
        NotApplicableCategory.ProcessOrGovernance => "Process / governance (outside document)",
        NotApplicableCategory.ConditionNotTriggered => "Condition not established",
        NotApplicableCategory.OptionalGuidance => "Optional guidance",
        NotApplicableCategory.RequiredInformationNotStated => "Information not stated",
        NotApplicableCategory.ScopeNotEstablished => "Scope not established",
        _ => "Topic not addressed",
    };

    public static (NotApplicableCategory Category, string Reason) Classify(Rule? rule, string? diagnosticTag)
    {
        var text = rule is null ? string.Empty : $"{rule.NaturalLanguage} {rule.EvidenceQuote}";

        if (diagnosticTag is not null
            && diagnosticTag.StartsWith(AbsenceEvidence.ExcludedTagPrefix, StringComparison.Ordinal))
        {
            var statement = diagnosticTag[AbsenceEvidence.ExcludedTagPrefix.Length..];
            var tech = ExtractTechnologies(text);
            return tech.Count > 0
                ? (NotApplicableCategory.ExcludedTechNotRequired,
                    $"The document states the technology is not present ({string.Join(", ", tech)}): \"{statement}\"")
                : (NotApplicableCategory.ExcludedNotRequired,
                    $"The document states the rule's subject or condition does not apply: \"{statement}\"");
        }

        if (diagnosticTag == AbsenceEvidence.ProcessTag
            || (rule is not null && AbsenceEvidence.IsOutOfDocumentProcess(rule)))
        {
            return (NotApplicableCategory.ProcessOrGovernance,
                "The rule concerns an organizational process or an external actor (for example a reviewing "
                + "group, approval, audit or cadence) rather than content a solution document would state. "
                + "Verify it through the governance process, not this document.");
        }

        if (rule?.Applicability == RuleApplicability.Conditional)
        {
            return (NotApplicableCategory.ConditionNotTriggered,
                "The rule applies only when its trigger condition holds, and no section of the document "
                + "establishes that condition. Evidence needed: a statement that the condition is absent, "
                + "or the control the rule requires if the condition holds.");
        }

        if (rule?.Applicability == RuleApplicability.Optional)
        {
            return (NotApplicableCategory.OptionalGuidance,
                "The rule is a recommendation (SHOULD / MAY), so a document that does not address it is "
                + "not in violation.");
        }

        if (diagnosticTag is not null)
        {
            var m = FactsTagPattern.Match(diagnosticTag);
            if (m.Success)
            {
                return (NotApplicableCategory.RequiredInformationNotStated,
                    $"The document does not state the information this rule evaluates: {m.Groups["f"].Value}. "
                    + "Evidence needed: the stated control, decision or owner, or an explicit statement that it does not apply.");
            }
            if (diagnosticTag.StartsWith("fact_mode:no_scoped_sections", StringComparison.Ordinal))
            {
                return (NotApplicableCategory.TopicNotAddressed,
                    "No section of the document discusses the concepts this rule evaluates.");
            }
            if (diagnosticTag.StartsWith("scope:not_established", StringComparison.Ordinal))
            {
                return (NotApplicableCategory.ScopeNotEstablished,
                    "No section establishes the scope this rule requires, so it could not be adjudicated.");
            }
        }

        return (NotApplicableCategory.TopicNotAddressed,
            "No section of the document is relevant to this rule's topic, so it was not evaluated. "
            + "Evidence needed: either state why the topic is out of scope, or document the control the rule requires.");
    }

    private static List<string> ExtractTechnologies(string text)
    {
        var found = new List<string>();
        foreach (Match m in ProperNounPattern.Matches(text))
        {
            if (NonProductWords.Contains(m.Value)) continue;
            if (!found.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) found.Add(m.Value);
        }
        if (found.Count == 0)
        {
            foreach (Match m in TechNounPattern.Matches(text))
            {
                var v = m.Value.ToLowerInvariant();
                if (!found.Contains(v, StringComparer.Ordinal)) found.Add(v);
                if (found.Count == 1) break;
            }
        }
        return found.Take(4).ToList();
    }

    private static string FindProbableSection(Rule? rule, IReadOnlyList<DocumentSection> sections)
    {
        if (rule is null || sections.Count == 0) return NoSectionText;
        rule.Metadata.TryGetValue("topicSlug", out var topic);
        var ruleTokens = Tokenize($"{rule.NaturalLanguage} {topic}");
        if (ruleTokens.Count == 0) return NoSectionText;

        var tokenSets = sections
            .Select(s => (Heading: Tokenize(s.Title), Body: Tokenize(s.Body)))
            .ToList();
        // Rare tokens carry more signal than ones spread across every section.
        double Weight(string t)
        {
            var df = tokenSets.Count(x => x.Heading.Contains(t) || x.Body.Contains(t));
            return df == 0 ? 0 : Math.Log(1.0 + (double)sections.Count / df);
        }
        var weights = ruleTokens.ToDictionary(t => t, Weight, StringComparer.Ordinal);

        var best = NoSectionText;
        var bestScore = 0.0;
        for (var i = 0; i < sections.Count; i++)
        {
            var (heading, bodyTokens) = tokenSets[i];
            var matched = ruleTokens.Where(t => heading.Contains(t) || bodyTokens.Contains(t)).ToList();
            if (matched.Count < 2) continue;
            var score = matched.Sum(t => weights[t] * (heading.Contains(t) ? 3.0 : 1.0));
            if (score > bestScore)
            {
                bestScore = score;
                best = sections[i].Title;
            }
        }
        return best;
    }

    private static HashSet<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{4,}")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A heading and the body text beneath it, read from the docx.</summary>
    public sealed record DocumentSection(string Title, string Body);

    /// <summary>Read the headings (and their body text) of a .docx in document order.</summary>
    public static IReadOnlyList<DocumentSection> ReadSections(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Array.Empty<DocumentSection>();

        var sections = new List<DocumentSection>();
        string? title = null;
        var buffer = new System.Text.StringBuilder();
        foreach (var element in body.Elements<OpenXmlElement>())
        {
            if (element is Paragraph p && HeadingLevel(p) is not null)
            {
                if (title is not null) sections.Add(new DocumentSection(title, buffer.ToString()));
                title = p.InnerText.Trim();
                buffer.Clear();
                if (title.Length == 0) title = null;
            }
            else if (title is not null)
            {
                buffer.Append(element.InnerText).Append(' ');
            }
        }
        if (title is not null) sections.Add(new DocumentSection(title, buffer.ToString()));
        return sections;
    }

    private static int? HeadingLevel(Paragraph p)
    {
        var outline = p.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and <= 8) return outline.Value + 1;
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is null) return null;
        var m = Regex.Match(style, @"^Heading\s*(\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Append the appendix to <paramref name="docxPath"/>. When the document
    /// already ends with an Appendix section, the table is added as a child
    /// heading (one level deeper) at the end of that section; otherwise a new
    /// top-level appendix heading is appended. Idempotent: any earlier
    /// appendix generated by this method is replaced.
    /// </summary>
    public static void Append(string docxPath, IReadOnlyList<NotApplicableRow> rows)
    {
        using var doc = WordprocessingDocument.Open(docxPath, isEditable: true);
        var main = doc.MainDocumentPart
            ?? throw new InvalidOperationException("DOCX has no MainDocumentPart");
        var body = main.Document?.Body
            ?? throw new InvalidOperationException("DOCX has no Body");

        RemovePreviousAppendix(body);

        var elements = body.Elements<OpenXmlElement>().ToList();
        var sectPr = elements.OfType<SectionProperties>().LastOrDefault();

        Paragraph? appendixHeading = null;
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            if (elements[i] is Paragraph p
                && HeadingLevel(p) is not null
                && p.InnerText.TrimStart().StartsWith("Appendix", StringComparison.OrdinalIgnoreCase))
            {
                appendixHeading = p;
                break;
            }
        }

        var styles = main.StyleDefinitionsPart?.Styles;
        bool HasStyle(string id) => styles?.Elements<Style>().Any(s => s.StyleId?.Value == id) == true;

        OpenXmlElement anchor;
        var content = new List<OpenXmlElement>();
        if (appendixHeading is not null)
        {
            var level = HeadingLevel(appendixHeading)!.Value;
            var sectionEnd = FindSectionEnd(elements, appendixHeading, level);
            anchor = sectionEnd ?? (OpenXmlElement?)sectPr ?? body.LastChild!;
            content.Add(BuildHeading(SubsectionHeadingText, Math.Min(level + 1, 9), HasStyle));
        }
        else
        {
            anchor = (OpenXmlElement?)sectPr ?? body.LastChild!;
            content.Add(BuildHeading(AppendixHeadingText, 1, HasStyle));
        }

        content.Add(BuildBodyParagraph(
            rows.Count == 0
                ? "Every rule was applicable to this document."
                : "These rules were not confirmed as compliant from the available document evidence. "
                  + "A rule is classified as Not applicable only when the document establishes that the relevant "
                  + "technology, workload characteristic, data class, operating model, or triggering condition is absent. "
                  + "When the rule is relevant to the documented solution but the required control, decision, evidence, "
                  + "ownership, or operating process is missing, the rule is classified as a Gap. "
                  + "Rows categorised EXCLUDED_TECH_NOT_REQUIRED or EXCLUDED_NOT_REQUIRED cite the document statement "
                  + "that excludes the technology or condition; other rows concern processes outside the document. "
                  + "Each row identifies the most relevant document section, the classification rationale, "
                  + "and the evidence needed to resolve the finding."));
        if (rows.Count > 0) content.Add(BuildTable(rows));

        foreach (var element in content)
            body.InsertBefore(element, anchor);

        main.Document.Save();
    }

    // First element after the appendix heading that is a heading of the same or higher level; null = end of body.
    private static OpenXmlElement? FindSectionEnd(List<OpenXmlElement> elements, Paragraph heading, int level)
    {
        var idx = elements.IndexOf(heading);
        for (var i = idx + 1; i < elements.Count; i++)
        {
            if (elements[i] is Paragraph p && HeadingLevel(p) is { } l && l <= level) return p;
            if (elements[i] is SectionProperties) return elements[i];
        }
        return null;
    }

    private static void RemovePreviousAppendix(Body body)
    {
        var elements = body.Elements<OpenXmlElement>().ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i] is not Paragraph p || HeadingLevel(p) is not { } level) continue;
            var text = p.InnerText.Trim();
            if (text != AppendixHeadingText && text != SubsectionHeadingText) continue;

            var j = i + 1;
            while (j < elements.Count
                && elements[j] is not SectionProperties
                && !(elements[j] is Paragraph q && HeadingLevel(q) is { } l && l <= level))
                j++;
            for (var k = i; k < j; k++) elements[k].Remove();
            return;
        }
    }

    private static Paragraph BuildHeading(string text, int level, Func<string, bool> hasStyle)
    {
        var styleId = $"Heading{level}";
        var para = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        var run = new Run(new Text(text));
        if (!hasStyle(styleId))
        {
            para.ParagraphProperties!.Append(new KeepNext(), new OutlineLevel { Val = level - 1 });
            run.RunProperties = new RunProperties(new Bold(), new FontSize { Val = (level == 1 ? 32 : 26).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        para.Append(run);
        return para;
    }

    private static Paragraph BuildBodyParagraph(string text) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Table BuildTable(IReadOnlyList<NotApplicableRow> rows)
    {
        var borders = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
        var table = new Table(new TableProperties(
            borders,
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableLayout { Type = TableLayoutValues.Fixed }));

        string[] headers = { "Rule", "Definition", "Probable Section", "Category", "Reason" };
        int[] widths = { 900, 2300, 1800, 1500, 2500 };
        table.Append(new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(System.Globalization.CultureInfo.InvariantCulture) })));

        var header = new TableRow(new TableRowProperties(new TableHeader()));
        for (var i = 0; i < headers.Length; i++) header.Append(BuildCell(headers[i], widths[i], bold: true));
        table.Append(header);

        foreach (var r in rows)
        {
            var row = new TableRow(new TableRowProperties(new CantSplit()));
            string[] cells = { r.RuleId, r.Definition, r.ProbableSection, r.Category, r.Reason };
            for (var i = 0; i < cells.Length; i++) row.Append(BuildCell(cells[i], widths[i], bold: false));
            table.Append(row);
        }
        return table;
    }

    private static TableCell BuildCell(string text, int width, bool bold)
    {
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        run.RunProperties = new RunProperties(new FontSize { Val = "18" });
        if (bold) run.RunProperties.InsertAt(new Bold(), 0);
        var cell = new TableCell(
            new TableCellProperties(
                new TableCellWidth { Width = width.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                bold ? new Shading { Val = ShadingPatternValues.Clear, Fill = "D9D9D9" } : new Shading { Val = ShadingPatternValues.Clear, Fill = "auto" }),
            new Paragraph(run));
        return cell;
    }
}
