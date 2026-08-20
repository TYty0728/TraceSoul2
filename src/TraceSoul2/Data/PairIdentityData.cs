using System;

namespace TraceSoul2.Data
{
    /// <summary>
    /// 两个人的显示名。内部路由键仍用稳定槽位，但 Prompt、事实短句和 Moment 角色必须用名字。
    /// </summary>
    public sealed class PairIdentity
    {
        public const string DefaultId = "default";
        public const string UsernameToken = "{username}";
        public const string AssnameToken = "{assname}";

        private static readonly string[] ForbiddenNames =
        {
            "用户", "助手", "user", "ass", "assistant", "username", "assname", "agent"
        };

        public static readonly PairIdentity Missing = new PairIdentity(string.Empty, string.Empty, string.Empty);

        public string Username { get; private set; }
        public string Assname { get; private set; }
        public string CallName { get; private set; }

        public bool IsComplete
        {
            get { return Username.Length > 0 && Assname.Length > 0; }
        }

        public bool HasCallName
        {
            get { return CallName.Length > 0 && !string.Equals(CallName, Username, StringComparison.Ordinal); }
        }

        public string AddressName
        {
            get { return HasCallName ? CallName : Username; }
        }

        private PairIdentity(string username, string assname, string callName)
        {
            Username = username ?? string.Empty;
            Assname = assname ?? string.Empty;
            CallName = callName ?? string.Empty;
        }

        public static PairIdentity Create(string username, string assname, string callName = null)
        {
            username = NormalizeName(username);
            assname = NormalizeName(assname);
            callName = NormalizeName(callName);
            if (username.Length == 0 || assname.Length == 0)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            if (string.Equals(username, assname, StringComparison.OrdinalIgnoreCase) ||
                (callName.Length > 0 && string.Equals(callName, assname, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("两个人的名字不能相同。");
            if (IsForbidden(username) || IsForbidden(assname) || IsForbidden(callName))
                throw new InvalidOperationException("名字请用彼此真正的称呼，不要填用户或助手。");
            if (string.Equals(callName, username, StringComparison.Ordinal)) callName = string.Empty;
            return new PairIdentity(username, assname, callName);
        }

        public static PairIdentity FromStored(string username, string assname, string callName = null)
        {
            username = NormalizeName(username);
            assname = NormalizeName(assname);
            callName = NormalizeName(callName);
            if (username.Length == 0 || assname.Length == 0) return Missing;
            if (string.Equals(username, assname, StringComparison.OrdinalIgnoreCase)) return Missing;
            if (IsForbidden(username) || IsForbidden(assname) || IsForbidden(callName)) return Missing;
            if (callName.Length > 0 && string.Equals(callName, assname, StringComparison.OrdinalIgnoreCase))
                callName = string.Empty;
            if (string.Equals(callName, username, StringComparison.Ordinal)) callName = string.Empty;
            return new PairIdentity(username, assname, callName);
        }

        public string Apply(string text)
        {
            var value = text ?? string.Empty;
            if (!IsComplete) return value;
            return value
                .Replace(UsernameToken, Username)
                .Replace(AssnameToken, Assname)
                .Replace("{callname}", AddressName);
        }

        public string RewriteRecordedText(string text, PairIdentity previous = null)
        {
            var value = text ?? string.Empty;
            if (previous != null && previous.IsComplete)
            {
                if (!string.Equals(previous.Username, Username, StringComparison.Ordinal))
                    value = ReplaceName(value, previous.Username, Username);
                if (!string.Equals(previous.Assname, Assname, StringComparison.Ordinal))
                    value = ReplaceName(value, previous.Assname, Assname);
                if (previous.HasCallName && HasCallName &&
                    !string.Equals(previous.CallName, CallName, StringComparison.Ordinal))
                    value = ReplaceName(value, previous.CallName, CallName);
            }
            value = Apply(value);
            if (IsComplete)
                value = value.Replace("用户", Username).Replace("助手", Assname);
            return value;
        }

        public bool IsHumanMoment(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            if (string.Equals(role.Trim(), "user", StringComparison.OrdinalIgnoreCase)) return true;
            return IsComplete &&
                   (string.Equals(role.Trim(), Username, StringComparison.OrdinalIgnoreCase) ||
                    (HasCallName && string.Equals(role.Trim(), CallName, StringComparison.OrdinalIgnoreCase)));
        }

        public bool IsCompanionMoment(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            if (string.Equals(role.Trim(), "assistant", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(role.Trim(), "ass", StringComparison.OrdinalIgnoreCase)) return true;
            return IsComplete && string.Equals(role.Trim(), Assname, StringComparison.OrdinalIgnoreCase);
        }

        public string CanonicalMomentRole(string role)
        {
            if (IsHumanMoment(role)) return IsComplete ? Username : "user";
            if (IsCompanionMoment(role)) return IsComplete ? Assname : "assistant";
            return (role ?? string.Empty).Trim();
        }

        public string LabelForRole(string role)
        {
            if (IsHumanMoment(role)) return IsComplete ? Username : role;
            if (IsCompanionMoment(role)) return IsComplete ? Assname : role;
            return string.IsNullOrWhiteSpace(role) ? "事件" : role.Trim();
        }

        public string CanonicalDomain(string value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (raw.StartsWith("domain.", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("domain.".Length);
            if (raw == "ass" || raw == "user" || raw == "relation" || raw == "world")
                return raw.ToLowerInvariant();
            if (raw == "我们" || raw == "关系") return "relation";
            if (raw == "世界") return "world";
            if (!IsComplete) return null;
            if (string.Equals(raw, Assname, StringComparison.OrdinalIgnoreCase)) return "ass";
            if (string.Equals(raw, Username, StringComparison.OrdinalIgnoreCase)) return "user";
            if (HasCallName && string.Equals(raw, CallName, StringComparison.OrdinalIgnoreCase)) return "user";
            return null;
        }

        private static string NormalizeName(string value)
        {
            var name = string.Join(" ", (value ?? string.Empty).Trim().Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return name.Length <= 12 ? name : name.Substring(0, 12);
        }

        private static bool IsForbidden(string name)
        {
            return Array.Exists(ForbiddenNames,
                x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string ReplaceName(string text, string from, string to)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from) || from == to) return text;
            return text.Replace(from, to ?? string.Empty);
        }
    }
}
