using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FlagsRollout.Lab;

// dotnet run -c Release --project benchmarks/FlagsRollout.Lab -- policies [seeds]
// dotnet run -c Release --project benchmarks/FlagsRollout.Lab -- propagation [samples]
var mode = args.Length > 0 ? args[0] : "policies";
var output = Path.Combine(FindRepositoryRoot(), "benchmark-results");
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true };

switch (mode)
{
    case "policies":
        {
            var seeds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 300;
            var stopwatch = Stopwatch.StartNew();
            var results = PolicyLab.Run(seeds);
            stopwatch.Stop();
            var markdown = PolicyLab.ToMarkdown(results);
            Console.WriteLine(markdown);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{seeds} seeds x {PolicyLab.Scenarios.Count} scenarios x {PolicyLab.Policies.Count} policies, {PolicyLab.DurationSeconds} simulated seconds each, in {stopwatch.Elapsed.TotalSeconds:0.0} s"));
            await File.WriteAllTextAsync(Path.Combine(output, "policies.md"), markdown);
            await File.WriteAllTextAsync(Path.Combine(output, "policies.json"), JsonSerializer.Serialize(results, json));
            break;
        }

    case "propagation":
        {
            var samples = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 40;
            var result = await PropagationLab.RunAsync(samples, Path.Combine(FindRepositoryRoot(), "deploy", "flagd", "flags.json"));
            Console.WriteLine(JsonSerializer.Serialize(result));
            await File.WriteAllTextAsync(Path.Combine(output, "propagation.json"), JsonSerializer.Serialize(result, json));
            break;
        }

    default:
        Console.Error.WriteLine("usage: FlagsRollout.Lab policies [seeds] | propagation [samples]");
        return 2;
}

return 0;

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlagsRollout.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Directory.GetCurrentDirectory();
}
