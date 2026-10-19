using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host;

/// <summary>Dedicated speech protocol; never creates a chat client or sends persona context.</summary>
public static class QwenTtsProvider
{
    public static void Validate(LlmProviderRecord provider, bool requireKey = false)
    {
        if (provider == null || !LlmProviderCatalog.IsTts(provider.type) ||
            !Uri.TryCreate(provider.baseUrl, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("千问TTS地址应为HTTPS或本机回环HTTP的 /v1 基址，不含查询参数。");
        if (string.IsNullOrWhiteSpace(provider.voice) || !Regex.IsMatch(provider.voice, "^[A-Za-z0-9_-]{1,48}$"))
            throw new InvalidOperationException("千问音色ID只支持英文、数字、下划线和短横线，最多48字。");
        if ((requireKey || !string.IsNullOrWhiteSpace(provider.apiKey)) &&
            !Regex.IsMatch(provider.apiKey ?? "", "^[A-Za-z0-9_-]{32,256}$"))
            throw new InvalidOperationException("请配置本地千问服务的独立API Key，不能使用占位值。");
        if (!string.IsNullOrWhiteSpace(provider.proxy))
            throw new InvalidOperationException("千问TTS直接连接配置地址，不使用代理。");
    }

    public static string SpeechUrl(LlmProviderRecord provider)
    {
        Validate(provider, true);
        return provider.baseUrl.TrimEnd('/') + "/audio/speech";
    }

    public static async Task<IReadOnlyList<string>> ListModelsAsync(LlmProviderRecord provider, CancellationToken token,
        HttpMessageHandler handler = null)
    {
        Validate(provider, true);
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65536 };
        using var request = new HttpRequestMessage(HttpMethod.Get, provider.baseUrl.TrimEnd('/') + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.apiKey);
        try
        {
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("千问模型列表返回HTTP " + (int)response.StatusCode + "；未自动重试。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("千问模型列表格式无效。");
            return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("id").GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 160).Distinct().ToList();
        }
        catch (HttpRequestException) { throw new InvalidOperationException("千问语音服务连接失败；未自动重试。"); }
        catch (JsonException) { throw new InvalidOperationException("千问模型列表格式无效。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("千问模型列表请求超时；未自动重试。"); }
    }
}
