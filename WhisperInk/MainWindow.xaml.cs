using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace WhisperInk;

public class AppConfig
{
    public string MistralApiKey { get; set; } = "setup-your-key-here";
    public bool IsSoundEnabled { get; set; } = true;
    public string SystemPrompt { get; set; } = "You are a precise execution engine. Output ONLY the direct result of the task. Do not say 'Here is the translation' or 'Sure'. Do not provide explanations, alternatives, or conversational filler. Just the result.";
}

public partial class MainWindow : Window
{
    private enum RecordingMode
    {
        None,
        Inject,         // Ctrl + Win -> Вставка текста
        AnalyzeContext  // Ctrl + Alt -> Анализ контекста + Генерация
    }

    private string _mistralApiKey = "";
    private const string ConfigFileName = "config.json";

    private string _systemPrompt = new AppConfig().SystemPrompt;

    // --- API MISTRAL ---
    private const string AudioApiUrl = "https://api.mistral.ai/v1/audio/transcriptions";
    private const string AudioModel = "voxtral-mini-latest";

    private const string ChatApiUrl = "https://api.mistral.ai/v1/chat/completions";
    private const string ChatModel = "mistral-medium-latest";

    // --- КОДЫ КЛАВИШ ---
    private const int WH_KEYBOARD_LL = 13;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const int VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static readonly LowLevelKeyboardProc _proc = HookCallback;
    private static IntPtr _hookID = IntPtr.Zero;

    private static bool _isRecording;
    private static bool _isProcessing; 
    private static RecordingMode _currentMode = RecordingMode.None;
    private static MainWindow _instance = null!;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string _currentFileName = "";

    private readonly DispatcherTimer _animationTimer;
    private readonly Random _rnd = new();

    private enum SoundType { Start, Stop, Success, Error }

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private int _selectedDeviceNumber;

    public MainWindow()
    {
        InitializeComponent();
        _instance = this;

        if (!LoadConfig())
        {
            Application.Current.Shutdown();
            return;
        }

        _hookID = SetHook(_proc);
        Closing += (_, _) => UnhookWindowsHookEx(_hookID);

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimer_Tick!;
    }

    private bool _isSoundEnabled = true;

