// ============================================================
// Script:      GameLogger.cs
// Description: Central logging system. Provides log-level filtering,
//              optional stack traces, color-coded console output,
//              and a global kill switch for shipping builds.
//              Used internally by IDebuggable extension methods.
// Author:      Goodgulf
// ============================================================

using System.Runtime.CompilerServices;
using UnityEngine;

namespace Goodgulf.Logging
{
    /// <summary>
    /// Severity level for a log message. Only messages at or above
    /// <see cref="GameLogger.GlobalMinLevel"/> are written to the console.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>Fine-grained tracing; highest volume, lowest severity.</summary>
        Verbose = 0,

        /// <summary>General informational messages about normal operation.</summary>
        Info = 1,

        /// <summary>Recoverable issues that may indicate a problem.</summary>
        Warning = 2,

        /// <summary>Failures that need immediate attention.</summary>
        Error = 3,

        /// <summary>Suppress all output. Assign to <see cref="GameLogger.GlobalMinLevel"/> to silence everything.</summary>
        None = 4
    }

    /// <summary>
    /// Static logging gateway used by the whole project.
    ///
    /// <para>
    /// Call site information (class name, method, line number) is captured at
    /// compile time via <c>[CallerMemberName]</c> / <c>[CallerFilePath]</c> /
    /// <c>[CallerLineNumber]</c> — zero reflection cost at runtime.
    /// </para>
    ///
    /// <para>
    /// For per-instance Inspector toggles on MonoBehaviours, implement
    /// <see cref="Goodgulf.Logging.IDebuggable"/> and call the extension
    /// methods defined in <c>DebuggableExtensions</c> instead of calling
    /// this class directly.
    /// </para>
    /// </summary>
    public static class GameLogger
    {
        // ── Global controls ──────────────────────────────────────────────────

        /// <summary>
        /// Minimum severity that will be written to the console.
        /// Messages below this level are discarded before any string is built.
        /// Set to <see cref="LogLevel.None"/> to suppress all output (e.g. in release builds).
        /// </summary>
        public static LogLevel GlobalMinLevel = LogLevel.Verbose;

        /// <summary>
        /// When true, a managed stack trace is appended to every message.
        /// Has a non-trivial runtime cost; keep false in production.
        /// </summary>
        public static bool ShowStackTrace = false;

        // ── Rich-text colours (Unity Console supports a subset of HTML colour names) ──

        // Colour tokens used to tag message prefixes in the Unity Console.
        private const string ColorVerbose = "#A0A0A0"; // grey
        private const string ColorInfo    = "#FFFFFF"; // white
        private const string ColorWarning = "#FFA500"; // orange  (distinct from Unity's built-in yellow)
        private const string ColorError   = "#FF4444"; // red

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Write a <see cref="LogLevel.Verbose"/> message.
        /// Automatically decorated with the caller's class name, method, and line number.
        /// </summary>
        /// <param name="message">Human-readable log text.</param>
        /// <param name="context">Optional Unity Object highlighted when the log entry is clicked.</param>
        /// <param name="callerMethod">Filled automatically by the compiler — do not pass manually.</param>
        /// <param name="callerFile">Filled automatically by the compiler — do not pass manually.</param>
        /// <param name="callerLine">Filled automatically by the compiler — do not pass manually.</param>
        public static void Verbose(
            string message,
            Object context = null,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
            => Write(LogLevel.Verbose, message, context, callerMethod, callerFile, callerLine);

        /// <summary>Write an <see cref="LogLevel.Info"/> message.</summary>
        /// <inheritdoc cref="Verbose"/>
        public static void Info(
            string message,
            Object context = null,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
            => Write(LogLevel.Info, message, context, callerMethod, callerFile, callerLine);

        /// <summary>Write a <see cref="LogLevel.Warning"/> message.</summary>
        /// <inheritdoc cref="Verbose"/>
        public static void Warning(
            string message,
            Object context = null,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
            => Write(LogLevel.Warning, message, context, callerMethod, callerFile, callerLine);

        /// <summary>Write an <see cref="LogLevel.Error"/> message.</summary>
        /// <inheritdoc cref="Verbose"/>
        public static void Error(
            string message,
            Object context = null,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
            => Write(LogLevel.Error, message, context, callerMethod, callerFile, callerLine);

        // ── Core dispatch ─────────────────────────────────────────────────────

        /// <summary>
        /// Core log dispatch. Builds the formatted string only when the level
        /// passes the global filter, then routes to the appropriate
        /// <c>Debug.*</c> method.
        /// </summary>
        public static void Write(
            LogLevel level,
            string   message,
            Object   context,
            string   callerMethod,
            string   callerFile,
            int      callerLine)
        {
            // Early-out before any string allocation when logging is suppressed.
            if (level < GlobalMinLevel)
                return;

            string className = System.IO.Path.GetFileNameWithoutExtension(callerFile);
            string colour     = LevelColour(level);
            string levelTag   = level.ToString().ToUpperInvariant();

            // Build the prefix: coloured level tag + class.method:line
            string prefix = $"<color={colour}>[{levelTag}]</color> <b>{className}.{callerMethod}</b>:{callerLine}";

            string body = ShowStackTrace
                ? $"{prefix}  {message}\n<color=#808080>{new System.Diagnostics.StackTrace(2, true)}</color>"
                : $"{prefix}  {message}";

            // Route to the matching Unity log channel so the Console filter icons work correctly.
            switch (level)
            {
                case LogLevel.Warning:
                    Debug.LogWarning(body, context);
                    break;

                case LogLevel.Error:
                    Debug.LogError(body, context);
                    break;

                default:
                    Debug.Log(body, context);
                    break;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Returns the HTML colour string associated with <paramref name="level"/>.</summary>
        private static string LevelColour(LogLevel level) => level switch
        {
            LogLevel.Verbose => ColorVerbose,
            LogLevel.Info    => ColorInfo,
            LogLevel.Warning => ColorWarning,
            LogLevel.Error   => ColorError,
            _                => ColorInfo
        };
    }
}
