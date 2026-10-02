using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Renders Markdown into a tree of UI Toolkit <see cref="VisualElement"/>s using element
    /// styling (font size, weight, indents, colored boxes) rather than rich-text tags.
    ///
    /// This deliberately avoids <c>enableRichText</c>: Unity's native text generator
    /// (TextCore <c>RichTextTagParser</c>) throws <see cref="System.IndexOutOfRangeException"/>
    /// while measuring rich-text content, so every label here keeps rich text disabled.
    /// </summary>
    public static class MarkdownView
    {
        static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex ListMarker = new(@"^( *)([-*+]|\d{1,9}[.)])( +)(.*)$", RegexOptions.Compiled);
        static readonly Regex TaskMarker = new(@"^\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Quote = new(@"^ {0,3}>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex AlertMarker = new(@"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ReferenceDefinition = new(@"^ {0,3}\[([^\]^][^\]]*)\]:\s*<?([^\s>]+)>?(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*$", RegexOptions.Compiled);
        static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);

        static readonly Regex LinkAt = new(@"\G\[([^\]]*)\]\(\s*<?([^)\s>]*)>?(?:\s+""[^""]*"")?\s*\)", RegexOptions.Compiled);
        static readonly Regex ReferenceLinkAt = new(@"\G\[([^\]]+)\]\[([^\]]*)\]", RegexOptions.Compiled);
        static readonly Regex ShortcutLinkAt = new(@"\G\[([^\]]+)\]", RegexOptions.Compiled);
        static readonly Regex AutolinkAt = new(@"\G<((?:https?|mailto):[^>\s]+)>", RegexOptions.Compiled);
        static readonly Regex SpanAt = new(@"\G<span\s+style\s*=\s*[""']\s*color\s*:\s*([^;""']+?)\s*;?\s*[""']\s*>(.*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex FontAt = new(@"\G<font\s+color\s*=\s*[""']?([^""'\s>]+)[""']?\s*>(.*?)</font>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex BreakAt = new(@"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Words = new(@"\S+\s*|\s+", RegexOptions.Compiled);
        static readonly Regex SlugStrip = new(@"[^\p{L}\p{N}\- _]", RegexOptions.Compiled);

        static readonly Color CodeColor = new(0.79f, 0.64f, 0.43f);
        static readonly Color CodeBackground = new(0f, 0f, 0f, 0.25f);
        static readonly Color LinkColor = new(0.45f, 0.7f, 1f);
        static readonly Color MutedColor = new(1f, 1f, 1f, 0.55f);
        static readonly Color TableBorderColor = new(1f, 1f, 1f, 0.15f);
        static readonly Color TableHeaderColor = new(1f, 1f, 1f, 0.08f);
        static readonly Color TableStripeColor = new(1f, 1f, 1f, 0.03f);

        const int BodyFontSize = 12;
        const char LineBreak = '\u2028';
        static readonly string[] BulletGlyphs = { "•", "◦", "▪" };

        enum AlertKind
        {
            Note,
            Tip,
            Important,
            Warning,
            Caution
        }

        [Flags]
        enum InlineStyle
        {
            None = 0,
            Bold = 1,
            Italic = 2,
            Code = 4,
            Strike = 8
        }

        readonly struct InlineRun
        {
            public readonly string Text;
            public readonly InlineStyle Style;
            public readonly string Link;
            public readonly Color? Color;

            public InlineRun(string text, InlineStyle style, string link, Color? color)
            {
                Text = text;
                Style = style;
                Link = link;
                Color = color;
            }
        }

        readonly struct SourceLine
        {
            public readonly string Text;
            public readonly int Index;

            public SourceLine(string text, int index)
            {
                Text = text;
                Index = index;
            }
        }

        sealed class RenderContext
        {
            public readonly VisualElement Root;
            public readonly string DocumentPath;
            public readonly Action<int, bool> OnCheckboxToggled;
            public readonly Dictionary<string, string> References = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, VisualElement> Anchors = new();

            public RenderContext(VisualElement root, string documentPath, Action<int, bool> onCheckboxToggled)
            {
                Root = root;
                DocumentPath = documentPath;
                OnCheckboxToggled = onCheckboxToggled;
            }
        }

        /// <param name="onCheckboxToggled">
        /// Invoked when a task-list checkbox (<c>- [ ]</c> / <c>- [x]</c>) is clicked, with the
        /// zero-based source line index and the new checked state. Pass <c>null</c> to render
        /// checkboxes as read-only (e.g. when the source text is truncated).
        /// </param>
        public static void Populate(VisualElement container, string markdown, Action<int, bool> onCheckboxToggled = null, string documentPath = null)
        {
            container.Clear();
            var ctx = new RenderContext(container, documentPath, onCheckboxToggled);
            container.userData = ctx.Anchors;
            if (string.IsNullOrEmpty(markdown)) return;

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
            RenderBlocks(container, Preprocess(lines, ctx), ctx, 0);
        }

        public static void ScrollToAnchorAfterLayout(VisualElement container, string anchor)
        {
            var target = FindAnchor((Dictionary<string, VisualElement>)container.userData, anchor);
            var scroll = container.GetFirstAncestorOfType<ScrollView>()
                ?? throw new InvalidOperationException("Markdown content is not inside a ScrollView, so it cannot scroll to an anchor.");
            EventCallback<GeometryChangedEvent> handler = null;
            handler = _ =>
            {
                scroll.contentContainer.UnregisterCallback(handler);
                scroll.schedule.Execute(() => ScrollTo(scroll, target));
            };
            scroll.contentContainer.RegisterCallback(handler);
        }

        static VisualElement FindAnchor(Dictionary<string, VisualElement> anchors, string anchor)
        {
            if (!anchors.TryGetValue(Uri.UnescapeDataString(anchor).ToLowerInvariant(), out var target))
                throw new KeyNotFoundException($"No heading with anchor '#{anchor}' in this Markdown document.");
            return target;
        }

        static void ScrollTo(ScrollView scroll, VisualElement target)
        {
            var y = scroll.contentContainer.WorldToLocal(target.worldBound.position).y;
            scroll.scrollOffset = new Vector2(scroll.scrollOffset.x, y);
        }

        static List<SourceLine> Preprocess(string[] lines, RenderContext ctx)
        {
            var result = new List<SourceLine>();
            var inFence = false;
            var inComment = false;
            var cleaned = new StringBuilder();

            for (var index = 0; index < lines.Length; index++)
            {
                var text = lines[index];
                if (!inComment && IsFence(text))
                {
                    inFence = !inFence;
                    result.Add(new SourceLine(text, index));
                    continue;
                }
                if (inFence)
                {
                    result.Add(new SourceLine(text, index));
                    continue;
                }

                var touchedComment = inComment || text.Contains("<!--");
                cleaned.Clear();
                var pos = 0;
                while (pos < text.Length)
                {
                    if (inComment)
                    {
                        var close = text.IndexOf("-->", pos, StringComparison.Ordinal);
                        if (close < 0) break;
                        inComment = false;
                        pos = close + 3;
                        continue;
                    }
                    var open = text.IndexOf("<!--", pos, StringComparison.Ordinal);
                    if (open < 0)
                    {
                        cleaned.Append(text, pos, text.Length - pos);
                        break;
                    }
                    cleaned.Append(text, pos, open - pos);
                    inComment = true;
                    pos = open + 4;
                }

                var line = cleaned.ToString();
                if (touchedComment && string.IsNullOrWhiteSpace(line)) continue;

                var definition = ReferenceDefinition.Match(line);
                if (definition.Success)
                {
                    ctx.References[definition.Groups[1].Value.Trim()] = definition.Groups[2].Value;
                    continue;
                }

                result.Add(new SourceLine(line, index));
            }
            return result;
        }

        static void RenderBlocks(VisualElement container, List<SourceLine> lines, RenderContext ctx, int listDepth)
        {
            var paragraph = new List<string>();
            var fence = new List<string>();
            var fenceLanguage = string.Empty;
            var inFence = false;

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                var block = Inline(JoinParagraph(paragraph), InlineStyle.None, BodyFontSize, ctx);
                block.style.marginBottom = listDepth > 0 ? 2 : 6;
                container.Add(block);
                paragraph.Clear();
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Text;
                if (IsFence(line))
                {
                    if (inFence) { container.Add(CodeBlock(fence, fenceLanguage)); fence.Clear(); inFence = false; }
                    else { FlushParagraph(); fenceLanguage = line.TrimStart().Substring(3).Trim().ToLowerInvariant(); inFence = true; }
                    continue;
                }

                if (inFence) { fence.Add(line); continue; }

                if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }

                if (IsTableStart(lines, i))
                {
                    FlushParagraph();
                    var header = SplitRow(line);
                    var alignments = ParseAlignments(SplitRow(lines[i + 1].Text));
                    var rows = new List<string[]>();
                    i += 2;
                    while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i].Text) && lines[i].Text.Contains('|'))
                    {
                        rows.Add(SplitRow(lines[i].Text));
                        i++;
                    }
                    i--;
                    container.Add(Table(header, alignments, rows, ctx));
                    continue;
                }

                if (IsRule(line))
                {
                    FlushParagraph();
                    container.Add(Rule());
                    continue;
                }

                var h = Heading.Match(line);
                if (h.Success)
                {
                    FlushParagraph();
                    container.Add(HeadingBlock(h.Groups[1].Value.Length, h.Groups[2].Value, ctx));
                    continue;
                }

                if (Quote.IsMatch(line))
                {
                    FlushParagraph();
                    var quoteLines = new List<SourceLine>();
                    while (i < lines.Count && Quote.IsMatch(lines[i].Text))
                    {
                        quoteLines.Add(new SourceLine(Quote.Match(lines[i].Text).Groups[1].Value, lines[i].Index));
                        i++;
                    }
                    i--;
                    var alert = AlertMarker.Match(quoteLines[0].Text);
                    container.Add(alert.Success
                        ? AlertBlock(ParseAlertKind(alert.Groups[1].Value), quoteLines.GetRange(1, quoteLines.Count - 1), ctx, listDepth)
                        : QuoteBlock(quoteLines, ctx, listDepth));
                    continue;
                }

                var marker = ListMarker.Match(line);
                if (marker.Success)
                {
                    FlushParagraph();
                    var indent = marker.Groups[1].Value.Length;
                    var spacing = marker.Groups[3].Value.Length;
                    var contentColumn = indent + marker.Groups[2].Value.Length + (spacing > 4 ? 1 : spacing);
                    var itemLines = new List<SourceLine> { new(marker.Groups[4].Value, lines[i].Index) };
                    var j = i + 1;
                    while (j < lines.Count)
                    {
                        var next = lines[j].Text;
                        if (string.IsNullOrWhiteSpace(next))
                        {
                            var k = j;
                            while (k < lines.Count && string.IsNullOrWhiteSpace(lines[k].Text)) k++;
                            if (k == lines.Count || !BelongsToItem(lines[k].Text, indent, contentColumn)) break;
                            for (; j < k; j++) itemLines.Add(new SourceLine(string.Empty, lines[j].Index));
                            continue;
                        }
                        if (BelongsToItem(next, indent, contentColumn))
                        {
                            itemLines.Add(new SourceLine(next.Substring(Math.Min(contentColumn, LeadingSpaces(next))), lines[j].Index));
                            j++;
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(lines[j - 1].Text) && !IsBlockStart(next))
                        {
                            itemLines.Add(new SourceLine(next.TrimStart(), lines[j].Index));
                            j++;
                            continue;
                        }
                        break;
                    }
                    container.Add(ListItem(marker.Groups[2].Value, itemLines, ctx, listDepth));
                    i = j - 1;
                    continue;
                }

                paragraph.Add(line);
            }

            if (inFence && fence.Count > 0) container.Add(CodeBlock(fence, fenceLanguage));
            FlushParagraph();
        }

        static bool BelongsToItem(string line, int itemIndent, int contentColumn)
        {
            var leading = LeadingSpaces(line);
            return leading >= contentColumn || leading > itemIndent && ListMarker.IsMatch(line);
        }

        static int LeadingSpaces(string line)
        {
            var count = 0;
            while (count < line.Length && line[count] == ' ') count++;
            return count;
        }

        static bool IsFence(string line) => line.TrimStart().StartsWith("```");

        static bool IsRule(string line) => line.Trim() is "---" or "***" or "___";

        static bool IsBlockStart(string line) =>
            IsFence(line) || IsRule(line) || Heading.IsMatch(line) || Quote.IsMatch(line) || ListMarker.IsMatch(line);

        static string JoinParagraph(List<string> lines)
        {
            var builder = new StringBuilder();
            for (var k = 0; k < lines.Count; k++)
            {
                var raw = lines[k];
                var text = raw.Trim();
                var last = k == lines.Count - 1;
                var backslashBreak = !last && text.EndsWith("\\");
                if (backslashBreak) text = text.Substring(0, text.Length - 1).TrimEnd();
                builder.Append(text);
                if (last) break;
                builder.Append(backslashBreak || raw.EndsWith("  ") ? LineBreak : ' ');
            }
            return builder.ToString();
        }

        static VisualElement HeadingBlock(int level, string text, RenderContext ctx)
        {
            var fontSize = level switch { 1 => 20, 2 => 17, 3 => 15, 4 => 14, _ => 13 };
            var block = Inline(text, InlineStyle.Bold, fontSize, ctx);
            block.style.marginTop = 8;
            block.style.marginBottom = 4;

            var baseSlug = SlugStrip.Replace(PlainText(text, ctx).Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');
            var slug = baseSlug;
            for (var suffix = 1; ctx.Anchors.ContainsKey(slug); suffix++) slug = $"{baseSlug}-{suffix}";
            ctx.Anchors[slug] = block;
            return block;
        }

        static VisualElement QuoteBlock(List<SourceLine> quoteLines, RenderContext ctx, int listDepth)
        {
            var block = new VisualElement();
            block.style.color = MutedColor;
            block.style.paddingLeft = 8;
            block.style.borderLeftWidth = 3;
            block.style.borderLeftColor = MutedColor;
            block.style.marginBottom = 6;
            RenderBlocks(block, quoteLines, ctx, listDepth);
            return block;
        }

        static VisualElement AlertBlock(AlertKind kind, List<SourceLine> bodyLines, RenderContext ctx, int listDepth)
        {
            var (title, color) = kind switch
            {
                AlertKind.Note => ("Note", new Color(0.27f, 0.58f, 0.97f)),
                AlertKind.Tip => ("Tip", new Color(0.25f, 0.73f, 0.31f)),
                AlertKind.Important => ("Important", new Color(0.67f, 0.49f, 0.97f)),
                AlertKind.Warning => ("Warning", new Color(0.82f, 0.6f, 0.13f)),
                AlertKind.Caution => ("Caution", new Color(0.97f, 0.32f, 0.29f)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };

            var block = new VisualElement
            {
                style =
                {
                    paddingLeft = 8, paddingTop = 4, paddingBottom = 2, marginBottom = 6,
                    borderLeftWidth = 3, borderLeftColor = color,
                    backgroundColor = new Color(color.r, color.g, color.b, 0.08f)
                }
            };

            var heading = new Label(title) { enableRichText = false };
            heading.style.color = color;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginLeft = 0;
            heading.style.paddingLeft = 0;
            heading.style.marginBottom = 4;
            block.Add(heading);

            RenderBlocks(block, bodyLines, ctx, listDepth);
            return block;
        }

        static AlertKind ParseAlertKind(string marker) => marker.ToUpperInvariant() switch
        {
            "NOTE" => AlertKind.Note,
            "TIP" => AlertKind.Tip,
            "IMPORTANT" => AlertKind.Important,
            "WARNING" => AlertKind.Warning,
            "CAUTION" => AlertKind.Caution,
            _ => throw new ArgumentOutOfRangeException(nameof(marker), marker, "Unknown alert marker.")
        };

        static VisualElement ListItem(string marker, List<SourceLine> itemLines, RenderContext ctx, int listDepth)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.FlexStart,
                    marginLeft = listDepth == 0 ? 12 : 2,
                    marginBottom = 2
                }
            };

            var body = new VisualElement { style = { flexGrow = 1, flexShrink = 1, minWidth = 0 } };
            var task = TaskMarker.Match(itemLines[0].Text);
            if (task.Success)
            {
                var isChecked = task.Groups[1].Value is "x" or "X";
                var sourceLine = itemLines[0].Index;
                var toggle = new Toggle { value = isChecked, style = { marginRight = 4, marginTop = 1 } };
                toggle.SetEnabled(ctx.OnCheckboxToggled != null);
                if (ctx.OnCheckboxToggled != null)
                    toggle.RegisterValueChangedCallback(evt => ctx.OnCheckboxToggled(sourceLine, evt.newValue));
                row.Add(toggle);
                if (isChecked) body.style.color = MutedColor;
                itemLines[0] = new SourceLine(task.Groups[2].Value, itemLines[0].Index);
            }
            else
            {
                var glyph = char.IsDigit(marker[0]) ? marker : BulletGlyphs[listDepth % BulletGlyphs.Length];
                var markerLabel = new Label(glyph) { enableRichText = false };
                markerLabel.style.minWidth = 14;
                markerLabel.style.marginLeft = 0;
                markerLabel.style.marginRight = 4;
                markerLabel.style.paddingLeft = 0;
                markerLabel.style.paddingRight = 0;
                row.Add(markerLabel);
            }

            RenderBlocks(body, itemLines, ctx, listDepth + 1);
            row.Add(body);
            return row;
        }

        static VisualElement CodeBlock(List<string> codeLines, string language)
        {
            var box = new VisualElement
            {
                style =
                {
                    backgroundColor = CodeBackground,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    marginBottom = 6,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                }
            };

            var code = string.Join("\n", codeLines);
            if (language is "csharp" or "cs" or "c#")
            {
                foreach (var line in CSharpHighlighter.Tokenize(code)) box.Add(CodeLine(line));
                var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = code) { text = "Copy" };
                copy.style.position = Position.Absolute;
                copy.style.top = 2;
                copy.style.right = 2;
                copy.style.fontSize = 10;
                box.Add(copy);
                return box;
            }

            var label = new Label(code) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = CodeColor;
            label.selection.isSelectable = true;
            box.Add(label);
            return box;
        }

        static VisualElement CodeLine(List<CodeToken> tokens)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, minHeight = BodyFontSize + 3 } };
            var spaceWidth = Mathf.Round(BodyFontSize * 0.3f);
            foreach (var token in tokens)
            {
                if (token.Kind == CodeTokenKind.Whitespace)
                {
                    row.Add(new VisualElement { style = { width = token.Text.Length * spaceWidth } });
                    continue;
                }
                var label = new Label(token.Text) { enableRichText = false };
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.marginLeft = 0;
                label.style.marginRight = 0;
                label.style.marginTop = 0;
                label.style.marginBottom = 0;
                label.style.paddingLeft = 0;
                label.style.paddingRight = 0;
                label.style.paddingTop = 0;
                label.style.paddingBottom = 0;
                label.style.color = TokenColor(token.Kind);
                if (token.Kind == CodeTokenKind.Comment) label.style.unityFontStyleAndWeight = FontStyle.Italic;
                row.Add(label);
            }
            return row;
        }

        static Color TokenColor(CodeTokenKind kind) => kind switch
        {
            CodeTokenKind.Plain => new Color(0.86f, 0.86f, 0.86f),
            CodeTokenKind.Keyword => new Color(0.34f, 0.61f, 0.84f),
            CodeTokenKind.Type => new Color(0.31f, 0.79f, 0.69f),
            CodeTokenKind.Method => new Color(0.86f, 0.86f, 0.67f),
            CodeTokenKind.String => new Color(0.81f, 0.57f, 0.47f),
            CodeTokenKind.Number => new Color(0.71f, 0.81f, 0.66f),
            CodeTokenKind.Comment => new Color(0.42f, 0.6f, 0.33f),
            CodeTokenKind.Preprocessor => new Color(0.61f, 0.61f, 0.61f),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        static bool IsTableStart(List<SourceLine> lines, int index)
        {
            if (index + 1 >= lines.Count || !lines[index].Text.Contains('|')) return false;
            if (!TableSeparator.IsMatch(lines[index + 1].Text)) return false;
            return SplitRow(lines[index].Text).Length == SplitRow(lines[index + 1].Text).Length;
        }

        static string[] SplitRow(string line)
        {
            var text = line.Trim();
            if (text.StartsWith("|")) text = text.Substring(1);
            if (text.EndsWith("|") && !text.EndsWith("\\|")) text = text.Substring(0, text.Length - 1);

            var cells = new List<string>();
            var current = new StringBuilder();
            var inCode = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    current.Append('|');
                    i++;
                    continue;
                }
                if (ch == '`') inCode = !inCode;
                if (ch == '|' && !inCode)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }
            cells.Add(current.ToString().Trim());
            return cells.ToArray();
        }

        static Justify[] ParseAlignments(string[] separatorCells)
        {
            var alignments = new Justify[separatorCells.Length];
            for (var i = 0; i < separatorCells.Length; i++)
            {
                var cell = separatorCells[i];
                var left = cell.StartsWith(":");
                var right = cell.EndsWith(":");
                alignments[i] = left && right ? Justify.Center
                    : right ? Justify.FlexEnd
                    : Justify.FlexStart;
            }
            return alignments;
        }

        static VisualElement Table(string[] header, Justify[] alignments, List<string[]> rows, RenderContext ctx)
        {
            var columns = header.Length;
            var weights = new float[columns];
            for (var col = 0; col < columns; col++)
                weights[col] = PlainText(header[col], ctx).Length;
            foreach (var row in rows)
                for (var col = 0; col < columns && col < row.Length; col++)
                    weights[col] = Mathf.Max(weights[col], PlainText(row[col], ctx).Length);
            for (var col = 0; col < columns; col++)
                weights[col] = Mathf.Clamp(weights[col], 4f, 60f);

            var table = new VisualElement
            {
                style =
                {
                    marginBottom = 8,
                    borderTopWidth = 1, borderLeftWidth = 1,
                    borderTopColor = TableBorderColor, borderLeftColor = TableBorderColor
                }
            };

            table.Add(TableRow(header, alignments, weights, true, false, ctx));
            for (var r = 0; r < rows.Count; r++)
                table.Add(TableRow(rows[r], alignments, weights, false, r % 2 == 1, ctx));
            return table;
        }

        static VisualElement TableRow(string[] cells, Justify[] alignments, float[] weights, bool isHeader, bool isStriped, RenderContext ctx)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            if (isHeader) row.style.backgroundColor = TableHeaderColor;
            else if (isStriped) row.style.backgroundColor = TableStripeColor;

            for (var col = 0; col < weights.Length; col++)
            {
                var cell = Inline(col < cells.Length ? cells[col] : string.Empty,
                    isHeader ? InlineStyle.Bold : InlineStyle.None, BodyFontSize, ctx);
                cell.style.justifyContent = alignments[col];
                cell.style.flexGrow = weights[col];
                cell.style.flexShrink = 1;
                cell.style.flexBasis = 0;
                cell.style.minWidth = 0;
                cell.style.paddingTop = 3;
                cell.style.paddingBottom = 3;
                cell.style.paddingLeft = 6;
                cell.style.paddingRight = 6;
                cell.style.borderRightWidth = 1;
                cell.style.borderBottomWidth = 1;
                cell.style.borderRightColor = TableBorderColor;
                cell.style.borderBottomColor = TableBorderColor;
                row.Add(cell);
            }
            return row;
        }

        static VisualElement Rule()
        {
            return new VisualElement
            {
                style =
                {
                    height = 1, marginTop = 6, marginBottom = 6,
                    backgroundColor = new Color(1f, 1f, 1f, 0.15f)
                }
            };
        }

        static VisualElement Inline(string text, InlineStyle baseStyle, int fontSize, RenderContext ctx)
        {
            var flow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.FlexEnd,
                    fontSize = fontSize
                }
            };

            var runs = new List<InlineRun>();
            ParseInline(text, baseStyle, null, null, ctx, runs);
            var spaceWidth = Mathf.Round(fontSize * 0.3f);
            foreach (var run in runs)
            {
                if (run.Text.Length == 1 && run.Text[0] == LineBreak)
                {
                    flow.Add(new VisualElement { style = { width = Length.Percent(100), height = 0 } });
                    continue;
                }
                foreach (Match word in Words.Matches(run.Text))
                    flow.Add(Word(word.Value, run, spaceWidth, ctx));
            }
            return flow;
        }

        static VisualElement Word(string word, InlineRun run, float spaceWidth, RenderContext ctx)
        {
            var visible = word.TrimEnd();
            var label = new Label(visible) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.marginLeft = 0;
            label.style.marginTop = 0;
            label.style.marginBottom = 0;
            label.style.paddingLeft = 0;
            label.style.paddingRight = 0;
            label.style.paddingTop = 0;
            label.style.paddingBottom = 0;
            label.style.marginRight = visible.Length < word.Length ? spaceWidth : 0;
            if (visible.Length == 0) label.style.minWidth = spaceWidth;

            var bold = (run.Style & InlineStyle.Bold) != 0;
            var italic = (run.Style & InlineStyle.Italic) != 0;
            label.style.unityFontStyleAndWeight = bold && italic ? FontStyle.BoldAndItalic
                : bold ? FontStyle.Bold
                : italic ? FontStyle.Italic
                : FontStyle.Normal;

            if ((run.Style & InlineStyle.Code) != 0)
            {
                label.style.color = CodeColor;
                label.style.backgroundColor = CodeBackground;
                label.style.paddingLeft = 2;
                label.style.paddingRight = 2;
                label.style.borderTopLeftRadius = 2;
                label.style.borderTopRightRadius = 2;
                label.style.borderBottomLeftRadius = 2;
                label.style.borderBottomRightRadius = 2;
            }

            if ((run.Style & InlineStyle.Strike) != 0)
            {
                label.Add(new VisualElement
                {
                    pickingMode = PickingMode.Ignore,
                    style =
                    {
                        position = Position.Absolute,
                        left = 0, right = 0, top = Length.Percent(55), height = 1,
                        backgroundColor = MutedColor
                    }
                });
            }

            if (run.Link != null)
            {
                label.style.color = LinkColor;
                label.style.borderBottomWidth = 1;
                label.style.borderBottomColor = LinkColor;
                label.tooltip = run.Link;
                var target = run.Link;
                label.RegisterCallback<ClickEvent>(_ => OpenLink(target, ctx));
            }

            if (run.Color is { } color)
            {
                label.style.color = color;
                if (run.Link != null) label.style.borderBottomColor = color;
            }

            return label;
        }

        static void OpenLink(string link, RenderContext ctx)
        {
            if (link.Contains("://") || link.StartsWith("mailto:"))
            {
                Application.OpenURL(link);
                return;
            }

            var hash = link.IndexOf('#');
            var anchor = hash >= 0 ? link.Substring(hash + 1) : null;
            var relative = Uri.UnescapeDataString(hash >= 0 ? link.Substring(0, hash) : link);
            if (relative.Length == 0)
            {
                if (string.IsNullOrEmpty(anchor)) return;
                var target = FindAnchor(ctx.Anchors, anchor);
                ScrollTo(target.GetFirstAncestorOfType<ScrollView>()
                    ?? throw new InvalidOperationException("Markdown content is not inside a ScrollView, so it cannot scroll to an anchor."), target);
                return;
            }

            if (ctx.DocumentPath == null)
                throw new InvalidOperationException($"Cannot resolve relative link '{link}': the Markdown was rendered without a document path.");

            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ctx.DocumentPath), relative));
            if (fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                MarkdownDocumentWindow.Open(fullPath, string.IsNullOrEmpty(anchor) ? null : anchor);
            else
                EditorUtility.OpenWithDefaultApp(fullPath);
        }

        static Color ParseColor(string value)
        {
            if (!ColorUtility.TryParseHtmlString(value.Trim(), out var color))
                throw new FormatException($"Markdown colour '{value}' is not a hex code or a Unity HTML colour name.");
            return color;
        }

        static string PlainText(string text, RenderContext ctx)
        {
            var runs = new List<InlineRun>();
            ParseInline(text, InlineStyle.None, null, null, ctx, runs);
            var builder = new StringBuilder();
            foreach (var run in runs) builder.Append(run.Text);
            return builder.ToString();
        }

        static void ParseInline(string text, InlineStyle style, string link, Color? color, RenderContext ctx, List<InlineRun> runs)
        {
            var plain = new StringBuilder();

            void Flush()
            {
                if (plain.Length == 0) return;
                runs.Add(new InlineRun(plain.ToString(), style, link, color));
                plain.Clear();
            }

            var i = 0;
            while (i < text.Length)
            {
                var ch = text[i];

                if (ch == LineBreak)
                {
                    Flush();
                    runs.Add(new InlineRun(LineBreak.ToString(), style, link, color));
                    i++;
                    continue;
                }

                if (ch == '\\' && i + 1 < text.Length && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
                {
                    plain.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == '`')
                {
                    var ticks = 1;
                    while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                    var close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        Flush();
                        runs.Add(new InlineRun(text.Substring(i + ticks, close - i - ticks).Trim(), style | InlineStyle.Code, link, color));
                        i = close + ticks;
                        continue;
                    }
                    plain.Append(text, i, ticks);
                    i += ticks;
                    continue;
                }

                if (link == null && (ch == '[' || ch == '!' && i + 1 < text.Length && text[i + 1] == '['))
                {
                    var start = ch == '!' ? i + 1 : i;
                    var m = LinkAt.Match(text, start);
                    if (m.Success)
                    {
                        Flush();
                        ParseInline(m.Groups[1].Value, style, m.Groups[2].Value, color, ctx, runs);
                        i = start + m.Length;
                        continue;
                    }

                    m = ReferenceLinkAt.Match(text, start);
                    if (m.Success)
                    {
                        var key = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
                        if (ctx.References.TryGetValue(key.Trim(), out var url))
                        {
                            Flush();
                            ParseInline(m.Groups[1].Value, style, url, color, ctx, runs);
                            i = start + m.Length;
                            continue;
                        }
                    }

                    m = ShortcutLinkAt.Match(text, start);
                    if (m.Success && ctx.References.TryGetValue(m.Groups[1].Value.Trim(), out var shortcutUrl))
                    {
                        Flush();
                        ParseInline(m.Groups[1].Value, style, shortcutUrl, color, ctx, runs);
                        i = start + m.Length;
                        continue;
                    }
                }

                if (ch == '<')
                {
                    var br = BreakAt.Match(text, i);
                    if (br.Success)
                    {
                        Flush();
                        runs.Add(new InlineRun(LineBreak.ToString(), style, link, color));
                        i += br.Length;
                        continue;
                    }

                    if (link == null)
                    {
                        var auto = AutolinkAt.Match(text, i);
                        if (auto.Success)
                        {
                            Flush();
                            runs.Add(new InlineRun(auto.Groups[1].Value, style, auto.Groups[1].Value, color));
                            i += auto.Length;
                            continue;
                        }
                    }

                    var m = SpanAt.Match(text, i);
                    if (!m.Success) m = FontAt.Match(text, i);
                    if (m.Success)
                    {
                        Flush();
                        ParseInline(m.Groups[2].Value, style, link, ParseColor(m.Groups[1].Value), ctx, runs);
                        i += m.Length;
                        continue;
                    }
                }

                if (TryDelimited(text, i, "**", out var end, out var inner) ||
                    TryDelimited(text, i, "__", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Bold, link, color, ctx, runs);
                    i = end;
                    continue;
                }

                if (TryDelimited(text, i, "~~", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Strike, link, color, ctx, runs);
                    i = end;
                    continue;
                }

                if (TryDelimited(text, i, "*", out end, out inner) ||
                    TryDelimited(text, i, "_", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Italic, link, color, ctx, runs);
                    i = end;
                    continue;
                }

                plain.Append(ch);
                i++;
            }

            Flush();
        }

        static bool TryDelimited(string text, int start, string marker, out int end, out string inner)
        {
            end = 0;
            inner = null;
            if (string.CompareOrdinal(text, start, marker, 0, marker.Length) != 0) return false;

            var intraword = marker[0] == '_';
            if (intraword && start > 0 && char.IsLetterOrDigit(text[start - 1])) return false;

            var contentStart = start + marker.Length;
            if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart])) return false;
            if (marker.Length == 1 && text[contentStart] == marker[0]) return false;

            var search = contentStart;
            while (true)
            {
                var close = text.IndexOf(marker, search, StringComparison.Ordinal);
                if (close < 0) return false;
                var after = close + marker.Length;
                var doubled = marker.Length == 1 && after < text.Length && text[after] == marker[0];
                var validClose = close > contentStart
                    && !char.IsWhiteSpace(text[close - 1])
                    && !doubled
                    && !(intraword && after < text.Length && char.IsLetterOrDigit(text[after]));
                if (validClose)
                {
                    inner = text.Substring(contentStart, close - contentStart);
                    end = after;
                    return true;
                }
                search = doubled ? after + 1 : close + 1;
            }
        }
    }
}
