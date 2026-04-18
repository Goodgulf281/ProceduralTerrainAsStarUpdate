// ============================================================
// Script:      IDebuggable.cs
// Description: Interface and extension methods that give any MonoBehaviour
//              a per-instance Inspector toggle while routing all output
//              through the central GameLogger system.
//
//              Usage — implement the interface on any MonoBehaviour:
//
//                  public class MySystem : MonoBehaviour, IDebuggable
//                  {
//                      [Header("Debug")]
//                      [SerializeField] private bool _debugEnabled = true;
//                      public bool DebugEnabled => _debugEnabled;
//
//                      private void Start()
//                      {
//                          this.LogInfo("System started");
//                          this.LogWarning("Low memory", LogLevel.Warning);
//                      }
//                  }
//
// Author:      Goodgulf
// ============================================================

using System.Runtime.CompilerServices;
using UnityEngine;

namespace Goodgulf.Logging
{
    // ── Interface ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks a MonoBehaviour as having a per-instance debug toggle.
    ///
    /// <para>
    /// Implement this interface and back <see cref="DebugEnabled"/> with a
    /// <c>[SerializeField] private bool _debugEnabled</c> field so the toggle
    /// is visible and editable in the Unity Inspector at runtime.
    /// </para>
    ///
    /// <para>
    /// Use the extension methods on <see cref="DebuggableExtensions"/> to log.
    /// Each call is gated by both the per-instance <see cref="DebugEnabled"/>
    /// flag AND the global <see cref="GameLogger.GlobalMinLevel"/> filter —
    /// both must pass for a message to be written.
    /// </para>
    /// </summary>
    public interface IDebuggable
    {
        /// <summary>
        /// When false, all log calls from this instance are silenced regardless
        /// of the global <see cref="GameLogger.GlobalMinLevel"/> setting.
        /// </summary>
        bool DebugEnabled { get; }
    }

    // ── Extension Methods ─────────────────────────────────────────────────────

    /// <summary>
    /// Fluent logging extensions for any class that implements <see cref="IDebuggable"/>.
    ///
    /// <para>
    /// The caller's class name, method name, and line number are injected at
    /// compile time — no reflection is used at runtime.
    /// </para>
    /// </summary>
    public static class DebuggableExtensions
    {
        /// <summary>
        /// Log a <see cref="LogLevel.Verbose"/> message from an <see cref="IDebuggable"/> source.
        /// Silenced when <see cref="IDebuggable.DebugEnabled"/> is false OR when
        /// <see cref="GameLogger.GlobalMinLevel"/> is above <see cref="LogLevel.Verbose"/>.
        /// </summary>
        /// <param name="source">The calling MonoBehaviour (pass <c>this</c>).</param>
        /// <param name="message">Human-readable log text.</param>
        /// <param name="callerMethod">Filled by the compiler — do not pass manually.</param>
        /// <param name="callerFile">Filled by the compiler — do not pass manually.</param>
        /// <param name="callerLine">Filled by the compiler — do not pass manually.</param>
        public static void LogVerbose(
            this IDebuggable source,
            string message,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
        {
            if (!source.DebugEnabled)
                return;

            // Pass the Unity Object context only when source is a Unity Object
            // so clicking the log entry in the Console highlights the GameObject.
            Object context = source as Object;
            GameLogger.Write(LogLevel.Verbose, message, context, callerMethod, callerFile, callerLine);
        }

        /// <summary>Log an <see cref="LogLevel.Info"/> message.</summary>
        /// <inheritdoc cref="LogVerbose"/>
        public static void LogInfo(
            this IDebuggable source,
            string message,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
        {
            if (!source.DebugEnabled)
                return;

            Object context = source as Object;
            GameLogger.Write(LogLevel.Info, message, context, callerMethod, callerFile, callerLine);
        }

        /// <summary>Log a <see cref="LogLevel.Warning"/> message.</summary>
        /// <inheritdoc cref="LogVerbose"/>
        public static void LogWarning(
            this IDebuggable source,
            string message,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
        {
            if (!source.DebugEnabled)
                return;

            Object context = source as Object;
            GameLogger.Write(LogLevel.Warning, message, context, callerMethod, callerFile, callerLine);
        }

        /// <summary>
        /// Log an <see cref="LogLevel.Error"/> message.
        ///
        /// <para>
        /// Note: error messages intentionally ignore the per-instance
        /// <see cref="IDebuggable.DebugEnabled"/> flag. Errors are always
        /// written so critical failures are never silently swallowed.
        /// </para>
        /// </summary>
        /// <inheritdoc cref="LogVerbose"/>
        public static void LogError(
            this IDebuggable source,
            string message,
            [CallerMemberName] string callerMethod = "",
            [CallerFilePath]   string callerFile   = "",
            [CallerLineNumber] int    callerLine    = 0)
        {
            // Errors bypass the per-instance toggle — they are always surfaced.
            Object context = source as Object;
            GameLogger.Write(LogLevel.Error, message, context, callerMethod, callerFile, callerLine);
        }
    }
}
