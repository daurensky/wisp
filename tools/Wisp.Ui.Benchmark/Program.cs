using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;

namespace Wisp.Ui.Benchmark;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int InputHistoryCount = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UserProfile Profile = new("00000000000000000000000000000001", "Benchmark");

    [STAThread]
    private static int Main(string[] args)
    {
        var entry = Stopwatch.StartNew();
        var options = ParseArguments(args);
        string root = Required(options, "fixture");
        if (options.GetValueOrDefault("mode") == "prepare")
        {
            PrepareFixture(root);
            return 0;
        }

        string output = Required(options, "output");
        var result = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["label"] = options.GetValueOrDefault("label", "unknown"),
            ["revision"] = options.GetValueOrDefault("revision", "unknown"),
            ["process_run"] = int.Parse(options.GetValueOrDefault("run", "0")),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["logical_processors"] = Environment.ProcessorCount,
            ["window_width_dip"] = 1280,
            ["window_height_dip"] = 800,
            ["scope"] = "Cached local UI; no authentication/network or physical audio/video capture.",
            ["cold_cache_controlled"] = false,
            ["startup_definition"] = "First/repeated process in series; OS file/assembly caches are uncontrolled. Fixture preparation is excluded.",
            ["unavailable"] = new[]
            {
                "OS-cold-cache launch and online login/server response time",
                "Physical microphone and continuous voice-media CPU",
                "Physical GPU/capture, 1080p60 sharing and monitor latency",
                "Active GIF animation under real pointer interaction"
            }
        };
        LocalStore? store = null;
        Application? app = null;
        MainWindow? main = null;
        string phase = "initialize";
        try
        {
            store = new LocalStore(root);
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Wisp;component/Theme.xaml")
            });
            ThemeManager.Initialize(root);
            result["render_tier"] = RenderCapability.Tier >> 16;
            result["system_client_area_animation"] = SystemParameters.ClientAreaAnimation;
            result["theme"] = ThemeManager.IsLight ? "light" : "dark";
            result["reduce_motion"] = Motion.Reduce;
            phase = "construct and render Home";
            var construction = Stopwatch.StartNew();
            main = new MainWindow(store, Profile) { Width = 1280, Height = 800 };
            result["main_construction_ms"] = construction.Elapsed.TotalMilliseconds;
            bool contentRendered = false;
            main.ContentRendered += (_, _) => contentRendered = true;
            main.Show();
            PumpUntil(main, () => contentRendered, "first ContentRendered");
            result["entry_to_home_ready_ms"] = entry.Elapsed.TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            result["process_start_to_home_ready_ms"] = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            PumpFor(main, TimeSpan.FromMilliseconds(400));

            phase = "Home idle";
            result["home_idle"] = MeasureIdle(main, process);
            var communities = store.GetCommunities();
            if (communities.Count != 2) throw new InvalidDataException("Expected two prepared communities.");
            var navigation = new MainWindowAccess(main);
            phase = "server switches";
            navigation.Invoke("SetCommunity", communities[0]);
            Pump(main);
            result["server_switch"] = Measure(main, 24, index => navigation.Invoke("SetCommunity", communities[(index + 1) % 2]));
            navigation.Invoke("SetCommunity", communities[0]);
            Pump(main);
            var channels = store.GetChannels(communities[0].Id).Where(channel => !channel.IsVoice).ToArray();
            phase = "channel switches";
            result["channel_switch_100_cached_messages"] = Measure(main, 32,
                index => navigation.Invoke("SelectChannel", channels[(index + 1) % channels.Length]));

            phase = "unchanged presence UI refresh";
            var voiceChannel = store.GetChannels(communities[0].Id).First(channel => channel.IsVoice);
            var peers = store.GetMembers(communities[0].Id).Where(person => person.Id != Profile.Id)
                .Select((person, index) => new PeerPresence(person, index % 2 == 0 ? voiceChannel.Id : null, index % 3 == 0, false)).ToArray();
            navigation.SetField("presence", peers);
            navigation.Invoke("RenderChannels");
            navigation.Invoke("RenderMembers");
            Pump(main);
            result["presence_fixture_remote_peers"] = peers.Length;
            result["presence_fixture_definition"] = "Controlled online UI roster only; no media connection or physical microphone.";
            result["unchanged_presence_ui_refresh"] = Measure(main, 32, _ =>
            {
                navigation.Invoke("RenderChannels");
                navigation.Invoke("RenderMembers");
                navigation.Invoke("UpdateStatus");
            });

            phase = "long history materialization";
            var history = CreateMessages(channels[0].Id, InputHistoryCount).ToArray();
            result["long_history_load"] = Measure(main, 1, _ => navigation.Invoke("LoadHistory", (object)history));
            var messages = navigation.Field<VirtualRows>("messages");
            var scroll = Descendants(messages).OfType<ScrollViewer>().FirstOrDefault()
                ?? throw new InvalidOperationException("Virtual message list did not create a ScrollViewer.");
            if (scroll.ScrollableHeight <= 0) throw new InvalidOperationException("History did not produce a scrollable viewport.");
            int maxRealized = CountRealized(messages);
            result["long_history_scroll"] = Measure(main, 48, index =>
            {
                double position = (index % 16) / 15d;
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * (index / 16 % 2 == 0 ? position : 1 - position));
            }, () => maxRealized = Math.Max(maxRealized, CountRealized(messages)));
            result["history_input_count"] = InputHistoryCount;
            result["history_retained_count"] = messages.Count;
            result["history_max_realized_presenters"] = maxRealized;
            result["history_scrollable_height"] = scroll.ScrollableHeight;
            result["history_viewport_height"] = scroll.ViewportHeight;
            result["history_memory"] = Memory(process);

            phase = "repeat navigation memory";
            for (int index = 0; index < 20; index++)
            {
                navigation.Invoke("SetCommunity", communities[index % 2]);
                Pump(main);
            }
            result["after_20_server_switches_memory"] = Memory(process);
            phase = "chat idle";
            result["chat_idle"] = MeasureIdle(main, process);
            result["status"] = "success";
        }
        catch (Exception error)
        {
            result["status"] = "failure";
            result["phase"] = phase;
            result["error"] = error.ToString();
        }
        finally
        {
            try
            {
                if (main is not null)
                {
                    main.Quit();
                    PumpUntil(main, () => !main.IsVisible, "window shutdown");
                }
                app?.Shutdown();
                store?.Dispose();
            }
            catch (Exception error)
            {
                result["status"] = "failure";
                result["shutdown_error"] = error.ToString();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(result, JsonOptions));
        }
        return Equals(result["status"], "success") ? 0 : 1;
    }

    private static void PrepareFixture(string root)
    {
        Directory.CreateDirectory(root);
        using var store = new LocalStore(root);
        store.SaveProfile(Profile);
        var people = Enumerable.Range(0, 12)
            .Select(index => index == 0 ? Profile : new UserProfile(Id(100 + index), $"Member{index:00}")).ToList();
        for (int group = 0; group < 2; group++)
        {
            var community = new Community(Id(1000 + group), $"Benchmark server {group + 1}", Profile.Id);
            var channels = Enumerable.Range(0, 10).Select(index =>
                new Channel(Id(2000 + group * 100 + index), community.Id, $"channel-{index:00}", index >= 8)).ToList();
            store.SaveSnapshot(new CommunitySnapshot(community, channels, people));
            foreach (var channel in channels.Where(channel => !channel.IsVoice))
                foreach (var message in CreateMessages(channel.Id, 100)) store.SaveMessage(message);
        }
    }

    private static IEnumerable<ChatMessage> CreateMessages(string channel, int count)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < count; index++)
        {
            int author = index % 12;
            yield return new ChatMessage(channel + index.ToString("x8"), channel,
                author == 0 ? Profile.Id : Id(100 + author), author == 0 ? Profile.Username : $"Member{author:00}",
                $"Message {index:0000}. A deterministic native WPF chat fixture with a wrapped line.\nSecond line of cached chat text.",
                start.AddSeconds(index * 7));
        }
    }

    private static Dictionary<string, object> Measure(Window window, int count, Action<int> action, Action? afterPump = null)
    {
        var samples = new double[count];
        long allocated = GC.GetTotalAllocatedBytes();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        for (int index = 0; index < count; index++)
        {
            var watch = Stopwatch.StartNew();
            action(index);
            Pump(window);
            samples[index] = watch.Elapsed.TotalMilliseconds;
            afterPump?.Invoke();
        }
        var sorted = samples.Order().ToArray();
        return new()
        {
            ["count"] = count,
            ["median_ms"] = sorted[sorted.Length / 2],
            ["p95_ms"] = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            ["maximum_ms"] = sorted[^1],
            ["samples_ms"] = samples,
            ["allocated_bytes"] = GC.GetTotalAllocatedBytes() - allocated,
            ["process_cpu_ms"] = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            ["gen0_collections"] = GC.CollectionCount(0) - collections[0],
            ["gen1_collections"] = GC.CollectionCount(1) - collections[1],
            ["gen2_collections"] = GC.CollectionCount(2) - collections[2],
            ["definition"] = "Action plus WPF layout/dispatcher through ApplicationIdle; not physical frame presentation latency."
        };
    }

    private static Dictionary<string, object> MeasureIdle(Window window, Process process)
    {
        PumpFor(window, TimeSpan.FromMilliseconds(250));
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        PumpFor(window, TimeSpan.FromSeconds(2));
        double elapsed = watch.Elapsed.TotalMilliseconds;
        double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        return new()
        {
            ["duration_ms"] = elapsed,
            ["process_cpu_ms"] = cpuMilliseconds,
            ["cpu_percent_one_logical_core"] = cpuMilliseconds / elapsed * 100,
            ["cpu_percent_machine"] = cpuMilliseconds / elapsed / Environment.ProcessorCount * 100,
            ["memory"] = Memory(process)
        };
    }

    private static Dictionary<string, object> Memory(Process process)
    {
        process.Refresh();
        return new()
        {
            ["private_bytes"] = process.PrivateMemorySize64,
            ["working_set_bytes"] = process.WorkingSet64,
            ["managed_heap_bytes_without_collection"] = GC.GetTotalMemory(false),
            ["total_allocated_bytes"] = GC.GetTotalAllocatedBytes(),
            ["gen0_collections"] = GC.CollectionCount(0),
            ["gen1_collections"] = GC.CollectionCount(1),
            ["gen2_collections"] = GC.CollectionCount(2)
        };
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void PumpFor(Window window, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump(window);
    }

    private static void PumpUntil(Window window, Func<bool> condition, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5)) PumpFor(window, TimeSpan.FromMilliseconds(10));
        if (!condition()) throw new TimeoutException(description);
        Pump(window);
    }

    private static int CountRealized(VirtualRows rows) => Descendants(rows).OfType<VirtualRows.RowPresenter>().Count();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Expected --name value arguments.");
            values.Add(args[index][2..], args[index + 1]);
        }
        return values;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.GetValueOrDefault(key) ?? throw new ArgumentException($"Missing --{key}.");
    private static string Id(int value) => value.ToString("x32");

    private sealed class MainWindowAccess(MainWindow window)
    {
        public void Invoke(string method, params object[] arguments)
        {
            try
            {
                (typeof(MainWindow).GetMethod(method, PrivateInstance)
                    ?? throw new MissingMethodException(typeof(MainWindow).FullName, method)).Invoke(window, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            }
        }
        public void SetField(string name, object value) =>
            (typeof(MainWindow).GetField(name, PrivateInstance)
                ?? throw new MissingFieldException(typeof(MainWindow).FullName, name)).SetValue(window, value);
        public T Field<T>(string name) where T : class =>
            (typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(window) as T)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);
    }
}
