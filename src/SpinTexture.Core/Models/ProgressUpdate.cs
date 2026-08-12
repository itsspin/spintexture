namespace SpinTexture.Core.Models;

public sealed record ProgressUpdate(
    string Stage,
    string Message,
    int Completed,
    int Total,
    string? CurrentItem = null,
    bool IsHeartbeat = false)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total * 100, 0, 100);
}
