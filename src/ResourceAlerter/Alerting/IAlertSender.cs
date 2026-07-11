namespace ResourceAlerter.Alerting;

public interface IAlertSender
{
    /// <returns>True if the mail was actually handed off to the SMTP server successfully.
    /// False on any failure (including no recipients configured) — this never throws, since a
    /// mail problem must never take monitoring down, but callers that need to know whether
    /// delivery really happened (e.g. the Viewer's "send now" button) can check the result.</returns>
    Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken);
}
