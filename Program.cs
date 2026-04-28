using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Apollo Disconnector only runs on Windows.");
    return 1;
}

var config = AppConfig.Load();
var forceSetup = args.Any(arg => arg.Equals("--setup", StringComparison.OrdinalIgnoreCase));
var uninstall = args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
if (uninstall)
{
    ConsoleManager.Show();
    Uninstaller.Run();
    return 0;
}

if (forceSetup || !config.HasRequiredSettings)
{
    ConsoleManager.Show();
    config = await FirstRunWizard.ConfigureAsync(config);

    if (AppLauncher.TryStartHidden())
    {
        Console.WriteLine();
        Console.WriteLine("Apollo Disconnector is now running quietly in the background.");
        Console.WriteLine("You can close this setup window.");
        return 0;
    }
}

using var appMutex = new Mutex(initiallyOwned: true, "ApolloDisconnector.SingleInstance", out var ownsMutex);
if (!ownsMutex)
{
    return 0;
}

var disconnector = new ApolloDisconnector(config);

ConsoleManager.WriteLineIfVisible("Apollo Disconnector is running.");
ConsoleManager.WriteLineIfVisible("Press any key on a local keyboard to close the active Apollo session.");
ConsoleManager.WriteLineIfVisible($"Apollo Web UI: {config.ApolloWebUrl}");
ConsoleManager.WriteLineIfVisible($"Settings: {AppConfig.ConfigPath}");

using var hook = config.EnableLowLevelKeyboardHook
    ? new LocalKeyboardHook(() => disconnector.TriggerAsync("low-level keyboard hook"))
    : null;
using var monitor = new RawKeyboardMonitor(config, source => disconnector.TriggerAsync(source));
hook?.Start();
monitor.Run();
return 0;

internal sealed class ApolloDisconnector
{
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookies = new();
    private long _lastTriggerTicks;
    private bool _isLoggedIn;

