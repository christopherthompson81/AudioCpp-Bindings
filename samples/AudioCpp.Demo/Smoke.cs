namespace AudioCpp.Demo;

/// <summary>
/// Headless exercise of the view model, for checking the sample still drives the ABI
/// without opening a window. Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Smoke
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: --smoke <model-path> [audio.wav] [task] [family-hint]");
            Console.WriteLine("no model given; skipping");
            return 77;
        }

        var viewModel = new MainWindowViewModel
        {
            ModelPath = args[0],
            AudioPath = args.Length > 1 ? args[1] : "",
            Task = args.Length > 2 ? args[2] : "asr",
            FamilyHint = args.Length > 3 ? args[3] : "",
        };

        if (!File.Exists(viewModel.ModelPath) && !Directory.Exists(viewModel.ModelPath))
        {
            Console.WriteLine($"no model at {viewModel.ModelPath}; skipping");
            return 77;
        }

        Console.WriteLine(viewModel.AbiVersion);

        await viewModel.LoadCommand.ExecuteAsync();
        Console.WriteLine(viewModel.Status);
        if (!viewModel.IsLoaded) return 1;

        Console.WriteLine(viewModel.ModelSummary);
        Console.WriteLine($"declared options: {viewModel.Options.Count}");
        foreach (var option in viewModel.Options.Take(6))
        {
            Console.WriteLine($"  {option.Scope,-8} {option.Name,-34} {option.Type,-12} " +
                              $"default='{option.Default}' {option.Range}");
        }

        await viewModel.RunCommand.ExecuteAsync();
        Console.WriteLine(viewModel.Status);

        if (viewModel.Transcript.Length > 0) Console.WriteLine($"transcript: {viewModel.Transcript}");
        if (viewModel.OutputSamples.Length > 0) Console.WriteLine($"audio: {viewModel.OutputSummary}");
        foreach (var row in viewModel.Rows.Take(6))
        {
            Console.WriteLine($"  {row.Kind,-9} {row.Span,-16} {row.Value}");
        }
        Console.WriteLine($"rows: {viewModel.Rows.Count}");

        var produced = viewModel.Transcript.Length > 0
                       || viewModel.OutputSamples.Length > 0
                       || viewModel.Rows.Count > 0;
        if (!produced)
        {
            Console.WriteLine("the run produced nothing");
            return 1;
        }

        Console.WriteLine("demo smoke OK");
        return 0;
    }
}
