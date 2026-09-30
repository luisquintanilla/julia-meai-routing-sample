using OllamaSharp;

namespace RoutingSamples;

internal sealed record OllamaSettings(
    Uri Endpoint,
    string FastModel,
    string BalancedModel,
    string StrongModel,
    bool Stream)
{
    internal const string Usage =
        @"dotnet run --project samples\OllamaRouting -- <loopback-endpoint> <fast-model:tag> <balanced-model:tag> <strong-model:tag> [--stream]";

    internal static OllamaSettings Parse(string[] arguments)
    {
        if (arguments.Length is not (4 or 5) || (arguments.Length == 5 && arguments[4] != "--stream"))
        {
            throw new ArgumentException(Usage);
        }

        if (!Uri.TryCreate(arguments[0], UriKind.Absolute, out var endpoint) ||
            !endpoint.IsLoopback ||
            endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/")
        {
            throw new ArgumentException("Use an absolute loopback HTTP(S) Ollama base endpoint, without credentials, path, query or fragment.");
        }

        foreach (string model in arguments[1..4])
        {
            if (string.IsNullOrWhiteSpace(model) || model.Length > 64 ||
                model.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':')) ||
                !model.Contains(':') || model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Supply exact local model:tag names from ollama list (at most 64 ASCII characters); cloud tags are not supported.");
            }
        }

        return new OllamaSettings(endpoint, arguments[1], arguments[2], arguments[3], arguments.Length == 5);
    }

    internal async Task EnsureModelsAvailableAsync(IOllamaApiClient client, CancellationToken cancellationToken)
    {
        var models = await client.ListLocalModelsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (models is null)
        {
            throw new InvalidDataException("Ollama returned no model inventory.");
        }

        var available = models.Select(model => model.Name).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { FastModel, BalancedModel, StrongModel }
            .Distinct(StringComparer.Ordinal)
            .Where(model => !available.Contains(model))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Local Ollama models unavailable: {string.Join(", ", missing)}. " +
                "Run ollama list and manually pull/select the exact local tags. Nothing was downloaded.");
        }
    }
}
