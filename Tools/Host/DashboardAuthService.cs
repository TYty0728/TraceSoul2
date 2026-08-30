using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TraceSoul2.Host
{
    internal sealed class DashboardAuthService
    {
        private const int Iterations = 600000;
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int InitialPasswordLength = 24;
        private readonly object gate = new object();
        private readonly string path;
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        private DashboardAuthConfig config;

        public DashboardAuthService(string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(homeRoot))
                throw new ArgumentException("TraceSoul2 家目录不能为空。", nameof(homeRoot));

            Directory.CreateDirectory(homeRoot);
            path = Path.Combine(homeRoot, "control-auth.json");
            if (File.Exists(path))
            {
                config = ReadConfig();
                ValidateConfig(config);
                return;
            }

            var initialPassword = GeneratePassword();
            config = CreateConfig("admin", initialPassword, true);
            WriteConfig(config);
            Console.WriteLine(string.Empty);
            Console.WriteLine("============================================================");
            Console.WriteLine("TraceSoul2 控制台已创建管理员账号（此密码仅显示一次）");
            Console.WriteLine("用户名：admin");
            Console.WriteLine("初始密码：" + initialPassword);
            Console.WriteLine("登录后请立即修改用户名和密码。");
            Console.WriteLine("============================================================");
            Console.WriteLine(string.Empty);
        }

        public DashboardAuthSnapshot Snapshot()
        {
            lock (gate)
            {
                return new DashboardAuthSnapshot(
                    config.Username,
                    config.SessionStamp,
                    config.MustChangePassword);
            }
        }

        public bool Verify(string username, string password)
        {
            lock (gate)
            {
                if (!string.Equals(
                        (username ?? string.Empty).Trim(),
                        config.Username,
                        StringComparison.Ordinal))
                {
                    RunDummyHash(password);
                    return false;
                }

                return VerifyPassword(password, config);
            }
        }

        public DashboardAuthSnapshot ChangeAccount(
            string currentPassword,
            string username,
            string newPassword)
        {
            lock (gate)
            {
                if (!VerifyPassword(currentPassword, config))
                    throw new InvalidOperationException("当前密码不正确。");

                username = ValidateUsername(username);
                ValidatePassword(newPassword);
                var updated = CreateConfig(username, newPassword, false);
                WriteConfig(updated);
                config = updated;
                return new DashboardAuthSnapshot(
                    config.Username,
                    config.SessionStamp,
                    config.MustChangePassword);
            }
        }

        private DashboardAuthConfig ReadConfig()
        {
            try
            {
                var value = JsonSerializer.Deserialize<DashboardAuthConfig>(
                    File.ReadAllText(path), jsonOptions);
                if (value == null) throw new InvalidDataException("认证配置为空。");
                return value;
            }
            catch (Exception exception) when (!(exception is InvalidDataException))
            {
                throw new InvalidDataException(
                    "无法读取控制台认证配置 " + path + "：" + exception.Message,
                    exception);
            }
        }

        private void WriteConfig(DashboardAuthConfig value)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, jsonOptions));
                RestrictPermissions(temporary);
                File.Move(temporary, path, true);
                RestrictPermissions(path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static DashboardAuthConfig CreateConfig(
            string username,
            string password,
            bool mustChangePassword)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Derive(password, salt, Iterations);
            return new DashboardAuthConfig
            {
                Version = 1,
                Username = username,
                Algorithm = "PBKDF2-SHA256",
                Iterations = Iterations,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                SessionStamp = Guid.NewGuid().ToString("N"),
                MustChangePassword = mustChangePassword,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static bool VerifyPassword(string password, DashboardAuthConfig value)
        {
            try
            {
                var salt = Convert.FromBase64String(value.Salt);
                var expected = Convert.FromBase64String(value.PasswordHash);
                var actual = Derive(password, salt, value.Iterations);
                return expected.Length == actual.Length &&
                       CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password ?? string.Empty,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);
        }

        private static void RunDummyHash(string password)
        {
            Derive(password, new byte[SaltSize], Iterations);
        }

        private static string ValidateUsername(string username)
        {
            username = (username ?? string.Empty).Trim();
            if (username.Length < 3 || username.Length > 32)
                throw new ArgumentException("用户名长度必须为 3–32 个字符。");
            if (username.Any(char.IsWhiteSpace) || username.Any(char.IsControl))
                throw new ArgumentException("用户名不能包含空白或控制字符。");
            return username;
        }

        private static void ValidatePassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 12)
                throw new ArgumentException("新密码至少需要 12 个字符。");
            if (!password.Any(char.IsUpper) ||
                !password.Any(char.IsLower) ||
                !password.Any(char.IsDigit))
                throw new ArgumentException("新密码必须同时包含大写字母、小写字母和数字。");
        }

        private static string GeneratePassword()
        {
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lower = "abcdefghijkmnopqrstuvwxyz";
            const string digits = "23456789";
            const string all = upper + lower + digits;
            var chars = new char[InitialPasswordLength];
            chars[0] = RandomChar(upper);
            chars[1] = RandomChar(lower);
            chars[2] = RandomChar(digits);
            for (var index = 3; index < chars.Length; index++) chars[index] = RandomChar(all);
            for (var index = chars.Length - 1; index > 0; index--)
            {
                var swapWith = RandomNumberGenerator.GetInt32(index + 1);
                (chars[index], chars[swapWith]) = (chars[swapWith], chars[index]);
            }
            return new string(chars);
        }

        private static char RandomChar(string source)
        {
            return source[RandomNumberGenerator.GetInt32(source.Length)];
        }

        private static void ValidateConfig(DashboardAuthConfig value)
        {
            if (value.Version != 1 ||
                !string.Equals(value.Algorithm, "PBKDF2-SHA256", StringComparison.Ordinal) ||
                value.Iterations < 100000 ||
                string.IsNullOrWhiteSpace(value.Username) ||
                string.IsNullOrWhiteSpace(value.Salt) ||
                string.IsNullOrWhiteSpace(value.PasswordHash) ||
                string.IsNullOrWhiteSpace(value.SessionStamp))
                throw new InvalidDataException("控制台认证配置格式无效，请从备份恢复，不能自动重置密码。");
        }

        private static void RestrictPermissions(string filePath)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private sealed class DashboardAuthConfig
        {
            public int Version { get; set; }
            public string Username { get; set; }
            public string Algorithm { get; set; }
            public int Iterations { get; set; }
            public string Salt { get; set; }
            public string PasswordHash { get; set; }
            public string SessionStamp { get; set; }
            public bool MustChangePassword { get; set; }
            public DateTimeOffset UpdatedAtUtc { get; set; }
        }
    }

    internal sealed class DashboardLoginLimiter
    {
        private const int MaxFailures = 5;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, FailureState> states =
            new ConcurrentDictionary<string, FailureState>(StringComparer.Ordinal);

        public TimeSpan BlockedFor(string key)
        {
            if (!states.TryGetValue(key, out var state)) return TimeSpan.Zero;
            lock (state)
            {
                var remaining = state.LockedUntilUtc - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero) return remaining;
                if (DateTimeOffset.UtcNow - state.WindowStartedUtc > FailureWindow)
                    states.TryRemove(key, out _);
                return TimeSpan.Zero;
            }
        }

        public void RegisterFailure(string key)
        {
            var state = states.GetOrAdd(key, _ => new FailureState());
            lock (state)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - state.WindowStartedUtc > FailureWindow)
                {
                    state.WindowStartedUtc = now;
                    state.Failures = 0;
                }
                state.Failures++;
                if (state.Failures >= MaxFailures) state.LockedUntilUtc = now + LockDuration;
            }
        }

        public void RegisterSuccess(string key)
        {
            states.TryRemove(key, out _);
        }

        private sealed class FailureState
        {
            public DateTimeOffset WindowStartedUtc { get; set; } = DateTimeOffset.UtcNow;
            public DateTimeOffset LockedUntilUtc { get; set; } = DateTimeOffset.MinValue;
            public int Failures { get; set; }
        }
    }

    internal sealed class DashboardAuthSnapshot
    {
        public DashboardAuthSnapshot(string username, string sessionStamp, bool mustChangePassword)
        {
            Username = username;
            SessionStamp = sessionStamp;
            MustChangePassword = mustChangePassword;
        }

        public string Username { get; }
        public string SessionStamp { get; }
        public bool MustChangePassword { get; }
    }
}
