using OutcomeRouting;

try
{
    var arguments = Arguments.Parse(args);

    if (arguments.Help)
    {
        Console.WriteLine(Arguments.Usage);
        return 0;
    }

    arguments.ValidateMode();

    if (arguments.JuliaDirectory is null)
    {
        await Demonstrations.RunAsync();
    }
    else
    {
        await NativeScenario.RunAsync(arguments);
    }

    return 0;
}
catch (Exception error)
{
    // Live provider messages can contain request bodies or endpoint secrets.
    bool safeMessage = error is ArgumentException or FormatException or DirectoryNotFoundException
        or FileNotFoundException or InvalidDataException;

    Console.Error.WriteLine(safeMessage
        ? $"ERROR ({error.GetType().Name}): {error.Message}"
        : $"ERROR ({error.GetType().Name}): execution failed; no fixture or empty-history fallback was used.");

    return 2;
}