    private void PlayUiSound(SoundType type)
    {
        if (!_isSoundEnabled)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                // Генерируем сигнал программно, чтобы не тащить wav-файлы
                var signal = new SignalGenerator()
                {
                    Gain = 0.15, // Громкость (0.0 - 1.0) - делаем тихим
                    Type = SignalGeneratorType.Sin
                };

                ISampleProvider provider = signal;
                int durationMs = 150;

                switch (type)
                {
                    case SoundType.Start:
                        signal.Frequency = 400; // Низкий тон
                        signal.FrequencyEnd = 600; // Плавный подъем
                        signal.Type = SignalGeneratorType.Sweep; // Скольжение частоты
                        durationMs = 200;
                        break;
                    case SoundType.Stop:
                        signal.Frequency = 600;
                        signal.FrequencyEnd = 300; // Плавный спуск
                        signal.Type = SignalGeneratorType.Sweep;
                        durationMs = 200;
                        break;
                    case SoundType.Success:
                        signal.Frequency = 1000; // Высокий "дзынь"
                        durationMs = 150;
                        break;
                    case SoundType.Error:
                        signal.Frequency = 150;
                        signal.Type = SignalGeneratorType.Square; // Резкий звук
                        durationMs = 300;
                        break;
                }

                // Обрезаем звук по длительности
                var offset = provider.Take(TimeSpan.FromMilliseconds(durationMs));

                using var waveOut = new WaveOutEvent();
                waveOut.Init(offset);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing) Thread.Sleep(10);
            }
            catch { /* Игнорируем ошибки звука, чтобы не ломать основную логику */ }
        });
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (var currentProcess = Process.GetCurrentProcess())
        using (var currentProcessMainModule = currentProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentProcessMainModule.ModuleName), 0);
        }
    }

    private bool LoadConfig()
    {
        try
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDirectory = Path.Combine(appDataPath, ".WhisperInk");

            if (!Directory.Exists(configDirectory)) Directory.CreateDirectory(configDirectory);

            string configPath = Path.Combine(configDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var defaultConfig = new AppConfig();
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, options));
                _isSoundEnabled = defaultConfig.IsSoundEnabled; // Initialize sound setting

                MessageBox.Show($"Конфиг создан: {configPath}", "Настройка", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string jsonContent = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(jsonContent);
            _isSoundEnabled = config?.IsSoundEnabled ?? true;
            _systemPrompt = string.IsNullOrWhiteSpace(config?.SystemPrompt) ? new AppConfig().SystemPrompt : config.SystemPrompt;

            if (config == null || String.IsNullOrWhiteSpace(config.MistralApiKey) || config.MistralApiKey.Contains("ВСТАВЬТЕ"))
            {
                _isSoundEnabled = new AppConfig().IsSoundEnabled; // Fallback to default sound setting
                MessageBox.Show("Укажите API ключ в config.json", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                Process.Start("explorer.exe", configDirectory);
                return false;
            }

            _mistralApiKey = config.MistralApiKey;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ошибка config.json: " + ex.Message);
            return false;
        }
    }

    private void SaveConfig()
    {
        try
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDirectory = Path.Combine(appDataPath, ".WhisperInk");
            if (!Directory.Exists(configDirectory)) Directory.CreateDirectory(configDirectory);

            string configPath = Path.Combine(configDirectory, ConfigFileName);

            var configToSave = new AppConfig
            {
                MistralApiKey = _mistralApiKey,
                IsSoundEnabled = _isSoundEnabled,
                SystemPrompt = _systemPrompt
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(configPath, JsonSerializer.Serialize(configToSave, options));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ошибка сохранения конфига: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var desktop = SystemParameters.WorkArea;
        Left = desktop.Left + (desktop.Width - Width) / 2;
        Top = desktop.Bottom - Height - 50;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void MainContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        MainContextMenu.Items.Clear();

        var historyItem = new MenuItem { Header = "📜 History..." };
        historyItem.Click += (_, _) =>
        {
            // Открываем окно истории
            new HistoryWindow().Show();
        };
        MainContextMenu.Items.Add(historyItem);

        // Добавляем пункт для редактирования промпта
        var editPromptItem = new MenuItem { Header = "⚙ System Prompt..." };
        editPromptItem.Click += (_, _) =>
        {
            var promptWindow = new PromptWindow(_systemPrompt);
            if (promptWindow.ShowDialog() == true) // Если нажали Save
            {
                _systemPrompt = promptWindow.PromptText;
                SaveConfig(); // Сразу сохраняем изменения в файл
            }
        };
        MainContextMenu.Items.Insert(1, editPromptItem);

        MainContextMenu.Items.Add(new Separator());

        MainContextMenu.Items.Add(new MenuItem { Header = "Microphones:", IsEnabled = false, FontWeight = FontWeights.Bold });

        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            string productName = caps.ProductName;
            const int maxLength = 30; // Установленный лимит символов для обрезки имени микрофона

            if (productName.Length > maxLength)
            {
                int parenthesisIndex = productName.IndexOf('(');
                if (parenthesisIndex != -1)
                {
                    productName = productName.Substring(0, parenthesisIndex).Trim();
                }
            }
            var item = new MenuItem { Header = productName, Tag = i, IsCheckable = true, IsChecked = i == _selectedDeviceNumber };
            item.Click += (s, _) => { if (s is MenuItem m && m.Tag is int id) _selectedDeviceNumber = id; };
            MainContextMenu.Items.Add(item);
        }

        MainContextMenu.Items.Add(new Separator());

        var soundToggleItem = new MenuItem { Header = "🔊 Sound On/Off", IsCheckable = true, IsChecked = _isSoundEnabled };
        soundToggleItem.Click += (s, _) =>
        {
            if (s is MenuItem m)
            {
                _isSoundEnabled = m.IsChecked;
                SaveConfig(); // Save the new sound setting
            }
        };
        MainContextMenu.Items.Add(soundToggleItem);

        MainContextMenu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => { UnhookWindowsHookEx(_hookID); Application.Current.Shutdown(); };
        MainContextMenu.Items.Add(exit);
    }

    // --- ХУК КЛАВИАТУРЫ ---
    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool ctrlDown = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            bool winDown = (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;
            bool altDown = (GetKeyState(VK_MENU) & 0x8000) != 0;

            if (!_isRecording && !_isProcessing)
            {
                if (ctrlDown && winDown) // Вставка
                {
                    _currentMode = RecordingMode.Inject;
                    _isRecording = true;
                    Application.Current.Dispatcher.Invoke(() => _instance.StartRecordingProcess());
                }
                else if (ctrlDown && altDown) // Анализ
                {
                    _currentMode = RecordingMode.AnalyzeContext;
                    _isRecording = true;
                    Application.Current.Dispatcher.Invoke(() => _instance.StartRecordingProcess());
                }
            }
            else
            {
                if (_currentMode == RecordingMode.Inject && (!ctrlDown || !winDown))
                {
                    _isRecording = false;
                    Application.Current.Dispatcher.Invoke(() => _instance.StopAndTranscribe());
                }
                else if (_currentMode == RecordingMode.AnalyzeContext && (!ctrlDown || !altDown))
                {
                    _isRecording = false;
                    Application.Current.Dispatcher.Invoke(() => _instance.StopAndTranscribe());
                }
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    // --- ЗАПИСЬ ---
    public void StartRecordingProcess()
    {
        try
        {
            PlayUiSound(SoundType.Start);

            if (_currentMode == RecordingMode.AnalyzeContext)
            {
                try { Clipboard.Clear(); } catch { }
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 255)); // Синий
            }
            else
            {
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100)); // Красный
            }

            lblStatus.Opacity = 0;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRecordings");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            _currentFileName = Path.Combine(folder, "temp_audio.wav");

            _waveIn = new WaveInEvent();
            if (_selectedDeviceNumber < WaveIn.DeviceCount) _waveIn.DeviceNumber = _selectedDeviceNumber;
            else _selectedDeviceNumber = 0;

            _waveIn.WaveFormat = new WaveFormat(16000, 1);
            _writer = new WaveFileWriter(_currentFileName, _waveIn.WaveFormat);
            _waveIn.DataAvailable += (_, a) => _writer.Write(a.Buffer, 0, a.BytesRecorded);
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            _isRecording = false;
            MessageBox.Show("Mic Error: " + ex.Message);
        }
    }

    private void AnimationTimer_Tick(object sender, EventArgs e)
    {
        AnimateBar(Bar1); AnimateBar(Bar2); AnimateBar(Bar3); AnimateBar(Bar4); AnimateBar(Bar5);
    }
    private void AnimateBar(Rectangle bar) => bar.Height = _rnd.Next(3, 15);

    // --- STOP AND PROCESSING LOGIC ---
    public async void StopAndTranscribe()
    {
        if (_isProcessing) return; // Additional protection against repeated logins
        _isProcessing = true;      // <-- Set the flag at the beginning.

        try
        {
            PlayUiSound(SoundType.Stop);

            // 1. Recording stopped
            _waveIn?.StopRecording();
            _waveIn?.Dispose(); _waveIn = null;
            _writer?.Close(); _writer?.Dispose(); _writer = null;

            _animationTimer.Stop();
            HistogramPanel.Visibility = Visibility.Collapsed;
            lblStatus.Text = "Think...";
            lblStatus.Opacity = 1;

            var mode = _currentMode;
            string contextText = "";

            // 2. If "AnalyzeContext" mode is on, copy the context.
            if (mode == RecordingMode.AnalyzeContext)
            {
                // Clear the buffer BEFORE attempting to copy.
                // This way, we'll know exactly when new text appears.
                try { Clipboard.Clear(); } catch { }

                SimulateCtrlC();

                // Polling loop: checking the buffer up to 10 times with a 100 ms interval (maximum 1 second)
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(100);

                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            contextText = Clipboard.GetText();
                            break; // Text successfully copied, exiting the wait loop.
                        }
                    }
                    catch
                    {
                        // Ignore errors (e.g., if the buffer is locked by another process)
                        // and just wait for the next iteration of the loop
                    }
                }
            }

            // 3. Voice transcription (User prompt)
            string? userPrompt = await TranscribeAudioAsync(_currentFileName);

            if (String.IsNullOrEmpty(userPrompt))
            {
                lblStatus.Text = "Empty";
                await Task.Delay(1000);
                ResetAnimation();
                _currentMode = RecordingMode.None;
                return;
            }

            string resultTextToPaste = "";

            // 4. Mode Logic
            if (mode == RecordingMode.Inject)
            {
                // Just insert what was dictated.
                resultTextToPaste = userPrompt;
            }
            else if (mode == RecordingMode.AnalyzeContext)
            {
                // Send to Chat Completions (Context + Prompt)
                lblStatus.Text = "AI..."; // AI Operation Indication
                string? aiResponse = await SendChatCompletionAsync(contextText, userPrompt);

                if (!String.IsNullOrEmpty(aiResponse))
                {
                    resultTextToPaste = aiResponse;
                }
                else
                {
                    PlayUiSound(SoundType.Error);

                    lblStatus.Text = "AI Error";
                    await Task.Delay(1000);
                    ResetAnimation();
                    _currentMode = RecordingMode.None;
                    return;
                }
            }

            // 5. Result insertion
            lblStatus.Text = "✓";
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 255, 100));


            if (!string.IsNullOrWhiteSpace(resultTextToPaste))
            {
                HistoryService.Add(resultTextToPaste);
            }

            PlayUiSound(SoundType.Success);
            PasteTextToActiveWindow(resultTextToPaste);

            await Task.Delay(1500);
            ResetAnimation();
        }
        catch (Exception ex)
        {
            PlayUiSound(SoundType.Error);

            Debug.WriteLine(ex.Message);
            ResetAnimation();
        }
        finally
        {
            _currentMode = RecordingMode.None;
            _isProcessing = false; // <-- Reset the flag at the very end, in the `finally` block.
        }
    }

    private void ResetAnimation()
    {
        _animationTimer.Stop();
        Bar1.Height = 3; Bar2.Height = 3; Bar3.Height = 3; Bar4.Height = 3; Bar5.Height = 3;
        HistogramPanel.Visibility = Visibility.Visible;
        lblStatus.Opacity = 0;
        MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
    }

    // --- API MISTRAL: AUDIO ---
    private async Task<string?> TranscribeAudioAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AudioApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mistralApiKey);

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(filePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

            content.Add(fileContent, "file", "audio.wav");
            content.Add(new StringContent(AudioModel), "model");

            request.Content = content;
            var response = await _httpClient.SendAsync(request);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("text", out var textElement)) return textElement.GetString();
        }
        catch (Exception ex) { Debug.WriteLine($"Network error: {ex.Message}"); }
        return null;
    }

    // --- API MISTRAL: CHAT COMPLETIONS ---
    private async Task<string?> SendChatCompletionAsync(string context, string instruction)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mistralApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Creating user content
            string finalContent;
            if (!string.IsNullOrWhiteSpace(context))
            {
                finalContent = $"Context:\n'''\n{context}\n'''\n\nTask: {instruction}";
            }
            else
            {
                finalContent = instruction;
            }

            var payload = new
            {
                model = ChatModel,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = _systemPrompt
                    },
                    new
                    {
                        role = "user",
                        content = finalContent
                    }
                },
                temperature = 0.3,
            };

            string jsonBody = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"Chat API Error: {response.StatusCode} - {responseString}");
                return null;
            }

            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString()?.Trim().Trim('"'); // Trim removes extra spaces/line breaks.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chat API Network error: {ex.Message}");
        }
        return null;
    }

    // --- UTILS ---
    private void PasteTextToActiveWindow(string text)
    {
        var staThread = new Thread(() => { try { Clipboard.SetText(text); } catch { } });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        SimulateCtrlV();
    }

    private static void SimulateCtrlV()
    {
        // 1. Force-releasing modifiers to prevent "sticking"
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);    // Alt
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);    // Win (left)
        keybd_event(VK_RWIN, 0, KEYEVENTF_KEYUP, 0);    // Win (right)
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0); // Ctrl

        Thread.Sleep(50); // Give the OS a micro-pause to update the keyboard state.

        // 2. Sending a pure Ctrl + V press.
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_V, 0, 0, 0);

        Thread.Sleep(20); // Micro-pause between pressing and releasing

        // 3. Release the keys
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void SimulateCtrlC()
    {
        // 1. Force-release the modifiers.
        // This ensures that the keys physically pressed by the user 
        // won’t interfere with our virtual combination.
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);    // Alt
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);    // Win (left)
        keybd_event(VK_RWIN, 0, KEYEVENTF_KEYUP, 0);    // Win (right)
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0); // Ctrl

        Thread.Sleep(50); // Giving the OS a micro-pause to update the keyboard state.

        // 2. Sending a clean Ctrl + C press.
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_C, 0, 0, 0);

        Thread.Sleep(20); // Time between press and release

        keybd_event(VK_C, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    // --- NATIVE METHODS ---
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
}