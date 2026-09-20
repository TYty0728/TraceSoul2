using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.ExternalPlugins
{
    public sealed partial class QqQzonePlugin
    {
        private async Task<List<QzonePhoto>> UploadImagesAsync(
            IReadOnlyList<string> files, QzoneSession session, CancellationToken cancellationToken)
        {
            var result = new List<QzonePhoto>();
            if (files == null || files.Count == 0) return result;
            if (files.Count > 9) throw new InvalidOperationException("一条说说最多配九张图片。");
            var cookies = ParseCookies(session.Cookies);
            foreach (var path in files)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 32 || info.Length > 12 * 1024 * 1024)
                    throw new InvalidOperationException("说说配图文件不存在或大小超限，本次未发布。");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (bytes.Length < 32 || bytes.Length > 12 * 1024 * 1024)
                    throw new InvalidOperationException("说说配图文件大小超限，本次未发布。");
                var knownImage = (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47) ||
                    (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) ||
                    (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) ||
                    (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                     bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50);
                if (!knownImage) throw new InvalidOperationException("说说配图格式无法识别，本次未发布。");
                var url = "https://up.qzone.qq.com/cgi-bin/upload/cgi_upload_image?g_tk=" + session.Gtk;
                var form = new Dictionary<string, string>
                {
                    ["uin"] = session.Uin.ToString(CultureInfo.InvariantCulture),
                    ["p_uin"] = session.Uin.ToString(CultureInfo.InvariantCulture),
                    ["zzpaneluin"] = session.Uin.ToString(CultureInfo.InvariantCulture),
                    ["skey"] = cookies.TryGetValue("skey", out var skey) ? skey : "",
                    ["p_skey"] = cookies.TryGetValue("p_skey", out var pSkey) ? pSkey : "",
                    ["filename"] = "filename", ["uploadtype"] = "1", ["albumtype"] = "7",
                    ["exttype"] = "0", ["refer"] = "shuoshuo", ["output_type"] = "jsonhtml",
                    ["charset"] = "utf-8", ["output_charset"] = "utf-8",
                    ["upload_hd"] = "1", ["hd_width"] = "2048", ["hd_height"] = "10000", ["hd_quality"] = "96",
                    ["base64"] = "1", ["picfile"] = Convert.ToBase64String(bytes),
                    ["jsonhtml_callback"] = "callback", ["url"] = url,
                    ["qzreferrer"] = "https://user.qzone.qq.com/" + session.Uin
                };
                using var http = NewClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(form)
                };
                ApplyBrowserHeaders(request, session);
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("说说配图上传失败 HTTP " + (int)response.StatusCode + "，本次未发布。");
                result.Add(ParseUploadedPhoto(await response.Content.ReadAsStringAsync(cancellationToken)));
            }
            return result;
        }

        internal static QzonePhoto ParseUploadedPhoto(string body)
        {
            using var doc = JsonDocument.Parse(StripEnvelope(body));
            var root = doc.RootElement;
            var code = ReadAnyString(root, "code", "ret");
            if (code.Length > 0 && code != "0")
                throw new InvalidOperationException("说说配图上传被拒绝，本次未发布。");
            var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            var width = ReadAnyString(data, "width");
            var height = ReadAnyString(data, "height");
            var album = ReadAnyString(data, "albumid");
            var large = ReadAnyString(data, "lloc");
            var small = ReadAnyString(data, "sloc");
            var type = ReadAnyString(data, "type", "picType");
            var bo = ReadAnyString(data, "pic_bo", "bo");
            if (bo.Length == 0)
            {
                var match = Regex.Match(ReadAnyString(data, "pre", "url"), @"(?:[?&])bo=([^&#]+)");
                if (match.Success) bo = Uri.UnescapeDataString(match.Groups[1].Value);
            }
            if (album.Length == 0 || large.Length == 0 || small.Length == 0 || bo.Length == 0 ||
                !int.TryParse(width, out var w) || w <= 0 || !int.TryParse(height, out var h) || h <= 0)
                throw new InvalidOperationException("说说配图上传回包缺少图片字段，本次未发布。");
            return new QzonePhoto
            {
                RichValue = string.Join(",", "", album, large, small, type.Length == 0 ? "1" : type, height, width, "", height, width),
                Bo = bo
            };
        }

        private static void AddPhotoFields(Dictionary<string, string> form, List<QzonePhoto> photos)
        {
            if (photos.Count == 0) return;
            form["richtype"] = "1";
            form["subrichtype"] = "1";
            form["richval"] = string.Join("\t", photos.Select(x => x.RichValue));
            form["pic_bo"] = string.Join(",", photos.Select(x => x.Bo));
            form["pic_template"] = "tpl-" + photos.Count + "-1";
        }

        internal sealed class QzonePhoto
        {
            public string RichValue;
            public string Bo;
        }
    }
}
