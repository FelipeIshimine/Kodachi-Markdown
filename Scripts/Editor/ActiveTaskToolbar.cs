using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Shows the current <see cref="ActiveTask"/> in the main editor toolbar: a play/pause
    /// glyph, the running timer, and the task name. Click for a menu to pause/resume, reset
    /// the timer, clear the task, or open the Markdown Pin popup. When no task is active it
    /// collapses to a small clock that opens the popup.
    /// </summary>
    public static class ActiveTaskToolbar
    {
        public const string Id = "Kodachi/ActiveTask";

        [MainToolbarElement(Id, defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement Create()
        {
            if (!ActiveTask.HasActive)
            {
                var idle = new MainToolbarContent("⏱",
                    "No active task.\nIn the Markdown Pin popup's Task view, right-click a task → \"Set as Active Task\".");
                return new MainToolbarButton(idle, MarkdownPinPopup.Toggle);
            }

            var glyph = ActiveTask.IsRunning ? "▶" : "⏸";
            var name  = Truncate(ActiveTask.Text, 32);
            var content = new MainToolbarContent(
                $"{glyph} {ActiveTask.Format(ActiveTask.ElapsedSeconds)}  {name}",
                $"Active task: {ActiveTask.Text}\nFile: {ActiveTask.Rel}\nClick for options.");

            return new MainToolbarDropdown(content, ShowMenu);
        }

        static void ShowMenu(Rect rect)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(ActiveTask.IsRunning ? "Pause timer" : "Resume timer"),
                false, ActiveTask.TogglePause);
            menu.AddItem(new GUIContent("Reset timer"), false, ActiveTask.ResetTimer);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Clear active task"), false, ActiveTask.Clear);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open Markdown popup"), false, MarkdownPinPopup.Toggle);

            menu.DropDown(rect);
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1).TrimEnd() + "…";
        }
    }
}