    public ApolloDisconnector(AppConfig config)
    {
        _config = config;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true
        };
        if (config.AllowInvalidCertificateForLocalhost)
        {
            handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                {
                    return true;
                }

                return request.RequestUri is { IsLoopback: true };
            };
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds)
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task TriggerAsync(string source)
    {
        var now = Stopwatch.GetTimestamp();
        var cooldownTicks = Stopwatch.Frequency * _config.TriggerCooldownSeconds;
        var previous = Interlocked.Read(ref _lastTriggerTicks);

        if (previous != 0 && now - previous < cooldownTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastTriggerTicks, now, previous) != previous)
        {
            return;
        }

        try
        {
            Console.WriteLine($"[{DateTimeOffset.Now:T}] Local keyboard input detected from {source}. Closing Apollo session...");

            await EnsureLoggedInAsync();
            if (!await HasActiveSessionAsync())
            {
                Console.WriteLine($"[{DateTimeOffset.Now:T}] Apollo has no active session; nothing to close.");
                return;
            }

            if (await PostApolloAsync(_config.ApiUrl, "Apollo session close"))
            {
                return;
            }

            var resetUrl = BuildSiblingApiUrl("reset-display-device-persistence");
            Console.WriteLine($"[{DateTimeOffset.Now:T}] Trying Apollo display persistence reset...");
            _ = await PostApolloAsync(resetUrl, "Apollo display persistence reset");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] Failed to close Apollo session: {ex.Message}");
        }
    }

    private async Task<bool> PostApolloAsync(string url, string description)
    {
        using var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.PostAsync(url, content);
        var responseBody = await response.Content.ReadAsStringAsync();
        var apiSucceeded = ResponseIndicatesSuccess(responseBody);

        if (response.IsSuccessStatusCode && apiSucceeded)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:T}] {description} request succeeded ({(int)response.StatusCode}).");
            return true;
        }

        Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] {description} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            Console.Error.WriteLine($"Apollo response: {responseBody}");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _isLoggedIn = false;
            Console.Error.WriteLine("Check the username and password in appsettings.json.");
        }

        return false;
    }

    private async Task EnsureLoggedInAsync()
    {
        if (_isLoggedIn)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
        {
            throw new InvalidOperationException("Set Username and Password in appsettings.json.");
        }

        var loginUrl = BuildSiblingApiUrl("login");
        var loginPayload = JsonSerializer.Serialize(new
        {
            username = _config.Username,
            password = _config.Password
        });

        using var content = new StringContent(loginPayload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(loginUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Apollo login failed with {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
        }

        _isLoggedIn = true;
        Console.WriteLine($"[{DateTimeOffset.Now:T}] Logged into Apollo Web UI.");
    }

    public Task TestLoginAsync()
    {
        return EnsureLoggedInAsync();
    }

    private async Task<bool> HasActiveSessionAsync()
    {
        using var response = await _httpClient.GetAsync(BuildSiblingApiUrl("apps"));
        if (!response.IsSuccessStatusCode)
        {
            return true;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            return json.RootElement.TryGetProperty("current_app", out var currentApp)
                && currentApp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(currentApp.GetString());
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ResponseIndicatesSuccess(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return true;
        }

        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (json.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        catch (JsonException)
        {
        }

        return true;
    }

    private string BuildSiblingApiUrl(string endpoint)
    {
        var apiUri = new Uri(_config.ApiUrl);
        return new Uri(apiUri, $"/api/{endpoint}").ToString();
    }
}

internal static class FirstRunWizard
{
    public static async Task<AppConfig> ConfigureAsync(AppConfig current)
    {
        Console.WriteLine("Apollo Disconnector setup");
        Console.WriteLine("This only needs to be done once. Press Enter to accept any value shown in [brackets].");
        Console.WriteLine();

        while (true)
        {
            var webUrl = Prompt("Apollo Web UI URL", current.ApolloWebUrl);
            var username = Prompt("Apollo Web UI username", current.HasRealUsername ? current.Username : null);
            var password = PromptPassword("Apollo Web UI password", current.HasRealPassword ? current.Password : null);

            var configured = new AppConfig
            {
                ApiUrl = AppConfig.BuildCloseApiUrl(webUrl),
                Username = username,
                Password = password,
                TriggerCooldownSeconds = current.TriggerCooldownSeconds,
                RequestTimeoutSeconds = current.RequestTimeoutSeconds,
                AllowInvalidCertificateForLocalhost = current.AllowInvalidCertificateForLocalhost,
                EnableLowLevelKeyboardHook = current.EnableLowLevelKeyboardHook,
                LogKeyboardDevices = current.LogKeyboardDevices,
                LocalKeyboardDeviceNameContains = current.LocalKeyboardDeviceNameContains,
                IgnoredKeyboardDeviceNameContains = current.IgnoredKeyboardDeviceNameContains
            };

            Console.WriteLine($"Testing Apollo login at {configured.ApolloWebUrl}...");
            try
            {
                await new ApolloDisconnector(configured).TestLoginAsync();
                AppConfig.Save(configured);
                Console.WriteLine($"Settings saved to {AppConfig.ConfigPath}");

                if (PromptYesNo("Start Apollo Disconnector automatically when you log into Windows?", defaultValue: true))
                {
                    StartupInstaller.Install();
                }

                Console.WriteLine();
                return configured;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Setup failed: {ex.Message}");
                if (!PromptYesNo("Try setup again?", defaultValue: true))
                {
                    throw;
                }
            }
        }
    }

    private static string Prompt(string label, string? defaultValue)
    {
        while (true)
        {
            Console.Write(string.IsNullOrWhiteSpace(defaultValue)
                ? $"{label}: "
                : $"{label} [{defaultValue}] (press Enter to use this): ");
            var value = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                return defaultValue;
            }
        }
    }

    private static string PromptPassword(string label, string? defaultValue)
    {
        Console.Write(string.IsNullOrWhiteSpace(defaultValue) ? $"{label}: " : $"{label} [saved; press Enter to keep]: ");
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            builder.Append(key.KeyChar);
            Console.Write("*");
        }

        return builder.Length == 0 && !string.IsNullOrWhiteSpace(defaultValue)
            ? defaultValue
            : builder.ToString();
    }

    private static bool PromptYesNo(string label, bool defaultValue)
    {
        var suffix = defaultValue ? " [Y/n, Enter = yes]: " : " [y/N, Enter = no]: ";
        while (true)
        {
            Console.Write(label + suffix);
            var value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            if (value.Equals("y", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("n", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }
}

internal static class StartupInstaller
{
    private const string StartupScriptName = "ApolloDisconnector.vbs";

    public static string StartupScriptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupScriptName);

    public static bool CanInstallOrLaunch =>
        Environment.ProcessPath is { } path
        && !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public static void Install()
    {
        var executablePath = Environment.ProcessPath;
        if (!CanInstallOrLaunch || string.IsNullOrWhiteSpace(executablePath))
        {
            Console.WriteLine("Startup install skipped while running through dotnet. Install startup from the published exe.");
            return;
        }

        var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var scriptPath = StartupScriptPath;
        var workingDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var script = "Set shell = CreateObject(\"WScript.Shell\")" + Environment.NewLine
            + $"shell.CurrentDirectory = \"{EscapeVbs(workingDirectory)}\"" + Environment.NewLine
            + $"shell.Run Chr(34) & \"{EscapeVbs(executablePath)}\" & Chr(34), 0, False" + Environment.NewLine;

        File.WriteAllText(scriptPath, script);

        Console.WriteLine($"Startup entry installed: {scriptPath}");
    }

    public static bool Uninstall()
    {
        var scriptPath = StartupScriptPath;
        if (!File.Exists(scriptPath))
        {
            return false;
        }

        File.Delete(scriptPath);
        return true;
    }

    private static string EscapeVbs(string value)
    {
        return value.Replace("\"", "\"\"");
    }
}

internal static class Uninstaller
{
    public static void Run()
    {
        Console.WriteLine("Apollo Disconnector uninstall");
        Console.WriteLine();

        var removedStartup = StartupInstaller.Uninstall();
        Console.WriteLine(removedStartup
            ? $"Removed startup entry: {StartupInstaller.StartupScriptPath}"
            : "No startup entry was found.");

        var removedSettings = AppConfig.DeleteSettings();
        Console.WriteLine(removedSettings
            ? $"Removed settings folder: {AppConfig.ConfigDirectory}"
            : "No settings folder was found.");

        Console.WriteLine();
        Console.WriteLine("Uninstall complete. You can delete ApolloDisconnector.exe whenever you want.");
        Console.WriteLine("Press Enter to close this window.");
        _ = Console.ReadLine();
    }
}

internal static class AppLauncher
{
    public static bool TryStartHidden()
    {
        var executablePath = Environment.ProcessPath;
        if (!StartupInstaller.CanInstallOrLaunch || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        return true;
    }
}

internal static class ConsoleManager
{
    private static bool _visible;

    public static void Show()
    {
        if (_visible)
        {
            return;
        }

        if (GetConsoleWindow() == IntPtr.Zero)
        {
            AllocConsole();
        }

        _visible = true;
    }

    public static void WriteLineIfVisible(string value)
    {
        if (_visible || GetConsoleWindow() != IntPtr.Zero)
        {
            Console.WriteLine(value);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}

internal sealed class RawKeyboardMonitor : IDisposable
{
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RID_INPUT = 0x10000003;
    private const int RIDI_DEVICENAME = 0x20000007;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int WM_INPUT = 0x00FF;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly AppConfig _config;
    private readonly Func<string, Task> _onKeyboardInput;
    private readonly WndProc _wndProc;
    private readonly string _className;
    private readonly ConcurrentDictionary<string, byte> _loggedDevices = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _windowHandle;

    public RawKeyboardMonitor(AppConfig config, Func<string, Task> onKeyboardInput)
    {
        _config = config;
        _onKeyboardInput = onKeyboardInput;
        _wndProc = WindowProc;
        _className = $"ApolloDisconnector_{Guid.NewGuid():N}";
    }

    public void Run()
    {
        RegisterWindowClass();
        CreateHiddenWindow();
        RegisterKeyboardDevice();

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_INPUT && TryGetAcceptedKeyboardSource(lParam, out var source))
        {
            _ = Task.Run(() => _onKeyboardInput(source));
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool TryGetAcceptedKeyboardSource(IntPtr rawInputHandle, out string source)
    {
        source = string.Empty;
        uint size = 0;
        _ = GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (read != size)
            {
                return false;
            }

            var input = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (input.header.dwType != RIM_TYPEKEYBOARD
                || (input.data.keyboard.Message != WM_KEYDOWN && input.data.keyboard.Message != WM_SYSKEYDOWN))
            {
                return false;
            }

            var deviceName = GetDeviceName(input.header.hDevice);
            LogDeviceOnce(deviceName);

            if (!IsAcceptedDevice(deviceName))
            {
                return false;
            }

            source = $"raw input device '{deviceName}'";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void LogDeviceOnce(string deviceName)
    {
        if (!_config.LogKeyboardDevices || !_loggedDevices.TryAdd(deviceName, 0))
        {
            return;
        }

        var action = IsAcceptedDevice(deviceName) ? "accepted" : "ignored";
        Console.WriteLine($"[{DateTimeOffset.Now:T}] Keyboard device {action}: {deviceName}");
    }

    private bool IsAcceptedDevice(string deviceName)
    {
        if (_config.LocalKeyboardDeviceNameContains.Length > 0)
        {
            return _config.LocalKeyboardDeviceNameContains.Any(part =>
                deviceName.Contains(part, StringComparison.OrdinalIgnoreCase));
        }

        return !_config.IgnoredKeyboardDeviceNameContains.Any(part =>
            deviceName.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDeviceName(IntPtr deviceHandle)
    {
        if (deviceHandle == IntPtr.Zero)
        {
            return "<unknown>";
        }

        uint size = 0;
        _ = GetRawInputDeviceInfo(deviceHandle, RIDI_DEVICENAME, null, ref size);
        if (size == 0)
        {
            return "<unknown>";
        }

        var builder = new StringBuilder((int)size);
        return GetRawInputDeviceInfo(deviceHandle, RIDI_DEVICENAME, builder, ref size) == uint.MaxValue
            ? "<unknown>"
            : builder.ToString();
    }

    private void RegisterWindowClass()
    {
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed with error {Marshal.GetLastWin32Error()}.");
        }
    }

    private void CreateHiddenWindow()
    {
        _windowHandle = CreateWindowEx(
            0,
            _className,
            "Apollo Disconnector",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed with error {Marshal.GetLastWin32Error()}.");
        }
    }

    private void RegisterKeyboardDevice()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = 0x06,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = _windowHandle
        };

        if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed with error {Marshal.GetLastWin32Error()}.");
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        StringBuilder? pData,
        ref uint pcbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTDATA data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUTDATA
    {
        [FieldOffset(0)]
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }
}

internal sealed class LocalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int LLKHF_LOWER_IL_INJECTED = 0x02;
    private const int LLKHF_INJECTED = 0x10;

    private readonly Func<Task> _onKeyboardInput;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle;

    public LocalKeyboardHook(Func<Task> onKeyboardInput)
    {
        _onKeyboardInput = onKeyboardInput;
        _hookProc = HookProc;
    }

    public void Start()
    {
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed with error {Marshal.GetLastWin32Error()}.");
        }

        Console.WriteLine("Local keyboard hook enabled; injected remote input will be ignored.");
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsKeyDownMessage(wParam))
        {
            var keyboard = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isInjected = (keyboard.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;

            if (!isInjected)
            {
                _ = Task.Run(_onKeyboardInput);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsKeyDownMessage(IntPtr wParam)
    {
        var message = wParam.ToInt32();
        return message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public int flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

internal sealed class AppConfig
{
    private const string FileName = "appsettings.json";
    private const string PlaceholderUsername = "apollo-username";
    private const string PlaceholderPassword = "apollo-password";

    public string ApiUrl { get; init; } = "https://localhost:47990/api/apps/close";
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int TriggerCooldownSeconds { get; init; } = 10;
    public int RequestTimeoutSeconds { get; init; } = 5;
    public bool AllowInvalidCertificateForLocalhost { get; init; } = true;
    public bool EnableLowLevelKeyboardHook { get; init; }
    public bool LogKeyboardDevices { get; init; } = true;
    public string[] LocalKeyboardDeviceNameContains { get; init; } = Array.Empty<string>();
    public string[] IgnoredKeyboardDeviceNameContains { get; init; } = new[]
    {
        "<unknown>",
        "ROOT",
        "VIRTUAL",
        "REMOTE",
        "MOONLIGHT",
        "SUNSHINE",
        "APOLLO",
        "VIGEM"
    };

    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ApolloDisconnector");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, FileName);

    public string ApolloWebUrl
    {
        get
        {
            var apiUri = new Uri(ApiUrl);
            return new Uri(apiUri, "/").ToString().TrimEnd('/');
        }
    }

    public bool HasRealUsername =>
        !string.IsNullOrWhiteSpace(Username)
        && !Username.Equals(PlaceholderUsername, StringComparison.OrdinalIgnoreCase);

    public bool HasRealPassword =>
        !string.IsNullOrWhiteSpace(Password)
        && !Password.Equals(PlaceholderPassword, StringComparison.OrdinalIgnoreCase);

    public bool HasRequiredSettings =>
        !string.IsNullOrWhiteSpace(ApiUrl)
        && HasRealUsername
        && HasRealPassword;

    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            return LoadFrom(ConfigPath);
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(localPath))
        {
            var localConfig = LoadFrom(localPath);
            if (localConfig.HasRequiredSettings)
            {
                Save(localConfig);
                return localConfig;
            }

            return localConfig;
        }

        return new AppConfig
        {
            Username = PlaceholderUsername,
            Password = PlaceholderPassword
        };
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static bool DeleteSettings()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            return false;
        }

        Directory.Delete(ConfigDirectory, recursive: true);
        return true;
    }

    public static string BuildCloseApiUrl(string webUrl)
    {
        var normalized = webUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || webUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? webUrl
                : $"https://{webUrl}";

        return new Uri(new Uri(normalized.TrimEnd('/') + "/"), "api/apps/close").ToString();
    }

    private static AppConfig LoadFrom(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
