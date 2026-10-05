using GarenaOrchestrator;

string? environmentMode = Environment.GetEnvironmentVariable("MODE");
string mode = !string.IsNullOrWhiteSpace(environmentMode)
    ? environmentMode.Trim().ToLowerInvariant()
    : args.Length > 0
        ? args[0].ToLowerInvariant()
        : "master";
bool firstArgumentIsMode = args.Length > 0 && args[0].ToLowerInvariant() is "master" or "satellite" or "help" or "--help" or "-h";
string[] modeArgs = firstArgumentIsMode ? args[1..] : args;

if (mode is "help" or "--help" or "-h")
{
    Console.WriteLine("Garena Orchestrator");
    Console.WriteLine("  dotnet GarenaOrchestrator.dll master");
    Console.WriteLine("  dotnet GarenaOrchestrator.dll satellite --master-url <url> --agent-token <token>");
    Console.WriteLine("  MODE=master|satellite can be used instead of a command argument.");
    return;
}

switch (mode)
{
    case "master":
        await MasterApp.RunAsync(modeArgs);
        break;
    case "satellite":
        await SatelliteApp.RunAsync(modeArgs);
        break;
    default:
        Console.Error.WriteLine($"Unknown mode: {mode}. Use 'master' or 'satellite'.");
        Environment.ExitCode = 2;
        break;
}
