using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    public sealed class MarkdownFindBar : VisualElement
    {
        const string MatchMark = "<mark=#FFD70055>";
        const string CurrentMark = "<mark=#FF8C00AA>";
        const string MarkEnd = "</mark>";

        readonly ScrollView _scroll;
        readonly VisualElement _content;
        readonly TextField _query;
        readonly Label _count;
        readonly List<Hit> _hits = new();
        readonly Dictionary<TextElement, string> _originals = new();
        int _current;

        abstract class Hit
        {
            public int Start;
            public int Length;
        }

        sealed class LabelHit : Hit
        {
            public TextElement Label;
            public int VisibleStart;
        }

        sealed class FieldHit : Hit
        {
            public TextField Field;
        }

        public bool IsOpen => style.display == DisplayStyle.Flex;

        public MarkdownFindBar(ScrollView scroll, VisualElement content)
        {
            _scroll = scroll;
            _content = content;

            style.display = DisplayStyle.None;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.flexShrink = 0;
            style.marginBottom = 4;

            _query = new TextField { style = { flexGrow = 1 } };
            _query.RegisterValueChangedCallback(_ => Search(0, true));
            _query.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            Add(_query);

            _count = new Label { style = { minWidth = 60, unityTextAlign = TextAnchor.MiddleCenter } };
            Add(_count);

            Add(new Button(() => Step(-1)) { text = "▲", tooltip = "Previous (Shift+Enter)" });
            Add(new Button(() => Step(1)) { text = "▼", tooltip = "Next (Enter)" });
            Add(new Button(Close) { text = "✕", tooltip = "Close (Esc)" });
        }

        public void Open()
        {
            style.display = DisplayStyle.Flex;
            _query.Focus();
            _query.SelectAll();
            Search(_current, true);
        }

        public void Close()
        {
            RestoreLabels();
            var current = _hits.Count > 0 ? _hits[_current] : null;
            _hits.Clear();
            style.display = DisplayStyle.None;
            if (current is FieldHit field)
            {
                field.Field.Focus();
                field.Field.SelectRange(field.Start, field.Start + field.Length);
            }
        }

        public void Refresh()
        {
            if (!IsOpen) return;
            _originals.Clear();
            Search(_current, false);
        }

        void OnKeyDown(KeyDownEvent e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.F3:
                    Step(e.shiftKey ? -1 : 1);
                    break;
                case KeyCode.Escape:
                    Close();
                    break;
                default:
                    return;
            }
            e.StopPropagation();
            focusController.IgnoreEvent(e);
        }

        void Step(int dir)
        {
            if (_hits.Count == 0) return;
            _current = (_current + dir + _hits.Count) % _hits.Count;
            ApplyMarks();
            ScrollToCurrent();
            UpdateCount();
        }

        void Search(int current, bool scroll)
        {
            RestoreLabels();
            _hits.Clear();

            var query = _query.value;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (var element in _content.Query<VisualElement>().ToList())
                {
                    if (element is TextField field)
                        CollectFieldHits(field, query);
                    else if (element is TextElement label && label.GetFirstAncestorOfType<TextField>() == null)
                        CollectLabelHits(label, query);
                }
            }

            _current = _hits.Count == 0 ? 0 : Mathf.Min(current, _hits.Count - 1);
            ApplyMarks();
            if (scroll && _hits.Count > 0) ScrollToCurrent();
            UpdateCount();
        }

        void CollectFieldHits(TextField field, string query)
        {
            var text = field.value;
            for (var i = text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = text.IndexOf(query, i + query.Length, System.StringComparison.OrdinalIgnoreCase))
                _hits.Add(new FieldHit { Field = field, Start = i, Length = query.Length });
        }

        void CollectLabelHits(TextElement label, string query)
        {
            var raw = label.text;
            if (string.IsNullOrEmpty(raw)) return;
            var (visible, map) = VisibleText(raw, label.enableRichText);
            for (var i = visible.IndexOf(query, System.StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = visible.IndexOf(query, i + query.Length, System.StringComparison.OrdinalIgnoreCase))
            {
                _originals.TryAdd(label, raw);
                _hits.Add(new LabelHit
                {
                    Label = label,
                    VisibleStart = i,
                    Start = map[i],
                    Length = map[i + query.Length - 1] + 1 - map[i]
                });
            }
        }

        static (string visible, List<int> map) VisibleText(string raw, bool richText)
        {
            var visible = new StringBuilder(raw.Length);
            var map = new List<int>(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                if (richText && raw[i] == '<')
                {
                    var close = raw.IndexOf('>', i);
                    if (close > i)
                    {
                        i = close;
                        continue;
                    }
                }
                visible.Append(raw[i]);
                map.Add(i);
            }
            return (visible.ToString(), map);
        }

        void ApplyMarks()
        {
            var current = _hits.Count > 0 ? _hits[_current] : null;
            foreach (var group in _hits.OfType<LabelHit>().GroupBy(h => h.Label))
            {
                var label = group.Key;
                if (!label.enableRichText) continue;
                var text = new StringBuilder(_originals[label]);
                foreach (var hit in group.OrderByDescending(h => h.Start))
                {
                    text.Insert(hit.Start + hit.Length, MarkEnd);
                    text.Insert(hit.Start, hit == current ? CurrentMark : MatchMark);
                }
                label.text = text.ToString();
            }
        }

        void RestoreLabels()
        {
            foreach (var (label, text) in _originals)
                label.text = text;
            _originals.Clear();
        }

        void ScrollToCurrent()
        {
            switch (_hits[_current])
            {
                case LabelHit hit:
                    _scroll.ScrollTo(hit.Label);
                    break;
                case FieldHit hit:
                    var text = hit.Field.value;
                    var lines = text.Count(c => c == '\n') + 1;
                    var line = text.Take(hit.Start).Count(c => c == '\n');
                    var local = new Vector2(0, hit.Field.layout.height * line / lines);
                    var y = hit.Field.ChangeCoordinatesTo(_scroll.contentContainer, local).y;
                    _scroll.scrollOffset = new Vector2(_scroll.scrollOffset.x, Mathf.Max(0, y - _scroll.contentViewport.layout.height / 3));
                    hit.Field.SelectRange(hit.Start, hit.Start + hit.Length);
                    break;
            }
        }

        void UpdateCount()
        {
            _count.text = string.IsNullOrEmpty(_query.value) ? ""
                : _hits.Count == 0 ? "0 / 0"
                : $"{_current + 1} / {_hits.Count}";
        }
    }
}
