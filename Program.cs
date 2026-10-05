using GarenaOrchestrator;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Garena Orchestrator");
    Console.WriteLine("  dotnet GarenaOrchestrator.dll master");
    Console.WriteLine("  dotnet GarenaOrchestrator.dll satellite --master-url <url> --agent-token <token>");
    return;
}

switch (args[0].ToLowerInvariant())
{
    case "master":
        await MasterApp.RunAsync(args[1..]);
        break;
    case "satellite":
        await SatelliteApp.RunAsync(args[1..]);
        break;
    default:
        Console.Error.WriteLine($"Unknown mode: {args[0]}. Use 'master' or 'satellite'.");
        Environment.ExitCode = 2;
        break;
}
