namespace ClashXW.Models;

public sealed record DashboardState(
    string Version,
    string DashboardDirectory,
    string UpdateUrl,
    string? ETag);
