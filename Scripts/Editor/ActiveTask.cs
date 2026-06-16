using System;
using UnityEditor;
using UnityEditor.Toolbars;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Tracks the single "active task" the user is currently working on, together with a
    /// pausable stopwatch. The active task is surfaced in the main editor toolbar
    /// (<see cref="ActiveTaskToolbar"/>) and highlighted at the top of the Pin popup's
    /// Task view.
    ///
    /// Identity is the pair (file rel-path, task text) so it survives line shuffles and
    /// priority edits. The chosen task persists across sessions (EditorPrefs); the elapsed
    /// time lives for the current editor session (SessionState), measured against
    /// <see cref="EditorApplication.timeSinceStartup"/> which survives domain reloads.
    /// </summary>
    [InitializeOnLoad]
    public static class ActiveTask
    {
        const string RelKey   = "KodachiMarkdown.ActiveTask.Rel";    // EditorPrefs (persists)
        const string TextKey  = "KodachiMarkdown.ActiveTask.Text";
        const string AccKey   = "KodachiMarkdown.ActiveTask.Acc";    // SessionState (per session)
        const string SinceKey = "KodachiMarkdown.ActiveTask.Since";
        const string RunKey   = "KodachiMarkdown.ActiveTask.Run";

        /// <summary>Raised when the active task or its run-state changes (not every tick).</summary>
        public static event Action Changed;

        static double _lastWholeSecond = -1;

        static ActiveTask() => EditorApplication.update += OnUpdate;

        // ── Query ─────────────────────────────────────────────────────────────────

        public static bool   HasActive => !string.IsNullOrEmpty(Rel);
        public static string Rel       => EditorPrefs.GetString(RelKey, "");
        public static string Text      => EditorPrefs.GetString(TextKey, "");
        public static bool   IsRunning => SessionState.GetBool(RunKey, false);

        public static bool IsActive(string rel, string text)
            => HasActive && Rel == rel && Text == text;

        public static double ElapsedSeconds
        {
            get
            {
                double acc = SessionState.GetFloat(AccKey, 0f);
                if (IsRunning)
                    acc += EditorApplication.timeSinceStartup - SessionState.GetFloat(SinceKey, (float)EditorApplication.timeSinceStartup);
                return acc < 0 ? 0 : acc;
            }
        }

        // ── Mutate ─────────────────────────────────────────────────────────────────

        public static void Set(string rel, string text)
        {
            EditorPrefs.SetString(RelKey, rel);
            EditorPrefs.SetString(TextKey, text);
            SessionState.SetFloat(AccKey, 0f);
            StartClock();
            Notify();
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(RelKey);
            EditorPrefs.DeleteKey(TextKey);
            SessionState.SetBool(RunKey, false);
            SessionState.SetFloat(AccKey, 0f);
            Notify();
        }

        // Mirrors the cleaning pipeline in TaskView.Parse so we can match stored text back to raw lines.
        static readonly System.Text.RegularExpressions.Regex _reCheckbox  = new(@"^(\s*[-*+]\s+)\[[ ]\]\s+(.*)$");
        static readonly System.Text.RegularExpressions.Regex _rePriority  = new(@"\{[Pp]:-?\d+\}");
        static readonly System.Text.RegularExpressions.Regex _reInlineCode = new("`([^`]+)`");
        static readonly System.Text.RegularExpressions.Regex _reEmphasis  = new(@"(\*\*|\*)(.+?)\1");
        static readonly System.Text.RegularExpressions.Regex _reLink      = new(@"\[([^\]]+)\]\(([^)]+)\)");

        static string CleanTaskText(string raw)
        {
            raw = _rePriority.Replace(raw, "").Trim();
            raw = _reInlineCode.Replace(raw, "$1");
            raw = _reLink.Replace(raw, "$1");
            raw = _reEmphasis.Replace(raw, "$2");
            return raw;
        }

        /// <summary>
        /// Marks the active task's checkbox as [x] in the source file, then clears the active task.
        /// </summary>
        public static void Complete()
        {
            if (!HasActive) return;

            var fullPath = MarkdownPins.ToFull(Rel);
            if (System.IO.File.Exists(fullPath))
            {
                var text = System.IO.File.ReadAllText(fullPath);
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                var taskText = Text;
                for (var i = 0; i < lines.Length; i++)
                {
                    var m = _reCheckbox.Match(lines[i]);
                    if (!m.Success) continue;
                    if (!string.Equals(CleanTaskText(m.Groups[2].Value), taskText, System.StringComparison.Ordinal)) continue;

                    lines[i] = m.Groups[1].Value + "[x] " + m.Groups[2].Value;
                    System.IO.File.WriteAllText(fullPath, string.Join("\n", lines));
                    break;
                }
            }

            Clear();
        }

        public static void Pause()
        {
            if (!IsRunning) return;
            SessionState.SetFloat(AccKey, (float)ElapsedSeconds); // fold the running span in
            SessionState.SetBool(RunKey, false);
            Notify();
        }

        public static void Resume()
        {
            if (!HasActive || IsRunning) return;
            StartClock();
            Notify();
        }

        public static void TogglePause()
        {
            if (IsRunning) Pause(); else Resume();
        }

        public static void ResetTimer()
        {
            SessionState.SetFloat(AccKey, 0f);
            if (IsRunning) StartClock();
            Notify();
        }

        static void StartClock()
        {
            SessionState.SetFloat(SinceKey, (float)EditorApplication.timeSinceStartup);
            SessionState.SetBool(RunKey, true);
        }

        // ── Toolbar tick ────────────────────────────────────────────────────────────

        static void OnUpdate()
        {
            if (!HasActive || !IsRunning) return;
            var whole = Math.Floor(ElapsedSeconds);
            if (whole == _lastWholeSecond) return;
            _lastWholeSecond = whole;
            MainToolbar.Refresh(ActiveTaskToolbar.Id);
        }

        static void Notify()
        {
            _lastWholeSecond = -1;
            Changed?.Invoke();
            MainToolbar.Refresh(ActiveTaskToolbar.Id);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────

        public static string Format(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var s = (int)seconds;
            int h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
        }
    }
}
