using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using System.Net.Http.Headers;
using System.Text.Json;
using Color = Android.Graphics.Color;
using Path = System.IO.Path;
using View = Android.Views.View;
using Clipboard = Android.Content.ClipboardManager;
using Microsoft.Maui.Storage;
using System.Net;

namespace WhisperInk.Maui
{
    // Добавляем ", View.IOnTouchListener" к объявлению класса
    [Service(ForegroundServiceType = ForegroundService.TypeMicrophone)]
    public class FloatingButtonService : Service, View.IOnTouchListener
    {
        public const string TAG = "WhisperInkDebug";

        private IWindowManager? _windowManager;
        private ImageView? _floatingButton;

        private AudioRecord? _audioRecord;
        private bool _isRecording;
        private Task? _recordingTask;
        private MemoryStream? _recordingStream; // <-- Используем MemoryStream вместо пути к файлу

        private int _initialX;
        private int _initialY;
        private float _initialTouchX;
        private float _initialTouchY;

        private static HttpClient? _sharedClient;
        private static string _lastUsedProxyConfig = "N/A"; // Специальное значение для инициализации
        private static readonly object _clientLock = new object(); // Для потокобезопасности

        private WindowManagerLayoutParams? _layoutParams; // Вынесли, чтобы менять положение

        public override IBinder? OnBind(Intent? intent) => null;
        
        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null || _layoutParams == null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    Log.Debug(TAG, ">>> Кнопка НАЖАТА. Начинаем запись...");
                    // Сохраняем начальные позиции
                    _initialX = _layoutParams.X;
                    _initialY = _layoutParams.Y;
                    _initialTouchX = e.RawX;
                    _initialTouchY = e.RawY;

                    StartRecording();
                    return true;

                case MotionEventActions.Move:
                    // Рассчитываем смещение от начальной точки касания
                    var dX = e.RawX - _initialTouchX;
                    var dY = e.RawY - _initialTouchY;

                    // Порог, чтобы случайное дрожание пальца не считалось перетаскиванием
                    const float DragThreshold = 10.0f;
                    if (Math.Abs(dX) > DragThreshold || Math.Abs(dY) > DragThreshold)
                    {
                        // Обновляем параметры положения окна
                        _layoutParams.X = _initialX + (int)dX;
                        _layoutParams.Y = _initialY + (int)dY;
                        _windowManager?.UpdateViewLayout(_floatingButton, _layoutParams);
                    }
                    return true;

                case MotionEventActions.Up:
                    Log.Debug(TAG, ">>> Кнопка ОТПУЩЕНА.");

                    // Запускаем асинхронную задачу в фоне (fire-and-forget), 
                    // не блокируя возврат true и работу UI
                    _ = Task.Run(async () => 
                    {
                        try 
                        {
                            await StopAndProcessRecordingAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(TAG, $"Критическая ошибка при остановке/обработке: {ex.Message}");
                        }
                    });
                    return true; // UI мгновенно "отпускает" кнопку!

                case MotionEventActions.Cancel:
                    Log.Debug(TAG, ">>> Касание ОТМЕНЕНО системой.");
                    _ = Task.Run(async () => 
                    {
                        await StopRecordingAndDiscardAsync();
                    });
                    return true;            
            }

