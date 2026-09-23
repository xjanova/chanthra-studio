using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// The one place LLM calls are sent and timed.
///
/// Each provider used its own <c>new HttpClient()</c>, whose built-in 100 s
/// timeout fired before the providers' own two-minute limit — so a long
/// storyboard (thousands of Thai tokens, plus thinking on newer models) died
/// at 100 s with "A task was canceled", which reads like the user pressed
/// cancel.
/// </summary>
internal static class LlmHttp
{
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How long one completion may take.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public static async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        HttpRequestMessage message, string providerName, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Limit);
        try
        {
            var resp = await Client.SendAsync(message, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return (resp, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{providerName} ไม่ตอบภายใน {Limit.TotalMinutes:0} นาที — ลองอีกครั้ง หรือขอให้เขียนสั้นลง");
        }
    }
}
