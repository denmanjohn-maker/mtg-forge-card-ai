using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace MtgForgeAi.Logging;

/// <summary>
/// Wraps a Serilog <see cref="ILogEventSink"/> (typically the GrafanaLoki sink)
/// and re-emits each event with a pre-rendered message and no dynamic properties.
///
/// <para>
/// <c>Serilog.Sinks.GrafanaLoki</c> v1.1.2 promotes every
/// <see cref="LogEvent"/> property to a Loki stream label.  ASP.NET Core's
/// hosting-diagnostics middleware adds ~16 HTTP-scope properties per request
/// (ConnectionId, Method, Path, StatusCode, …), which pushes the label count
/// past Loki's hard limit of 15.
/// </para>
/// <para>
/// This sink renders the message template with all property values substituted
/// so that nothing is lost in the log line, then forwards a stripped
/// <see cref="LogEvent"/> that carries no dynamic properties.  The static
/// labels (<c>app</c>, <c>env</c>) injected via the GrafanaLoki
/// <c>labels</c> dictionary and the auto-added <c>level</c> label are
/// therefore the only stream labels Loki sees — well under the limit of 15.
/// </para>
/// </summary>
internal sealed class LokiLabelSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        // Render the full message with all structured property values
        // substituted so they remain visible in the Grafana log line even
        // after we strip the properties below.
        var rendered = logEvent.RenderMessage();

        // Build a plain-text MessageTemplate (no property holes) so the
        // inner Loki sink has no properties to promote to stream labels.
        var template = new MessageTemplate(
            rendered,
            [new TextToken(rendered)]);

        var stripped = new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.Exception,
            template,
            properties: []);

        inner.Emit(stripped);
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
