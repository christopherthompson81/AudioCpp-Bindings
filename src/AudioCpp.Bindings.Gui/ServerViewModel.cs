using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioCpp.Server;
using Avalonia.Threading;

namespace AudioCpp.Bindings.Gui;

/// <summary>One model the server will offer, as the page edits it.</summary>
public sealed class ServerModelRow(string id, string path, string family, string task)
    : INotifyPropertyChanged
{
    private string _id = id, _path = path, _family = family, _task = task, _mode = "offline";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string Family { get => _family; set => Set(ref _family, value); }
    public string Task { get => _task; set => Set(ref _task, value); }
    public string Mode { get => _mode; set => Set(ref _mode, value); }

    public IReadOnlyList<string> Tasks { get; } = Workflow.AllTasks;
    public IReadOnlyList<string> Modes { get; } = ["offline", "streaming"];

    public ServerModel ToSpec() => new(Id, Family, Path, Task, Mode);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// The Server page: start, stop, configure, watch.
/// </summary>
/// <remarks>
/// Health is read from the server's own endpoint rather than from the state
/// flag beside it. The flag says what this process believes; the endpoint says
/// what a client would actually get, and those differ exactly when something is
/// wrong -- which is when the page matters.
/// </remarks>
public sealed class ServerPage : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AudioCppServer _server = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private DispatcherTimer? _healthTimer;

    private string _host = "127.0.0.1";
    private int _port = 8080;
    private string _backend = "cpu";
    private int _threads = 1;
    private bool _lazyLoad = true;
    private string _health = "";
    private string _status = "Stopped.";

    public ServerPage()
    {
        StartCommand = new RelayCommand(StartAsync, () => !IsBusy && !IsRunning);
        StopCommand = new RelayCommand(StopAsync, () => !IsBusy && IsRunning);
        AddModelCommand = new RelayCommand(AddModel, () => !IsRunning);
        RemoveModelCommand = new RelayCommand(RemoveModel, () => !IsRunning && SelectedModel is not null);

        _server.Logged += line => Dispatcher.UIThread.Post(() =>
        {
            Log.Add(line);
            if (Log.Count > 500) Log.RemoveAt(0);
        });
    }

    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<ServerModelRow> Models { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand AddModelCommand { get; }
    public RelayCommand RemoveModelCommand { get; }

    public IReadOnlyList<string> Backends { get; } = ["cpu", "cuda", "hip", "vulkan", "metal", "best"];

    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string Backend { get => _backend; set => Set(ref _backend, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public bool LazyLoad { get => _lazyLoad; set => Set(ref _lazyLoad, value); }

    private ServerModelRow? _selectedModel;
    public ServerModelRow? SelectedModel
    {
        get => _selectedModel;
        set { if (Set(ref _selectedModel, value)) RemoveModelCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsRunning => _server.State == ServerState.Running;
    public bool IsBusy => _server.State is ServerState.Starting or ServerState.Stopping;

    /// <summary>The address a client would use.</summary>
    public string Address => $"http://{Host}:{Port}";

    public string State => _server.State.ToString();

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>What /health answered, or why it could not be reached.</summary>
    public string Health { get => _health; private set => Set(ref _health, value); }

    private Task AddModel()
    {
        Models.Add(new ServerModelRow($"model-{Models.Count + 1}", "", "", "tts"));
        SelectedModel = Models[^1];
        return Task.CompletedTask;
    }

    private Task RemoveModel()
    {
        if (SelectedModel is { } row) Models.Remove(row);
        SelectedModel = Models.FirstOrDefault();
        return Task.CompletedTask;
    }

    private async Task StartAsync()
    {
        Refresh();
        await _server.StartAsync(new ServerConfig
        {
            Host = Host,
            Port = Port,
            Backend = Backend,
            Threads = Threads,
            LazyLoad = LazyLoad,
            Models = [.. Models.Select(m => m.ToSpec())],
        });

        Status = _server.State == ServerState.Running
            ? $"Serving on {Address}."
            : $"Could not start: {_server.Problem}";
        Refresh();
        if (IsRunning) StartHealthPolling();
    }

    private async Task StopAsync()
    {
        StopHealthPolling();
        await _server.StopAsync();
        Health = "";
        Status = "Stopped.";
        Refresh();
    }

    private void StartHealthPolling()
    {
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += async (_, _) => await PollHealthAsync();
        _healthTimer.Start();
        _ = PollHealthAsync();
    }

    private void StopHealthPolling()
    {
        _healthTimer?.Stop();
        _healthTimer = null;
    }

    private async Task PollHealthAsync()
    {
        if (!IsRunning) return;
        try
        {
            var response = await _http.GetAsync($"{Address}/health");
            Health = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync()
                : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException)
        {
            // Running by the flag and unreachable over HTTP is the case worth
            // showing: it means the process thinks it is serving and a client
            // disagrees.
            Health = $"unreachable: {exception.Message}";
        }
    }

    private void Refresh()
    {
        Notify(nameof(IsRunning));
        Notify(nameof(IsBusy));
        Notify(nameof(State));
        Notify(nameof(Address));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        AddModelCommand.RaiseCanExecuteChanged();
        RemoveModelCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        StopHealthPolling();
        _http.Dispose();
        await _server.DisposeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        if (name is nameof(Host) or nameof(Port)) Notify(nameof(Address));
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
