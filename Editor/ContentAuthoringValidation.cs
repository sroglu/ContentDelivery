using System.Collections.Generic;
using System.Text;
using UnityEditor;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>One authoring validation message: a severity + text.</summary>
    public readonly struct AuthoringIssue
    {
        public readonly MessageType Severity;
        public readonly string Message;
        public AuthoringIssue(MessageType severity, string message) { Severity = severity; Message = message; }
        public static AuthoringIssue Error(string m)   => new AuthoringIssue(MessageType.Error, m);
        public static AuthoringIssue Warning(string m) => new AuthoringIssue(MessageType.Warning, m);
    }

    /// <summary>
    /// A content-authoring SO that can self-validate (empty/duplicate addresses, an active environment not in the
    /// list, …). Implemented by every authoring SO so the shared <see cref="AuthoringValidation"/> helpers surface
    /// their issues consistently in the inspector (via Odin <c>[InfoBox]</c>).
    /// </summary>
    public interface IContentAuthoringValidation
    {
        IEnumerable<AuthoringIssue> Validate();
    }

    /// <summary>Shared formatting of an <see cref="IContentAuthoringValidation"/>'s issues for Odin InfoBoxes.</summary>
    public static class AuthoringValidation
    {
        public static bool HasErrors(IContentAuthoringValidation v)   => Any(v, MessageType.Error);
        public static bool HasWarnings(IContentAuthoringValidation v) => Any(v, MessageType.Warning);
        public static bool Ready(IContentAuthoringValidation v)       => !HasErrors(v) && !HasWarnings(v);
        public static string Errors(IContentAuthoringValidation v)    => Join(v, MessageType.Error);
        public static string Warnings(IContentAuthoringValidation v)  => Join(v, MessageType.Warning);

        static bool Any(IContentAuthoringValidation v, MessageType s)
        {
            if (v == null) return false;
            foreach (var i in v.Validate()) if (i.Severity == s) return true;
            return false;
        }

        static string Join(IContentAuthoringValidation v, MessageType s)
        {
            if (v == null) return string.Empty;
            var sb = new StringBuilder();
            foreach (var i in v.Validate())
                if (i.Severity == s) { if (sb.Length > 0) sb.Append('\n'); sb.Append("• ").Append(i.Message); }
            return sb.ToString();
        }
    }
}
