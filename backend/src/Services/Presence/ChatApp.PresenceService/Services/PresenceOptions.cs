namespace ChatApp.PresenceService.Services;

public sealed class PresenceOptions
{
    public int HeartbeatSeconds { get; set; } = 10;
    public int LeaseSeconds { get; set; } = 60;
    public int SweepSeconds { get; set; } = 2;
}