            return false;
        }

        private async Task StopRecordingAndDiscardAsync() // Сделали метод асинхронным
        {
            Log.Debug(TAG, "Запись отменена (например, свернули приложение). Удаляем данные.");
    
            if (!_isRecording) return;

            // 1. Даем сигнал фоновому потоку остановиться
            _isRecording = false;

            // 2. ОБЯЗАТЕЛЬНО ждем, пока фоновый цикл while() корректно завершит свой последний круг
            if (_recordingTask != null)
            {
                await _recordingTask; 
            }

            // 3. Теперь, когда никто не читает и не пишет, безопасно освобождаем ресурсы
            try
            {
                _audioRecord?.Stop();
                _audioRecord?.Release();
                _audioRecord = null;
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Ошибка при освобождении микрофона: {ex.Message}");
            }

            // 4. Уничтожаем записанные данные
            try
            {
                _recordingStream?.Close();
                _recordingStream?.Dispose(); // Освобождаем память
                _recordingStream = null;
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Ошибка при закрытии потока: {ex.Message}");
            }
        }
        private void StartRecording()
        {
            if (_isRecording) return;
    
            // Ставим флаг СРАЗУ в UI-потоке, чтобы предотвратить множественные 
            // одновременные нажатия, пока фоновый поток запускается
            _isRecording = true;

            LogService.Log("Начало записи...");

            // Отправляем всю работу с железом в фон
            _recordingTask = Task.Run(() =>
            {
                try
                {
                    // Инициализируем поток в памяти
                    _recordingStream = new MemoryStream();

                    var audioSource = AudioSource.Mic;
                    var sampleRate = 16000;
                    var channelConfig = ChannelIn.Mono;
                    var audioFormat = Encoding.Pcm16bit;
                    var bufferSize = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, audioFormat);

                    // Теперь инициализация железа не тормозит интерфейс
                    _audioRecord = new AudioRecord(audioSource, sampleRate, channelConfig, audioFormat, bufferSize);
                    _audioRecord.StartRecording();

                    var buffer = new byte[bufferSize];
            
                    // Запускаем цикл чтения
                    while (_isRecording)
                    {
                        int read = _audioRecord.Read(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            // Пишем напрямую в MemoryStream
                            _recordingStream.Write(buffer, 0, read);
                        }
                    }
                }
                catch (Exception ex) 
                { 
                    Log.Error(TAG, $"!!! ОШИБКА StartRecording: {ex.Message}");
                    // В случае ошибки сбрасываем флаг, чтобы можно было попробовать снова
                    _isRecording = false; 
                }
            });
        }

        private async Task StopAndProcessRecordingAsync()
        {
            if (!_isRecording || _recordingStream == null) return;

            // 1. Останавливаем запись
            _isRecording = false;

            if (_recordingTask != null) 
            {
                await _recordingTask; // Гарантированно дожидаемся завершения потока
            }

            _audioRecord?.Stop();
            _audioRecord?.Release();
            _audioRecord = null;

            // 2. Получаем сырые PCM данные из MemoryStream
            byte[] pcmData = _recordingStream.ToArray();
            _recordingStream.Close(); // Освобождаем память
            
            if (pcmData.Length == 0)
            {
                Log.Warn(TAG, "Записан пустой аудиофайл, отправка отменена.");
                return;
            }

            // 3. Создаем WAV-файл в памяти
            byte[] wavData = await Task.Run(() => WavHelper.CreateWavFile(pcmData, 16000, 1, 16));
            Log.Debug(TAG, $"WAV-файл создан в памяти, размер: {wavData.Length} байт.");

            // 4. Отправляем в API
            string? transcribedText = await TranscribeAudioAsync(wavData);

            // 5. Обрабатываем результат (Важно: вызов UI-методов должен быть в главном потоке)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!string.IsNullOrEmpty(transcribedText))
                {
                    Log.Debug(TAG, $"Получен текст: {transcribedText}");
                    CopyToClipboardAndNotify(transcribedText);
                }
                else
                {
                    Log.Error(TAG, "Не удалось получить текст от API (возможно, пустой ответ).");
                    LogService.Log("Ошибка распознавания");
                }
            });
        }

        private async Task<string?> TranscribeAudioAsync(byte[] wavFileBytes)
        {
            // Получаем ключ из настроек приложения
            var mistralApiKey = Preferences.Get("MistralApiKey", string.Empty);
            var currentProxyConfig = Preferences.Get("ProxyConfig", string.Empty);

            if (string.IsNullOrEmpty(mistralApiKey))
            {
                Log.Error(TAG, "API ключ не установлен! Пожалуйста, задайте его в приложении.");
                LogService.Log("API key not set");
                return null;
            }

            try
            {
                // 1. Получаем правильный клиент (старый или новый, если настройки поменялись)
                var client = GetSmartHttpClient(currentProxyConfig);

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/audio/transcriptions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mistralApiKey);

                using var content = new MultipartFormDataContent();
        
                // ИСПРАВЛЕНИЕ 2: Обернули в using, чтобы сразу освобождать память из-под тяжелого массива байтов
                using var audioContent = new ByteArrayContent(wavFileBytes);
                audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
                content.Add(audioContent, "file", "audio.wav");

                // ИСПРАВЛЕНИЕ 3: Обернули в using текстовый контент
                using var modelContent = new StringContent("voxtral-mini-latest");
                content.Add(modelContent, "model");

                request.Content = content;
        
                // ИСПРАВЛЕНИЕ 4: Обернули ответ в using
                using var response = await client.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LogService.Log($"Ошибка API: {(int)response.StatusCode} - {responseString}");
                    Log.Error(TAG, $"Ошибка API: {(int)response.StatusCode} - {responseString}");
                    return null;
                }

                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            }
            catch (Exception ex)
            {
                LogService.Log($"Сетевая ошибка: {ex.Message}");
                Log.Error(TAG, $"Сетевая ошибка: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogService.Log($"Внутреннее исключение: {ex.InnerException.Message}");
                    Log.Error(TAG, $"Внутреннее исключение: {ex.InnerException.Message}");
                }
                return null;
            }
        }

        private HttpClient GetSmartHttpClient(string currentProxySettings)
        {
            lock (_clientLock)
            {
                // Если клиент уже есть И настройки прокси НЕ изменились — возвращаем старый
                if (_sharedClient != null && _lastUsedProxyConfig == currentProxySettings)
                {
                    return _sharedClient;
                }

                // Если настройки изменились или клиента нет — создаем новый
                Log.Debug(TAG, "Настройки прокси изменились (или первый запуск). Пересоздаем HttpClient.");

                // Если был старый клиент — можно его корректно закрыть (хотя в статике это не обязательно, GC заберет)
                _sharedClient?.Dispose();

                _sharedClient = CreateConfiguredHttpClient(currentProxySettings);
                _sharedClient.Timeout = TimeSpan.FromSeconds(60);

                // Запоминаем текущую конфигурацию
                _lastUsedProxyConfig = currentProxySettings;

                return _sharedClient;
            }
        }

        private HttpClient CreateConfiguredHttpClient(string proxySettings)
        {
            if (string.IsNullOrWhiteSpace(proxySettings))
            {
                return new HttpClient(); // Прямое подключение
            }

            try
            {
                var atIndex = proxySettings.LastIndexOf('@');

                if (atIndex == -1)
                {
                    // Прокси без пароля
                    var simpleHandler = new SocketsHttpHandler
                    {
                        Proxy = new WebProxy(proxySettings),
                        UseProxy = true
                    };
                    return new HttpClient(simpleHandler);
                }

                var credentialsPart = proxySettings.Substring(0, atIndex);
                var addressPart = proxySettings.Substring(atIndex + 1);
                var creds = credentialsPart.Split(':');

                // Используем SocketsHttpHandler - он надежнее в MAUI
                var handler = new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = new WebProxy($"http://{addressPart}")
                    {
                        Credentials = new NetworkCredential(creds[0], creds[1])
                    },
                    // ВАЖНО: Заставляем отправлять логин и пароль сразу с первым запросом!
                    PreAuthenticate = true
                };

                LogService.Log("Используется прокси.");

                return new HttpClient(handler);
            }
            catch (Exception ex)
            {
                LogService.Log($"Ошибка прокси: {ex.Message}. Возврат к прямому подключению.");
                Log.Error(TAG, $"Ошибка прокси: {ex.Message}. Возврат к прямому подключению.");
                return new HttpClient();
            }
        }

        private void CopyToClipboardAndNotify(string text)
        {
            // Оборачиваем ВСЁ взаимодействие с UI-контекстом в главный поток
            if (MainThread.IsMainThread)
            {
                ExecuteCopy(text);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => ExecuteCopy(text));
            }

            // Вспомогательный локальный метод, чтобы не дублировать код
            void ExecuteCopy(string textToCopy)
            {
                try
                {
                    // Используем нативный API Android
                    var clipboardManager = (Clipboard)GetSystemService(ClipboardService);
                    var clipData = ClipData.NewPlainText("WhisperInk Result", textToCopy);
            
                    if (clipboardManager != null)
                    {
                        clipboardManager.PrimaryClip = clipData;
                        // Toast тоже вызываем отсюда, он уже умеет обрабатывать потоки
                        LogService.Log("Текст скопирован!");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(TAG, $"Ошибка копирования в буфер: {ex.Message}");
                    LogService.Log("Ошибка при копировании");
                }
            }
        }

        private void CreateFloatingButton()
        {
            try
            {
                Log.Debug(TAG, ">>> Начинаем создание кнопки...");
                _windowManager = GetSystemService(WindowService).JavaCast<IWindowManager>();

                _floatingButton = new ImageView(this);
                // ↓↓↓ ПОДПИСЫВАЕМСЯ НА СОБЫТИЯ КАСАНИЯ ↓↓↓
                _floatingButton.SetOnTouchListener(this);

                try 
                {
                    _floatingButton.SetImageResource(Resource.Drawable.mic_icon);
                    _floatingButton.SetBackgroundColor(Color.Transparent); 
                }
                catch
                {
                    Log.Warn(TAG, ">>> Ресурс 'mic_icon' не найден. Используется серый фон.");
                    _floatingButton.SetBackgroundColor(Color.ParseColor("#888888")); // Сделаем непрозрачным серым
                }

                _layoutParams = new WindowManagerLayoutParams(
                    150, 150,
                    WindowManagerTypes.ApplicationOverlay,
                    WindowManagerFlags.NotFocusable,
                    Format.Translucent
                );

                _layoutParams.Gravity = GravityFlags.Left | GravityFlags.Top;
                _layoutParams.X = 100;
                _layoutParams.Y = 200;

                _windowManager.AddView(_floatingButton, _layoutParams);
                Log.Debug(TAG, ">>> КНОПКА УСПЕШНО ДОБАВЛЕНА НА ЭКРАН!");
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"!!! ОШИБКА в CreateFloatingButton: {ex.Message}");
            }
        }

        // Остальные методы (OnStartCommand, CreateNotificationChannel, OnDestroy) остаются без изменений.
        // Просто скопируйте их из вашего предыдущего рабочего файла.
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            Log.Debug(TAG, ">>> FloatingButtonService: OnStartCommand вызван.");

            CreateNotificationChannel();
            var notification = new Notification.Builder(this, "WhisperInkServiceChannel")
                .SetContentTitle("WhisperInk Active")
                .SetContentText("Floating button is available.")
                .SetSmallIcon(Resource.Mipmap.appicon)
                .Build();

            StartForeground(101, notification, ForegroundService.TypeMicrophone);

            if (_floatingButton == null)
            {
                CreateFloatingButton();
            }

            return StartCommandResult.Sticky;
        }
        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            var channelName = "WhisperInk Service";
            var channel = new NotificationChannel("WhisperInkServiceChannel", channelName, NotificationImportance.Low);
            var manager = (NotificationManager)GetSystemService(NotificationService);
            manager.CreateNotificationChannel(channel);
            Log.Debug(TAG, ">>> Канал уведомлений создан.");
        }
        public override void OnDestroy()
        {
            base.OnDestroy();
            Log.Debug(TAG, ">>> OnDestroy вызван. Удаляем кнопку.");
            if (_floatingButton != null && _windowManager != null)
            {
                _windowManager.RemoveView(_floatingButton);
            }
        }
    }
}