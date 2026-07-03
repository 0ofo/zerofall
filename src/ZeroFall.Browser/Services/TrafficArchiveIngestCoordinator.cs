using System;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 独立于流量表 UI 的归档写入：保证 Bottom 未选中「网络监控」时流量仍会持久化。
/// </summary>
public sealed class TrafficArchiveIngestCoordinator
{
    private readonly TrafficArchiveService _archive;

    public TrafficArchiveIngestCoordinator(IEventBus eventBus, TrafficArchiveService archive)
    {
        _archive = archive;
        eventBus.Subscribe<TrafficEntryIngestedEvent>(OnTrafficIngested);
        eventBus.Subscribe<WebTrafficBodyUpdatedEvent>(OnBodyUpdated);
    }

    private void OnTrafficIngested(TrafficEntryIngestedEvent e)
    {
        if (e.Decision == TrafficCaptureDedup.Decision.DropProxyDuplicate)
            return;

        _ = PersistAsync(e);
    }

    private void OnBodyUpdated(WebTrafficBodyUpdatedEvent e)
    {
        if (IsEmptyBodyUpdate(e))
            return;

        _ = PersistBodyAsync(e);
    }

    private async Task PersistAsync(TrafficEntryIngestedEvent e)
    {
        try
        {
            await _archive.InsertAsync(e.Entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TrafficArchiveIngest] Insert failed for {e.Entry.EntryId}: {ex.Message}");
        }
    }

    private async Task PersistBodyAsync(WebTrafficBodyUpdatedEvent e)
    {
        var responseBodyLength = e.ResponseBodyRaw?.Length
            ?? (string.IsNullOrEmpty(e.ResponseBody) ? 0 : e.ResponseBody.Length);

        try
        {
            await _archive.UpdateBodyAsync(
                e.EntryId,
                e.RequestBody,
                e.ResponseBody,
                e.RequestBodyRaw,
                e.ResponseBodyRaw,
                responseBodyLength: responseBodyLength > 0 ? responseBodyLength : null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TrafficArchiveIngest] Body update failed for {e.EntryId}: {ex.Message}");
        }
    }

    private static bool IsEmptyBodyUpdate(WebTrafficBodyUpdatedEvent e) =>
        string.IsNullOrEmpty(e.RequestBody)
        && string.IsNullOrEmpty(e.ResponseBody)
        && e.RequestBodyRaw is null
        && e.ResponseBodyRaw is null;
}
