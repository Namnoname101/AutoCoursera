using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Acose.CustomerManager;
using Acose.CustomerManager.Runtime;

internal static class Program
{
    private static int _checks;
    private static readonly List<Window> Windows = [];
    private const string OrderId = "local-window-test-only";
    private const string Title = "ACOSE Worker · " + OrderId;

    [STAThread]
    private static int Main()
    {
        try
        {
            CheckCourseModel();
            CheckControllerWithoutWorker();
            CheckNativeWindows();
            Console.WriteLine($"PASS: {_checks} window-visibility checks; no customer profile, engine, or network used.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL after {_checks} checks: {error}");
            return 1;
        }
        finally
        {
            foreach (var window in Windows)
                try { window.Close(); } catch (InvalidOperationException) { }
        }
    }

    private static void CheckCourseModel()
    {
        var course = new CourseItem { Url = "https://www.coursera.org/learn/local-test", State = CourseState.Running, Progress = 42 };
        var changed = new List<string?>();
        course.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Check(!course.CanToggleWorkerWindow, "no HWND disables control");
        Check(course.WorkerWindowButtonText == "Hiện", "no HWND label");
        Check(course.WorkerWindowToolTip.Contains("Chỉ dùng"), "no HWND explanation");
        course.SetWorkerWindowShown(false);
        Check(course.CanToggleWorkerWindow, "hidden HWND enables control");
        Check(course.WorkerWindowButtonText == "Hiện", "hidden HWND label");
        Check(course.WorkerWindowToolTip.Contains("không khởi động thêm"), "show explanation distinguishes existing Worker");
        Check(changed.SequenceEqual(new[] { "CanToggleWorkerWindow", "WorkerWindowButtonText", "WorkerWindowToolTip" }), "visibility notifies all UI bindings");
        changed.Clear();
        course.SetWorkerWindowShown(false);
        Check(changed.Count == 0, "unchanged visibility does not notify");
        course.SetWorkerWindowShown(true);
        Check(course.CanToggleWorkerWindow && course.WorkerWindowButtonText == "Ẩn", "shown HWND label and availability");
        Check(course.WorkerWindowToolTip.Contains("vẫn tiếp tục chạy"), "hide explanation");
        Check(course.State == CourseState.Running && course.Progress == 42, "visibility never changes course progress or execution state");
        var json = JsonSerializer.Serialize(course);
        Check(!json.Contains("WorkerWindow", StringComparison.Ordinal), "runtime visibility excluded from persisted JSON");
        var restored = JsonSerializer.Deserialize<CourseItem>(json)!;
        Check(!restored.CanToggleWorkerWindow && restored.WorkerWindowButtonText == "Hiện", "deserialization cannot restore stale window state");
        course.State = CourseState.Paused;
        Check(course.CanToggleWorkerWindow, "pending-exit HWND can still be controlled after terminal report");
        course.SetWorkerWindowShown(null);
        Check(!course.CanToggleWorkerWindow && course.WorkerWindowButtonText == "Hiện", "exit clears button state");
    }

