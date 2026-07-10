namespace ResourceAlerter.Alerting;

public interface IAlertSender
{
    Task SendAsync(AlertMessage message, CancellationToken cancellationToken);
}
