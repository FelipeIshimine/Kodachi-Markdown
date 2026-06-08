using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    /// Inline emphasis markers are stripped for readability rather than styled per-run.
    /// </summary>
    public static class MarkdownView
    {
        static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Checkbox = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Numbered = new(@"^(\s*)(\d+)\.\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Quote = new(@"^>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex InlineCode = new("`([^`]+)`", RegexOptions.Compiled);
        static readonly Regex Emphasis = new(@"(\*\*|\*)(.+?)\1", RegexOptions.Compiled);
        static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        static readonly Color CodeColor = new(0.79f, 0.64f, 0.43f);
        static readonly Color MutedColor = new(1f, 1f, 1f, 0.55f);

        /// <param name="onCheckboxToggled">
        /// Invoked when a task-list checkbox (<c>- [ ]</c> / <c>- [x]</c>) is clicked, with the
        /// zero-based source line index and the new checked state. Pass <c>null</c> to render
        /// checkboxes as read-only (e.g. when the source text is truncated).
        /// </param>
        public static void Populate(VisualElement container, string markdown, Action<int, bool> onCheckboxToggled = null)
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
                container.Add(Paragraph(Clean(string.Join(" ", paragraph))));
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
                    container.Add(HeadingLabel(h.Groups[1].Value.Length, Clean(h.Groups[2].Value)));
                    continue;
                }

                var q = Quote.Match(line);
                if (q.Success)
                {
                    FlushParagraph();
                    container.Add(QuoteLabel(Clean(q.Groups[1].Value)));
                    continue;
                }

                var c = Checkbox.Match(line);
                if (c.Success)
                {
                    FlushParagraph();
                    var indent = c.Groups[1].Value.Length;
                    var isChecked = c.Groups[2].Value is "x" or "X";
                    var sourceLine = lineIndex;
                    container.Add(CheckboxItem(indent, isChecked, Clean(c.Groups[3].Value),
                        onCheckboxToggled == null ? null : v => onCheckboxToggled(sourceLine, v)));
                    continue;
                }

                var b = Bullet.Match(line);
                if (b.Success)
                {
                    FlushParagraph();
                    container.Add(ListItem(b.Groups[1].Value.Length, "•  " + Clean(b.Groups[2].Value)));
                    continue;
                }

                var n = Numbered.Match(line);
                if (n.Success)
                {
                    FlushParagraph();
                    container.Add(ListItem(n.Groups[1].Value.Length, $"{n.Groups[2].Value}.  {Clean(n.Groups[3].Value)}"));
                    continue;
                }

                paragraph.Add(trimmed);
            }

            if (inFence && fence.Count > 0) container.Add(CodeBlock(fence));
            FlushParagraph();
        }

        static Label PlainLabel(string text)
        {
            var label = new Label(text) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.selection.isSelectable = true;
            return label;
        }

        static Label Paragraph(string text)
        {
            var label = PlainLabel(text);
            label.style.marginBottom = 6;
            return label;
        }

        static Label HeadingLabel(int level, string text)
        {
            var label = PlainLabel(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = level switch { 1 => 20, 2 => 17, 3 => 15, 4 => 14, _ => 13 };
            label.style.marginTop = 8;
            label.style.marginBottom = 4;
            return label;
        }

        static Label QuoteLabel(string text)
        {
            var label = PlainLabel(text);
            label.style.unityFontStyleAndWeight = FontStyle.Italic;
            label.style.color = MutedColor;
            label.style.paddingLeft = 8;
            label.style.borderLeftWidth = 3;
            label.style.borderLeftColor = MutedColor;
            label.style.marginBottom = 4;
            return label;
        }

        static Label ListItem(int indent, string text)
        {
            var label = PlainLabel(text);
            label.style.marginLeft = 12 + indent;
            label.style.marginBottom = 2;
            return label;
        }

        static VisualElement CheckboxItem(int indent, bool isChecked, string text, Action<bool> onToggled)
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

            var label = PlainLabel(text);
            label.style.flexGrow = 1;
            if (isChecked)
            {
                label.style.color = MutedColor;
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
            }
            row.Add(label);
            return row;
        }

        static VisualElement CodeBlock(List<string> codeLines)
        {
            var box = new VisualElement
            {
                style =
                {
                    backgroundColor = new Color(0f, 0f, 0f, 0.25f),
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

        /// <summary>Strips inline markup so emphasized/code/link text reads cleanly as plain text.</summary>
        static string Clean(string text)
        {
            text = InlineCode.Replace(text, "$1");
            text = Link.Replace(text, "$1");
            text = Emphasis.Replace(text, "$2");
            return text;
        }
    }
}
