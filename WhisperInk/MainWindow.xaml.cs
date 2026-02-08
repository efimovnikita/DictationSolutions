using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;
using Path = System.IO.Path;

namespace WhisperInk;

// Класс для структуры JSON
public class AppConfig
{
    public string MistralApiKey { get; set; } = "ВСТАВЬТЕ_ВАШ_КЛЮЧ_СЮДА";
}

public partial class MainWindow : Window
{
    // ... (API ключи и константы хука остаются без изменений) ...
    private string _mistralApiKey = ""; // Теперь не const, а переменная
    private const string ConfigFileName = "config.json";
    private const string ApiUrl = "https://api.mistral.ai/v1/audio/transcriptions";
    private const string ModelName = "voxtral-mini-latest";

    private const int WH_KEYBOARD_LL = 13;
    private const int VK_CONTROL = 0x11;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static readonly LowLevelKeyboardProc _proc = HookCallback;
    private static IntPtr _hookID = IntPtr.Zero;
    private static bool _isRecording;
    private static MainWindow _instance = null!;

    private WaveInEvent _waveIn;
    private WaveFileWriter _writer;
    private string _currentFileName;

    private readonly DispatcherTimer _animationTimer;
    private readonly Random _rnd = new();

    // Создаем один экземпляр на всё время работы программы
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60) // Увеличим таймаут для аудио
    };

    // НОВОЕ: ID выбранного микрофона (по умолчанию 0 - системный дефолт)
    private int _selectedDeviceNumber;

    public MainWindow()
    {
        InitializeComponent();
        _instance = this;

        // 1. Загрузка конфига ПЕРЕД установкой хуков
        if (!LoadConfig())
        {
            // Если конфиг только что создан или неверен, закрываемся
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

    // --- ЛОГИКА ЗАГРУЗКИ КОНФИГА (ОБНОВЛЕННАЯ ВЕРСИЯ) ---
    private bool LoadConfig()
    {
        try
        {
            // 1. Получаем путь к AppData\Roaming
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // 2. Создаем путь к нашей папке .VoiceHUD (как просили, с точкой)
            string configDirectory = Path.Combine(appDataPath, ".VoiceHUD");

            // 3. Убеждаемся, что папка существует. Если нет - создаем.
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            // 4. Полный путь к файлу
            string configPath = Path.Combine(configDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                // Создаем дефолтный конфиг
                var defaultConfig = new AppConfig();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(defaultConfig, options);
                File.WriteAllText(configPath, jsonString);

                MessageBox.Show(
                    $"Файл конфигурации создан в папке AppData:\n{configPath}\n\nПожалуйста, откройте его и вставьте ваш API ключ Mistral.",
                    "Настройка", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            // Читаем конфиг
            string jsonContent = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(jsonContent);

            if (config == null || String.IsNullOrWhiteSpace(config.MistralApiKey) ||
                config.MistralApiKey.Contains("ВСТАВЬТЕ_ВАШ_КЛЮЧ"))
            {
                MessageBox.Show("Пожалуйста, укажите корректный API ключ в файле config.json",
                    "Ошибка ключа", MessageBoxButton.OK, MessageBoxImage.Warning);

                // Открываем папку с конфигом для удобства пользователя
                Process.Start("explorer.exe", configDirectory);
                return false;
            }

            _mistralApiKey = config.MistralApiKey;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ошибка чтения config.json: " + ex.Message);
            return false;
        }
    }

    // Позиционирование окна
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var desktopWorkingArea = SystemParameters.WorkArea;
        Left = desktopWorkingArea.Left + (desktopWorkingArea.Width - Width) / 2;
        Top = desktopWorkingArea.Bottom - Height - 50;
    }

    // Перетаскивание
    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    // --- ЛОГИКА МЕНЮ (НОВОЕ) ---
    private void MainContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        MainContextMenu.Items.Clear();

        // 1. Заголовок
        var header = new MenuItem { Header = "Выберите микрофон:", IsEnabled = false, FontWeight = FontWeights.Bold };
        MainContextMenu.Items.Add(header);

        // 2. Список устройств из NAudio
        int deviceCount = WaveIn.DeviceCount;
        for (int i = 0; i < deviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            var item = new MenuItem
            {
                Header = caps.ProductName, // Имя микрофона
                Tag = i, // Сохраняем ID устройства
                IsCheckable = true,
                IsChecked = i == _selectedDeviceNumber // Ставим галочку, если это текущий
            };
            item.Click += MicItem_Click;
            MainContextMenu.Items.Add(item);
        }

        // 3. Разделитель
        MainContextMenu.Items.Add(new Separator());

        // 4. Кнопка выхода
        var exitItem = new MenuItem { Header = "Закрыть приложение" };
        exitItem.Click += (_, _) =>
        {
            UnhookWindowsHookEx(_hookID);
            Application.Current.Shutdown();
        };
        MainContextMenu.Items.Add(exitItem);
    }

    private void MicItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is int deviceId)
        {
            _selectedDeviceNumber = deviceId;
            // При следующем открытии меню галочка перерисуется сама
        }
    }

    // ... (Хуки клавиатуры HookCallback остаются без изменений) ...
    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (var currentProcess = Process.GetCurrentProcess())
        using (var currentProcessMainModule = currentProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentProcessMainModule.ModuleName), 0);
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool ctrlDown = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            bool winDown = (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

            if (ctrlDown && winDown)
            {
                if (!_isRecording)
                {
                    _isRecording = true;
                    Application.Current.Dispatcher.Invoke(() => _instance.StartRecordingProcess());
                }
            }
            else
            {
                if (_isRecording)
                {
                    _isRecording = false;
                    Application.Current.Dispatcher.Invoke(() => _instance.StopAndTranscribe());
                }
            }
        }

        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    // --- ЗАПИСЬ (ОБНОВЛЕНО) ---
    public void StartRecordingProcess()
    {
        try
        {
            // UI
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            lblStatus.Opacity = 0;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            // Файл
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MyRecordings");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _currentFileName = Path.Combine(folder, "temp_audio.wav");

            // Аудио
            _waveIn = new WaveInEvent();

            // --- ИСПОЛЬЗУЕМ ВЫБРАННЫЙ МИКРОФОН ---
            if (_selectedDeviceNumber < WaveIn.DeviceCount)
            {
                _waveIn.DeviceNumber = _selectedDeviceNumber;
            }
            else
            {
                // Если вдруг выбранный микрофон отключили, сбрасываем на 0
                _selectedDeviceNumber = 0;
                _waveIn.DeviceNumber = 0;
            }
            // -------------------------------------

            _waveIn.WaveFormat = new WaveFormat(16000, 1);
            _writer = new WaveFileWriter(_currentFileName, _waveIn.WaveFormat);
            _waveIn.DataAvailable += (_, a) => _writer.Write(a.Buffer, 0, a.BytesRecorded);
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            _isRecording = false;
            MessageBox.Show("Error mic: " + ex.Message);
        }
    }

    // ... (StopAndTranscribe, AnimationTimer_Tick, AnimateBar, ResetAnimation и WinAPI остаются без изменений) ...

    // Для полноты картины приведу нужные методы, чтобы код копировался целиком рабочим:
    private void AnimationTimer_Tick(object sender, EventArgs e)
    {
        AnimateBar(Bar1);
        AnimateBar(Bar2);
        AnimateBar(Bar3);
        AnimateBar(Bar4);
        AnimateBar(Bar5);
    }

    private void AnimateBar(Rectangle bar)
    {
        double targetHeight = _rnd.Next(3, 15);
        bar.Height = targetHeight;
    }

    public async void StopAndTranscribe()
    {
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
            _writer?.Close();
            _writer?.Dispose();
            _writer = null;

            _animationTimer.Stop();
            HistogramPanel.Visibility = Visibility.Collapsed;
            lblStatus.Text = "Thinking...";
            lblStatus.Opacity = 1;
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0));

            string text = await TranscribeAudioAsync(_currentFileName);
            if (!String.IsNullOrEmpty(text))
            {
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 255, 100));
                lblStatus.Text = "✓";
                PasteTextToActiveWindow(text);
            }
            else
            {
                lblStatus.Text = "Empty";
            }

            await Task.Delay(1500);
            ResetAnimation();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            ResetAnimation();
        }
    }

    private void ResetAnimation()
    {
        _animationTimer.Stop();
        Bar1.Height = 3;
        Bar2.Height = 3;
        Bar3.Height = 3;
        Bar4.Height = 3;
        Bar5.Height = 3;
        HistogramPanel.Visibility = Visibility.Visible;
        lblStatus.Opacity = 0;
        MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
    }

    private async Task<string?> TranscribeAudioAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        // Мы НЕ оборачиваем _httpClient в using, так как он живет вечно
        try
        {
            // Заголовки авторизации лучше устанавливать для каждого запроса отдельно, 
            // если ключ может измениться, или один раз в конструкторе, если он статичен.
            // Используем HttpRequestMessage для чистоты:
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mistralApiKey);

            using var content = new MultipartFormDataContent();
            // Используем Stream вместо ReadAllBytes, это эффективнее для памяти
            using var fileStream = File.OpenRead(filePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

            content.Add(fileContent, "file", "audio.wav");
            content.Add(new StringContent(ModelName), "model");

            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("text", out var textElement))
            {
                return textElement.GetString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network error: {ex.Message}");
        }

        return null;
    }

    private void PasteTextToActiveWindow(string text)
    {
        var staThread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // ignored
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        SimulateCtrlV();
    }

    private static void SimulateCtrlV()
    {
        Thread.Sleep(100);
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_V, 0, 0, 0);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

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
}