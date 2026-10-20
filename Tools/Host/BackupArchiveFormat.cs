using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TraceSoul2.Host
{
    // v1: header TSBAK001 + random salt(16); repeated length(4 LE), tag(16), ciphertext.
    // Per-archive key = HMAC-SHA256(recovery key, header), avoiding cross-archive nonce reuse.
    // Nonce = salt prefix(4) + uint64 BE counter; AAD = header + counter + length.
    // An authenticated zero-length final record is mandatory; trailing bytes are rejected.
    public static class BackupArchiveFormat
    {
        private const int ChunkSize = 1024 * 1024;
        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

        public static void Encrypt(string zip, string output, byte[] key, CancellationToken token)
        {
            using var input = File.OpenRead(zip);
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var header = Encoding.ASCII.GetBytes("TSBAK001").Concat(RandomNumberGenerator.GetBytes(16)).ToArray();
            target.Write(header);
            var fileKey = HMACSHA256.HashData(key, header);
            using var aes = new AesGcm(fileKey, 16);
            CryptographicOperations.ZeroMemory(fileKey);
            var plain = new byte[ChunkSize];
            var cipher = new byte[ChunkSize];
            var tag = new byte[16];
            ulong index = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = input.ReadAtLeast(plain, ChunkSize, throwOnEndOfStream: false);
                var (nonce, aad, size) = Record(header, index++, count);
                aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, aad);
                target.Write(size); target.Write(tag); target.Write(cipher, 0, count);
                if (count == 0) break;
            }
            CryptographicOperations.ZeroMemory(plain);
            target.Flush(true);
        }

        public static void Decrypt(string archive, string output, byte[] key, CancellationToken token)
        {
            using var input = File.OpenRead(archive);
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var header = new byte[24]; input.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes("TSBAK001")))
                throw new InvalidDataException("不是支持的 TraceSoul 存档格式。");
            var fileKey = HMACSHA256.HashData(key, header);
            using var aes = new AesGcm(fileKey, 16);
            CryptographicOperations.ZeroMemory(fileKey);
            var plain = new byte[ChunkSize]; var cipher = new byte[ChunkSize];
            var size = new byte[4]; var tag = new byte[16];
            ulong index = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                input.ReadExactly(size);
                var count = BinaryPrimitives.ReadInt32LittleEndian(size);
                if (count < 0 || count > ChunkSize) throw new InvalidDataException("存档块长度无效。");
                input.ReadExactly(tag); input.ReadExactly(cipher.AsSpan(0, count));
                var (nonce, aad, _) = Record(header, index++, count);
                aes.Decrypt(nonce, cipher.AsSpan(0, count), tag, plain.AsSpan(0, count), aad);
                target.Write(plain, 0, count);
                if (count != 0) continue;
                if (input.ReadByte() != -1) throw new InvalidDataException("存档尾部存在额外数据。");
                break;
            }
            CryptographicOperations.ZeroMemory(plain);
        }

        private static (byte[] nonce, byte[] aad, byte[] size) Record(byte[] header, ulong index, int count)
        {
            var nonce = new byte[12]; header.AsSpan(8, 4).CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
            var aad = new byte[36]; header.CopyTo(aad, 0);
            BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(24), index);
            BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(32), count);
            return (nonce, aad, aad.AsSpan(32, 4).ToArray());
        }

        public static string Hash(string path)
        { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }

        public static string SafeName(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
                path.Split('/').Any(s => s.Length == 0 || s == "." || s == ".." ||
                    s.EndsWith(' ') || s.EndsWith('.') || s.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                    System.Text.RegularExpressions.Regex.IsMatch(s.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidDataException("存档包含不安全的相对路径。");
            return path;
        }

        public static BackupManifest ValidateZip(string zipPath, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > 200001) throw new InvalidDataException("存档文件数量超限。");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                SafeName(entry.FullName);
                if (!entries.TryAdd(entry.FullName, entry) || entry.Length > 128L * 1024 * 1024 * 1024 ||
                    ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("存档存在重名、链接或超限文件。");
            }
            if (!entries.TryGetValue("manifest.json", out var meta) || meta.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("存档清单缺失或超限。");
            using var json = meta.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, Json);
            if (manifest == null || manifest.Format != 1 || manifest.Files == null || manifest.Files.Count != entries.Count - 1)
                throw new InvalidDataException("存档清单版本或文件数量不符。");
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                SafeName(file.Path);
                if (!listed.Add(file.Path) || file.Path == "manifest.json" ||
                    !new[] { "Data/", "Plugins/", "plugins_data/", "Extras/", "AppDefaults/" }.Any(p => file.Path.StartsWith(p, StringComparison.Ordinal)) ||
                    !entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Bytes)
                    throw new InvalidDataException("存档清单与内容不一致。");
                using var content = entry.Open();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("存档文件校验失败。");
            }
            SafeName(manifest.ActiveSoul);
            if (!listed.Contains("Data/souls/" + manifest.ActiveSoul + "/tracesoul2-brainframe.sqlite3"))
                throw new InvalidDataException("存档缺少当前 Soul 主库。");
            return manifest;
        }

        // Offline only. Never opens a SoulRuntime, starts a plugin, or overwrites an existing directory.
        public static BackupManifest Extract(string archive, string keyFile, string destination, CancellationToken token)
        {
            destination = Path.GetFullPath(destination);
            BackupArchiveService.RejectLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("恢复目录必须尚不存在。");
            var key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            var work = destination + ".verify-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(work); BackupArchiveService.PrivateDirectory(work);
            try
            {
                var zipPath = Path.Combine(work, "verified.zip");
                Decrypt(archive, zipPath, key, token);
                var manifest = ValidateZip(zipPath, token);
                var extracted = Path.Combine(work, "content"); Directory.CreateDirectory(extracted);
                ZipFile.ExtractToDirectory(zipPath, extracted);
                foreach (var file in manifest.Files.Where(f => f.Database))
                    BackupArchiveService.CheckDatabase(Path.Combine(extracted, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                // Rewrite only the offline copy; retain all other instance settings.
                var homePath = Path.Combine(extracted, "Data", "home.json");
                var config = File.Exists(homePath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(homePath)).AsObject()
                    : new System.Text.Json.Nodes.JsonObject();
                config["activeSoul"] = manifest.ActiveSoul;
                config["pluginsDirectory"] = "../Plugins"; config["pluginsDataDirectory"] = "../plugins_data";
                File.WriteAllText(homePath, config.ToJsonString(Json));
                Directory.Move(extracted, destination);
                return manifest;
            }
            finally { CryptographicOperations.ZeroMemory(key); Directory.Delete(work, true); }
        }
    }

    public sealed class BackupManifest
    {
        public int Format { get; set; } = 1;
        public string HostVersion { get; set; }
        public string ActiveSoul { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string Consistency { get; set; } = "SQLite逐库快照；普通文件变化检测；跨库和外部服务不保证同一时刻。";
        public List<string> Notes { get; set; } = new List<string>();
        public List<BackupFile> Files { get; set; } = new List<BackupFile>();
    }
    public sealed class BackupFile
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }
        public bool Database { get; set; }
    }
}
