using System.Globalization;
using Lockpick.Core.Fingerprinting;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.App.Services;

public sealed record BuildCheckResult(bool Found, bool Supported, string Summary, string Details);

/// <summary>Fingerprints an installed game executable and reports whether Lockpick supports it.</summary>
public static class BuildCheck
{
    public static BuildCheckResult Run(IFingerprintProvider fingerprints, Re1Adapter adapter, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new BuildCheckResult(false, false, "Game not found", "No executable configured, and no GOG install was found in the registry.");
        }

        try
        {
            var fp = fingerprints.Compute(path.Trim());
            var match = adapter.Identify(fp);
            var details = string.Create(CultureInfo.InvariantCulture, $"{fp.Path}\n{fp.FileSize:N0} bytes · SHA-256 {fp.Sha256}\n{match.Reason}");
            return match.IsSupported
                ? new BuildCheckResult(true, true, $"Supported — {match.Profile!.DisplayName}", details)
                : new BuildCheckResult(true, false, "This build is not supported yet", details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new BuildCheckResult(false, false, "Can't read that file", $"{path}: {ex.Message}");
        }
    }
}
