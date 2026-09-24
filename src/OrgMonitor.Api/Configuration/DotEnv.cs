namespace OrgMonitor.Api.Configuration;

/// <summary>
/// Minimal dotenv loader so secrets live in a gitignored <c>.env</c> instead of
/// appsettings, shell history, or systemd unit files.
///
/// Semantics match the usual dotenv convention:
/// <list type="bullet">
/// <item><c>#</c> starts a comment only at the start of a line, so a secret may contain <c>#</c>.</item>
/// <item>Values may be wrapped in single or double quotes (quotes are stripped).</item>
/// <item>A variable already present in the process environment is never overwritten,
/// so real environment variables always win over the file.</item>
/// </list>
///
/// Keys use ASP.NET's environment-variable form, where <c>__</c> nests:
/// <c>Monitoring__AdminApiKey</c> becomes <c>Monitoring:AdminApiKey</c>.
/// </summary>
public static class DotEnv
{
    private const string FileName = ".env";

    /// <summary>Loads the nearest <c>.env</c> if one exists. Returns its path, or null.</summary>
    public static string? Load()
    {
        var path = Find();
        if (path is null)
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue; // not a KEY=VALUE line
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = Unquote(line[(separator + 1)..].Trim());

            // An explicitly exported variable outranks the file.
            if (Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
        }

        return path;
    }

    /// <summary>
    /// Searches upward from the working directory and from the assembly location,
    /// so the file is found whether the API is started with `dotnet run` from the
    /// repository root or by running the built binary from `bin/`.
    /// </summary>
    private static string? Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\'')
            ? value[1..^1]
            : value;
}