    private static void CheckNativeWindows()
    {
        using var process = Process.GetCurrentProcess();
        var saved = Identity(process);
        var target = TestWindow(Title);
        nint targetHandle = new WindowInteropHelper(target).Handle;
        var state = WorkerWindow.Read(process, saved);
        Check(state?.Handle == targetHandle, "find expected WPF main window");
        Check(state is { IsVisible: true, IsMinimized: false, IsShown: true }, "read initially visible state");

        for (int iteration = 0; iteration < 3; iteration++)
        {
            Check(WorkerWindow.RequestVisibility(process, saved, false), $"hide request {iteration}");
            Until(() => WorkerWindow.Read(process, saved) is { IsVisible: false }, "native hide takes effect");
            Check(WorkerWindow.Read(process, saved)?.Handle == targetHandle, "hidden HWND rediscovered rather than a replacement");
            Check(WorkerWindow.RequestVisibility(process, saved, true), $"show request {iteration}");
            Until(() => WorkerWindow.Read(process, saved) is { IsShown: true }, "native show takes effect");
            Check(WorkerWindow.Read(process, saved)?.Handle == targetHandle, "show uses same existing window");
        }

        Check(WorkerWindow.RequestVisibility(process, saved, true), "idempotent show request");
        Until(() => WorkerWindow.Read(process, saved) is { IsShown: true }, "idempotent show remains shown");
        ShowWindow(targetHandle, 6); // SW_MINIMIZE, only our transparent local test window.
        Until(() => WorkerWindow.Read(process, saved) is { IsMinimized: true, IsShown: false }, "minimized window is treated as not shown");
        Check(WorkerWindow.RequestVisibility(process, saved, true), "restore minimized request");
        Until(() => WorkerWindow.Read(process, saved) is { IsShown: true, IsMinimized: false }, "show restores minimized window");

        // Test window is transparent, non-activating, click-through and constrained to 32px.
        ShowWindow(targetHandle, 3); // SW_MAXIMIZE.
        Until(() => IsZoomed(targetHandle), "local test window enters maximized state");
        Check(WorkerWindow.RequestVisibility(process, saved, false), "hide maximized request");
        Until(() => WorkerWindow.Read(process, saved) is { IsVisible: false }, "maximized hide takes effect");
        Check(WorkerWindow.RequestVisibility(process, saved, true), "show maximized request");
        Until(() => WorkerWindow.Read(process, saved) is { IsShown: true }, "maximized show takes effect");
        Check(IsZoomed(targetHandle), "hide/show preserves maximization");

        Reject(process, Identity(process, x => x.ProcessId = -1), "mismatched PID");
        Reject(process, Identity(process, x => x.StartTimeUtc = x.StartTimeUtc.AddSeconds(2)), "mismatched start time");
        Reject(process, Identity(process, x => x.ExecutablePath += ".different"), "mismatched executable");
        Reject(process, Identity(process, x => x.Mode = "browse"), "browse mode");
        Reject(process, Identity(process, x => x.OrderId = "other-order"), "different customer title");

        target.Title = Title + "-unrelated";
        Reject(process, saved, "prefix-only title match");
        target.Title = "Foreign local test window";
        Reject(process, saved, "unrelated main title");
        var ownedPopup = TestWindow(Title, target);
        Check(new WindowInteropHelper(ownedPopup).Owner == targetHandle, "popup has actual native owner");
        Reject(process, saved, "owned popup with otherwise matching title");
        ownedPopup.Close();
        target.Title = Title;
        Check(WorkerWindow.Read(process, saved)?.Handle == targetHandle, "correct main title resolves again");
        target.Close();
        Reject(process, saved, "closed main window");

        nint foreignClass = CreateWindowEx(0x08000080, "STATIC", Title, 0, -32000, -32000, 1, 1, 0, 0, 0, 0);
        Check(foreignClass != 0, "local non-WPF fixture created");
        try { Reject(process, saved, "matching title on non-WPF window"); }
        finally { if (foreignClass != 0) DestroyWindow(foreignClass); }
    }

    private static Window TestWindow(string title, Window? owner = null)
    {
        var window = new Window
        {
            Title = title, Width = 32, Height = 32, MaxWidth = 32, MaxHeight = 32,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = true, Opacity = 0,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, IsHitTestVisible = false,
            Owner = owner
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            nint style = GetWindowLongPtr(hwnd, -20);
            SetWindowLongPtr(hwnd, -20, style | (nint)0x080000A0); // NOACTIVATE, TOOLWINDOW, TRANSPARENT.
        };
        Windows.Add(window);
        window.Show();
        return window;
    }

    private static void CheckControllerWithoutWorker()
    {
        string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AcoseWindowChecks-" + Guid.NewGuid().ToString("N"));
        var controller = new ManagerController(scratch, System.IO.Path.Combine(scratch, "no-engine.exe"));
        var order = new CustomerOrder { Name = "Local fixture only" };
        var course = new CourseItem { State = CourseState.Paused, Progress = 63, Activity = "Original status" };
        course.SetWorkerWindowShown(true);
        order.Courses.Add(course);
        controller.Orders.Add(order);
        var action = controller.ToggleWorkerWindowAsync(order, course);
        Check(action.IsCompletedSuccessfully, "no-worker toggle finishes without launching missing engine");
        Check(!course.CanToggleWorkerWindow, "no-worker toggle clears stale HWND availability");
        Check(course.State == CourseState.Paused && course.Progress == 63 && course.Activity == "Original status", "no-worker toggle preserves course status");
        Check(!controller.HasActiveWork, "no-worker toggle does not create active run");
        Check(controller.StatusMessage.Contains("không có cửa sổ"), "no-worker toggle explains unavailable window");
        Check(!System.IO.Directory.Exists(scratch), "no-worker toggle does not persist or create profile/data directories");
    }

    private static SavedRun Identity(Process process, Action<SavedRun>? change = null)
    {
        var saved = new SavedRun
        {
            ProcessId = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(),
            ExecutablePath = process.MainModule!.FileName, OrderId = OrderId,
            CourseId = "local-course", JobId = "local-job", Mode = "course"
        };
        change?.Invoke(saved);
        return saved;
    }

    private static void Reject(Process process, SavedRun saved, string label)
    {
        Check(WorkerWindow.Read(process, saved) is null, label + " read rejected");
        Check(!WorkerWindow.RequestVisibility(process, saved, true), label + " show rejected");
        Check(!WorkerWindow.RequestVisibility(process, saved, false), label + " hide rejected");
    }

    private static void Until(Func<bool> condition, string label)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Check(condition(), label);
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException(label);
        _checks++;
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
