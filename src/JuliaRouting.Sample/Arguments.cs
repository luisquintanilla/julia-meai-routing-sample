using System.Globalization;
using OutcomeRouting;

internal sealed record Arguments(
    string? JuliaDirectory,
    string? Store,
    bool Live,
    bool NoVerifier,
    bool Help,
    PolicySettings Policy)
{
    internal const string Usage = """
        Julia outcome-aware routing (.NET 10)
          dotnet run --project src\JuliaRouting.Sample                       # isolated fixture demo
          dotnet run --project src\JuliaRouting.Sample -- --julia <directory> # real CPU Julia, fixture chat
          dotnet run --project src\JuliaRouting.Sample -- --julia <directory> --live
        Native/live options:
          --store <database-path>  --no-verifier
          --confidence-floor <0..1>  --conservative-tier <balanced|strong|fast>
          --history-limit <1..6>  --history-days <1..30>  --max-attempts <1..6>
        Live mode requires ROUTING_CHAT_ENDPOINT, ROUTING_CHAT_API_KEY, ROUTING_FAST_MODEL,
        ROUTING_BALANCED_MODEL and ROUTING_STRONG_MODEL in the environment (at least two distinct models).
        Optional ROUTING_<FAST|BALANCED|STRONG>_REASONING=<low|medium|high>.
        No automatic model downloads; --live is the only path making downstream network calls.
        """;

    internal void ValidateMode()
    {
        Policy.Validate();

        if (JuliaDirectory is not null)
        {
            return;
        }

        if (Live || Store is not null || NoVerifier)
        {
            throw new ArgumentException(
                "--live, --store and --no-verifier require --julia <directory>; offline demo data is isolated.");
        }

        if (Policy != new PolicySettings())
        {
            throw new ArgumentException(
                "Policy flags apply to --julia mode; the offline demonstration uses fixed asserted settings.");
        }
    }

    internal static Arguments Parse(string[] arguments)
    {
        string? directory = null;
        string? store = null;
        bool live = false;
        bool noVerifier = false;
        var policy = new PolicySettings();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < arguments.Length; index++)
        {
            string flag = arguments[index];

            if (!seen.Add(flag))
            {
                throw new ArgumentException($"Duplicate option {flag}.");
            }

            string Value()
            {
                index++;

                if (index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) ||
                    arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"An explicit value is required for {flag}.");
                }

                return arguments[index];
            }

            switch (flag)
            {
                case "--help" when arguments.Length == 1:
                    return new Arguments(null, null, false, false, true, policy);

                case "--julia":
                    directory = Path.GetFullPath(Value());
                    break;

                case "--store":
                    store = Path.GetFullPath(Value());
                    break;

                case "--live":
                    live = true;
                    break;

                case "--no-verifier":
                    noVerifier = true;
                    break;

                case "--confidence-floor":
                    policy = policy with { ConfidenceFloor = double.Parse(Value(), CultureInfo.InvariantCulture) };
                    break;

                case "--conservative-tier":
                    string tier = Value();

                    if (tier is not ("fast" or "balanced" or "strong"))
                    {
                        throw new ArgumentException("Use fast, balanced or strong.");
                    }

                    policy = policy with { ConservativeTier = Enum.Parse<Tier>(tier, true) };
                    break;

                case "--history-limit":
                    policy = policy with { EvidenceLimit = int.Parse(Value(), CultureInfo.InvariantCulture) };
                    break;

                case "--history-days":
                    policy = policy with
                    {
                        EvidenceAge = TimeSpan.FromDays(int.Parse(Value(), CultureInfo.InvariantCulture))
                    };
                    break;

                case "--max-attempts":
                    policy = policy with { MaximumAttempts = int.Parse(Value(), CultureInfo.InvariantCulture) };
                    break;

                default:
                    throw new ArgumentException($"Unknown option {flag}. Use --help.");
            }
        }

        return new Arguments(directory, store, live, noVerifier, false, policy);
    }
}
