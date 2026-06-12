using System.Reflection;

namespace AgentRecall.Core;

/// <summary>
/// Static information about the AgentRecall application itself.
/// </summary>
public static class AppInfo
{
    /// <summary>The product name used in CLI output.</summary>
    public const string Name = "agentrecall";

    /// <summary>
    /// The informational version of the running assembly, falling back to the
    /// assembly version when no informational version attribute is present.
    /// </summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the source-revision suffix (e.g. "1.0.0+abc123") if present.
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
