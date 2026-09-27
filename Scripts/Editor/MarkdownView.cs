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
        static readonly Regex Checkbox = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Numbered = new(@"^(\s*)(\d+)\.\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Quote = new(@"^>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex LinkAt = new(@"\G\[([^\]]*)\]\(\s*<?([^)\s>]*)>?(?:\s+""[^""]*"")?\s*\)", RegexOptions.Compiled);
        static readonly Regex AutolinkAt = new(@"\G<((?:https?|mailto):[^>\s]+)>", RegexOptions.Compiled);
        static readonly Regex Words = new(@"\S+\s*|\s+", RegexOptions.Compiled);
        static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);

        static readonly Color CodeColor = new(0.79f, 0.64f, 0.43f);
        static readonly Color CodeBackground = new(0f, 0f, 0f, 0.25f);
        static readonly Color LinkColor = new(0.45f, 0.7f, 1f);
        static readonly Color MutedColor = new(1f, 1f, 1f, 0.55f);
        static readonly Color TableBorderColor = new(1f, 1f, 1f, 0.15f);
        static readonly Color TableHeaderColor = new(1f, 1f, 1f, 0.08f);
        static readonly Color TableStripeColor = new(1f, 1f, 1f, 0.03f);

        static readonly Regex AlertMarker = new(@"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex SpanAt = new(@"\G<span\s+style\s*=\s*[""']\s*color\s*:\s*([^;""']+?)\s*;?\s*[""']\s*>(.*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex FontAt = new(@"\G<font\s+color\s*=\s*[""']?([^""'\s>]+)[""']?\s*>(.*?)</font>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        const int BodyFontSize = 12;

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

        /// <param name="onCheckboxToggled">
        /// Invoked when a task-list checkbox (<c>- [ ]</c> / <c>- [x]</c>) is clicked, with the
        /// zero-based source line index and the new checked state. Pass <c>null</c> to render
        /// checkboxes as read-only (e.g. when the source text is truncated).
        /// </param>
        public static void Populate(VisualElement container, string markdown, Action<int, bool> onCheckboxToggled = null, string documentPath = null)
        {
            container.Clear();
            if (string.IsNullOrEmpty(markdown)) return;

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var paragraph = new List<string>();
            var fence = new List<string>();
            var inFence = false;

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                var block = Inline(string.Join(" ", paragraph), InlineStyle.None, BodyFontSize, documentPath);
                block.style.marginBottom = 6;
                container.Add(block);
                paragraph.Clear();
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.TrimStart().StartsWith("```"))
                {
                    if (inFence) { container.Add(CodeBlock(fence)); fence.Clear(); inFence = false; }
                    else { FlushParagraph(); inFence = true; }
                    continue;
                }

                if (inFence) { fence.Add(line); continue; }

                if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }

                if (IsTableStart(lines, lineIndex))
                {
                    FlushParagraph();
                    var header = SplitRow(line);
                    var alignments = ParseAlignments(SplitRow(lines[lineIndex + 1]));
                    var rows = new List<string[]>();
                    lineIndex += 2;
                    while (lineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[lineIndex]) && lines[lineIndex].Contains('|'))
                    {
                        rows.Add(SplitRow(lines[lineIndex]));
                        lineIndex++;
                    }
                    lineIndex--;
                    container.Add(Table(header, alignments, rows, documentPath));
                    continue;
                }

                var trimmed = line.Trim();
                if (trimmed is "---" or "***" or "___")
                {
                    FlushParagraph();
                    container.Add(Rule());
                    continue;
                }

                var h = Heading.Match(line);
                if (h.Success)
                {
                    FlushParagraph();
                    container.Add(HeadingBlock(h.Groups[1].Value.Length, h.Groups[2].Value, documentPath));
                    continue;
                }

                if (Quote.IsMatch(line))
                {
                    FlushParagraph();
                    var quoteLines = new List<string>();
                    while (lineIndex < lines.Length && Quote.IsMatch(lines[lineIndex]))
                    {
                        quoteLines.Add(Quote.Match(lines[lineIndex]).Groups[1].Value);
                        lineIndex++;
                    }
                    lineIndex--;
                    var alert = AlertMarker.Match(quoteLines[0]);
                    container.Add(alert.Success
                        ? AlertBlock(ParseAlertKind(alert.Groups[1].Value), quoteLines.GetRange(1, quoteLines.Count - 1), documentPath)
                        : QuoteBlock(quoteLines, documentPath));
                    continue;
                }

                var c = Checkbox.Match(line);
                if (c.Success)
                {
                    FlushParagraph();
                    var indent = c.Groups[1].Value.Length;
                    var isChecked = c.Groups[2].Value is "x" or "X";
                    var sourceLine = lineIndex;
                    container.Add(CheckboxItem(indent, isChecked, c.Groups[3].Value, documentPath,
                        onCheckboxToggled == null ? null : v => onCheckboxToggled(sourceLine, v)));
                    continue;
                }

                var b = Bullet.Match(line);
                if (b.Success)
                {
                    FlushParagraph();
                    container.Add(ListItem(b.Groups[1].Value.Length, "•", b.Groups[2].Value, documentPath));
                    continue;
                }

                var n = Numbered.Match(line);
                if (n.Success)
                {
                    FlushParagraph();
                    container.Add(ListItem(n.Groups[1].Value.Length, n.Groups[2].Value + ".", n.Groups[3].Value, documentPath));
                    continue;
                }

                paragraph.Add(trimmed);
            }

            if (inFence && fence.Count > 0) container.Add(CodeBlock(fence));
            FlushParagraph();
        }

        static VisualElement HeadingBlock(int level, string text, string documentPath)
        {
            var fontSize = level switch { 1 => 20, 2 => 17, 3 => 15, 4 => 14, _ => 13 };
            var block = Inline(text, InlineStyle.Bold, fontSize, documentPath);
            block.style.marginTop = 8;
            block.style.marginBottom = 4;
            return block;
        }

        static VisualElement QuoteBlock(List<string> quoteLines, string documentPath)
        {
            var block = new VisualElement();
            block.style.color = MutedColor;
            block.style.paddingLeft = 8;
            block.style.borderLeftWidth = 3;
            block.style.borderLeftColor = MutedColor;
            block.style.marginBottom = 6;
            AddQuoteParagraphs(block, quoteLines, InlineStyle.Italic, documentPath);
            return block;
        }

        static VisualElement AlertBlock(AlertKind kind, List<string> bodyLines, string documentPath)
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

            AddQuoteParagraphs(block, bodyLines, InlineStyle.None, documentPath);
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

        static void AddQuoteParagraphs(VisualElement block, List<string> quoteLines, InlineStyle style, string documentPath)
        {
            var paragraph = new List<string>();

            void Flush()
            {
                if (paragraph.Count == 0) return;
                var body = Inline(string.Join(" ", paragraph), style, BodyFontSize, documentPath);
                body.style.marginBottom = 2;
                block.Add(body);
                paragraph.Clear();
            }

            foreach (var quoteLine in quoteLines)
            {
                if (string.IsNullOrWhiteSpace(quoteLine)) Flush();
                else paragraph.Add(quoteLine.Trim());
            }
            Flush();
        }

        static VisualElement ListItem(int indent, string marker, string text, string documentPath)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.FlexStart,
                    marginLeft = 12 + indent,
                    marginBottom = 2
                }
            };

            var markerLabel = new Label(marker) { enableRichText = false };
            markerLabel.style.minWidth = 14;
            markerLabel.style.marginLeft = 0;
            markerLabel.style.marginRight = 4;
            markerLabel.style.paddingLeft = 0;
            markerLabel.style.paddingRight = 0;
            row.Add(markerLabel);

            var body = Inline(text, InlineStyle.None, BodyFontSize, documentPath);
            body.style.flexGrow = 1;
            body.style.flexShrink = 1;
            row.Add(body);
            return row;
        }

        static VisualElement CheckboxItem(int indent, bool isChecked, string text, string documentPath, Action<bool> onToggled)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.FlexStart,
                    marginLeft = 12 + indent,
                    marginBottom = 2
                }
            };

            var toggle = new Toggle { value = isChecked, style = { marginRight = 4, marginTop = 1 } };
            toggle.SetEnabled(onToggled != null);
            if (onToggled != null)
                toggle.RegisterValueChangedCallback(evt => onToggled(evt.newValue));
            row.Add(toggle);

            var body = Inline(text, isChecked ? InlineStyle.Italic : InlineStyle.None, BodyFontSize, documentPath);
            body.style.flexGrow = 1;
            body.style.flexShrink = 1;
            if (isChecked) body.style.color = MutedColor;
            row.Add(body);
            return row;
        }

        static VisualElement CodeBlock(List<string> codeLines)
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

            var label = new Label(string.Join("\n", codeLines)) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = CodeColor;
            label.selection.isSelectable = true;
            box.Add(label);
            return box;
        }

        static bool IsTableStart(string[] lines, int index)
        {
            if (index + 1 >= lines.Length || !lines[index].Contains('|')) return false;
            if (!TableSeparator.IsMatch(lines[index + 1])) return false;
            return SplitRow(lines[index]).Length == SplitRow(lines[index + 1]).Length;
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

        static VisualElement Table(string[] header, Justify[] alignments, List<string[]> rows, string documentPath)
        {
            var columns = header.Length;
            var weights = new float[columns];
            for (var col = 0; col < columns; col++)
                weights[col] = PlainText(header[col]).Length;
            foreach (var row in rows)
                for (var col = 0; col < columns && col < row.Length; col++)
                    weights[col] = Mathf.Max(weights[col], PlainText(row[col]).Length);
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

            table.Add(TableRow(header, alignments, weights, true, false, documentPath));
            for (var r = 0; r < rows.Count; r++)
                table.Add(TableRow(rows[r], alignments, weights, false, r % 2 == 1, documentPath));
            return table;
        }

        static VisualElement TableRow(string[] cells, Justify[] alignments, float[] weights, bool isHeader, bool isStriped, string documentPath)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            if (isHeader) row.style.backgroundColor = TableHeaderColor;
            else if (isStriped) row.style.backgroundColor = TableStripeColor;

            for (var col = 0; col < weights.Length; col++)
            {
                var cell = Inline(col < cells.Length ? cells[col] : string.Empty,
                    isHeader ? InlineStyle.Bold : InlineStyle.None, BodyFontSize, documentPath);
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

        static VisualElement Inline(string text, InlineStyle baseStyle, int fontSize, string documentPath)
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
            ParseInline(text, baseStyle, null, null, runs);
            var spaceWidth = Mathf.Round(fontSize * 0.3f);
            foreach (var run in runs)
                foreach (Match word in Words.Matches(run.Text))
                    flow.Add(Word(word.Value, run, spaceWidth, documentPath));
            return flow;
        }

        static VisualElement Word(string word, InlineRun run, float spaceWidth, string documentPath)
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
                label.RegisterCallback<ClickEvent>(_ => OpenLink(target, documentPath));
            }

            if (run.Color is { } color)
            {
                label.style.color = color;
                if (run.Link != null) label.style.borderBottomColor = color;
            }

            return label;
        }

        static void OpenLink(string link, string documentPath)
        {
            if (link.Contains("://") || link.StartsWith("mailto:"))
            {
                Application.OpenURL(link);
                return;
            }

            var hash = link.IndexOf('#');
            var relative = Uri.UnescapeDataString(hash >= 0 ? link.Substring(0, hash) : link);
            if (relative.Length == 0) return;
            if (documentPath == null)
                throw new InvalidOperationException($"Cannot resolve relative link '{link}': the Markdown was rendered without a document path.");

            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentPath), relative));
            if (fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                MarkdownDocumentWindow.Open(fullPath);
            else
                EditorUtility.OpenWithDefaultApp(fullPath);
        }

        static Color ParseColor(string value)
        {
            if (!ColorUtility.TryParseHtmlString(value.Trim(), out var color))
                throw new FormatException($"Markdown colour '{value}' is not a hex code or a Unity HTML colour name.");
            return color;
        }

        static string PlainText(string text)
        {
            var runs = new List<InlineRun>();
            ParseInline(text, InlineStyle.None, null, null, runs);
            var builder = new StringBuilder();
            foreach (var run in runs) builder.Append(run.Text);
            return builder.ToString();
        }

        static void ParseInline(string text, InlineStyle style, string link, Color? color, List<InlineRun> runs)
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
                        ParseInline(m.Groups[1].Value, style, m.Groups[2].Value, color, runs);
                        i = start + m.Length;
                        continue;
                    }
                }

                if (link == null && ch == '<')
                {
                    var m = AutolinkAt.Match(text, i);
                    if (m.Success)
                    {
                        Flush();
                        runs.Add(new InlineRun(m.Groups[1].Value, style, m.Groups[1].Value, color));
                        i += m.Length;
                        continue;
                    }
                }

                if (ch == '<')
                {
                    var m = SpanAt.Match(text, i);
                    if (!m.Success) m = FontAt.Match(text, i);
                    if (m.Success)
                    {
                        Flush();
                        ParseInline(m.Groups[2].Value, style, link, ParseColor(m.Groups[1].Value), runs);
                        i += m.Length;
                        continue;
                    }
                }

                if (TryDelimited(text, i, "**", out var end, out var inner) ||
                    TryDelimited(text, i, "__", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Bold, link, color, runs);
                    i = end;
                    continue;
                }

                if (TryDelimited(text, i, "~~", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Strike, link, color, runs);
                    i = end;
                    continue;
                }

                if (TryDelimited(text, i, "*", out end, out inner) ||
                    TryDelimited(text, i, "_", out end, out inner))
                {
                    Flush();
                    ParseInline(inner, style | InlineStyle.Italic, link, color, runs);
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
