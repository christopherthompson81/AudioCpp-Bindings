using Avalonia.Threading;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Drives the microphone path inside a real window, on the real dispatcher.
/// </summary>
/// <remarks>
/// The headless --smoke path cannot verify live updates: every callback from the
/// capture pump hops through Dispatcher.UIThread, and with no dispatcher loop
/// running those posts are queued and never executed. A headless run therefore
/// reports a level of zero and an empty live transcript while the underlying
/// audio is flowing perfectly — which looks exactly like a broken meter.
///
/// This runs the same sequence with a window up, so the posts actually execute.
/// </remarks>
internal static class LiveCheck
{
    internal static void Arm(MainWindowViewModel viewModel, string[] args, int seconds)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var failures = 0;
            try
            {
                foreach (var assignment in args.Where(a => a.Contains('=')))
                {
                    var parts = assignment.Split('=', 2);
                    switch (parts[0])
                    {
                        case "model": viewModel.ModelPath = parts[1]; break;
                        case "family": viewModel.FamilyHint = parts[1]; break;
                        case "backend": viewModel.Backend = parts[1]; break;
                        case "capture":
                            viewModel.CaptureDevice = viewModel.CaptureDevices
                                .FirstOrDefault(d => d.Index == int.Parse(parts[1]));
                            break;
                    }
                }

                await viewModel.LoadCommand.ExecuteAsync();
                Console.WriteLine($"load: {viewModel.Status}");
                if (!viewModel.IsLoaded) { Console.Error.WriteLine("model did not load"); failures++; }

                if (failures == 0)
                {
                    await viewModel.RecordCommand.ExecuteAsync();
                    Console.WriteLine($"recording: {viewModel.IsRecording}");

                    var peak = 0f;
                    var liveSeen = "";
                    for (var i = 0; i < seconds * 4; i++)
                    {
                        await Task.Delay(250);
                        peak = Math.Max(peak, viewModel.InputLevel);
                        if (viewModel.Transcript.Length > liveSeen.Length) liveSeen = viewModel.Transcript;
                    }

                    await viewModel.RecordCommand.ExecuteAsync();

                    Console.WriteLine($"peak level while recording: {peak:F4}");
                    Console.WriteLine($"live transcript seen: {(liveSeen.Length > 0 ? "yes" : "no")}"
                                      + (liveSeen.Length > 0 ? $" ({liveSeen.Length} chars)" : ""));
                    Console.WriteLine($"final: {viewModel.Transcript}");
                    Console.WriteLine($"status: {viewModel.Status}");

                    // The meter is the point of this check: a live path that only
                    // produces text at the end would pass the headless smoke.
                    if (peak <= 0) { Console.Error.WriteLine("meter never moved"); failures++; }
                    if (liveSeen.Length == 0) { Console.Error.WriteLine("no live transcript"); failures++; }
                    if (viewModel.IsRecording) { Console.Error.WriteLine("still recording"); failures++; }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"live check threw: {exception}");
                failures++;
            }

            Console.WriteLine(failures == 0 ? "live check OK" : $"live check: {failures} failure(s)");
            Environment.Exit(failures == 0 ? 0 : 1);
        });
    }
}
