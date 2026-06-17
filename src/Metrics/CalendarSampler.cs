using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using SysWidge.Config;

namespace SysWidge.Metrics;

/// <summary>
/// Fetches one or more ICS feeds in the background and exposes the day's upcoming events
/// (merged across feeds). If today has none, falls back to the single next event so the
/// tile isn't blank.
///
/// Network + parse run on a timer thread; <see cref="Snapshot"/> returns the last result
/// instantly. A one-line status is written to calendar.log for diagnosis.
/// </summary>
public sealed class CalendarSampler : IDisposable
{
    private const int MaxShow = 12;
    private const int PerFeedCap = 200;

    private static readonly HttpClient Http = CreateHttp();

    private readonly IReadOnlyList<(string Url, string ColorHex)> _feeds;
    private readonly int _lookaheadDays;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<CalEvent> _events = Array.Empty<CalEvent>();

    public CalendarSampler(IReadOnlyList<(string Url, string ColorHex)> feeds, int refreshMinutes, int lookaheadDays)
    {
        _feeds = feeds;
        _lookaheadDays = Math.Clamp(lookaheadDays, 0, 7);
        int minutes = Math.Max(1, refreshMinutes);
        _timer = new System.Threading.Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromMinutes(minutes));
    }

    public IReadOnlyList<CalEvent> Snapshot()
    {
        lock (_gate) return _events;
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        DateTime horizon = now.AddDays(60);
        var from = new CalDateTime(DateTime.SpecifyKind(now.AddMinutes(-1), DateTimeKind.Unspecified));

        var upcoming = new List<CalEvent>();
        int feedsOk = 0;
        string lastError = "";

        foreach (var (url, colorHex) in _feeds)
        {
            try
            {
                string ics = Http.GetStringAsync(url).GetAwaiter().GetResult();
                var calendar = Calendar.Load(ics);
                if (calendar is null) { lastError = "empty/invalid ICS"; continue; }

                int perFeed = 0;
                foreach (var occ in calendar.GetOccurrences(from))
                {
                    if (++perFeed > PerFeedCap) break;

                    var ev = occ.Source as CalendarEvent;
                    bool allDay;
                    DateTime start;
                    try
                    {
                        allDay = ev?.IsAllDay ?? false;
                        start = allDay ? occ.Period.StartTime.Value.Date : occ.Period.StartTime.AsUtc.ToLocalTime();
                    }
                    catch { continue; }

                    if (start > horizon) break;
                    bool past = start < now.AddMinutes(-1);
                    if (past && !(allDay && start.Date == now.Date)) continue;

                    upcoming.Add(new CalEvent(start, string.IsNullOrWhiteSpace(ev?.Summary) ? "(busy)" : ev!.Summary.Trim(), allDay, colorHex));
                }
                feedsOk++;
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        upcoming.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Events within the look-ahead window (today + N days); if none, the next upcoming one.
        DateTime windowEnd = now.Date.AddDays(_lookaheadDays + 1);
        var inWindow = upcoming.Where(e => e.Start < windowEnd).Take(MaxShow).ToList();
        IReadOnlyList<CalEvent> result = inWindow.Count > 0
            ? inWindow
            : (upcoming.Count > 0 ? new List<CalEvent> { upcoming[0] } : Array.Empty<CalEvent>());

        lock (_gate) _events = result;

        string head = result.Count == 0 ? "(none)" : $"{result.Count} shown, first = {result[0].Start:MM-dd HH:mm} {result[0].Title}";
        Log($"feeds {feedsOk}/{_feeds.Count}, look {_lookaheadDays}d, {upcoming.Count} upcoming -> {head}{(lastError.Length > 0 ? $"; lastErr: {lastError}" : "")}");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SysWidge/0.3 (+https://github.com/profangrybeard/sysWidge)");
        return http;
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.GetDirectoryName(WidgetConfig.ConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "calendar.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => _timer.Dispose();
}
