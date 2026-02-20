using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Color = Android.Graphics.Color;
using View = Android.Views.View;

namespace WhisperInk.Maui
{
    [Service(ForegroundServiceType = ForegroundService.TypeMicrophone)]
    public class FloatingButtonService : Service, View.IOnTouchListener
    {
        public const string TAG = "WhisperInkDebug";

        private IWindowManager? _windowManager;

        // РАЗДЕЛЯЕМ UI НА ДВА НЕЗАВИСИМЫХ КОМПОНЕНТА
        private FrameLayout? _rippleContainer; // Невидимый для кликов контейнер для волн (400x400)
        private ImageView? _floatingButton;    // Сама кликабельная кнопка (150x150)

        private View? _rippleView;
        private AnimatorSet? _rippleAnimator;

        private AudioRecord? _audioRecord;
        private bool _isRecording;
        private Task? _recordingTask;
        private MemoryStream? _recordingStream;

        private int _initialX;
        private int _initialY;
        private float _initialTouchX;
        private float _initialTouchY;

        private static HttpClient? _sharedClient;
        private static string _lastUsedProxyConfig = "N/A";
        private static readonly object _clientLock = new object();

        private const string ChatApiUrl = "https://api.mistral.ai/v1/chat/completions"; //
        private const string ChatModel = "mistral-medium-latest"; // Или mistral-small-latest, если требуется

        // РАЗДЕЛЬНЫЕ ПАРАМЕТРЫ ОКОН
        private WindowManagerLayoutParams? _buttonLayoutParams;
        private WindowManagerLayoutParams? _rippleLayoutParams;

        // Константы размеров, чтобы легко было считать смещение
        private const int BUTTON_SIZE = 150;
        private const int RIPPLE_SIZE = 400;

        private async Task<string?> SendChatCompletionAsync(string context, string instruction)
        {
            try
            {
                // Достаем настройки из Preferences MAUI
                string apiKey = Preferences.Get(MainPage.ApiKeyPreferenceKey, string.Empty);
                string systemPrompt = Preferences.Get(MainPage.SystemPromptKey, "You are a precise execution engine. Output ONLY the direct result of the task.");

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Log.Warn("MistralApi", "API Key is missing!");
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, ChatApiUrl); //
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey); //
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); //

                // Формируем финальный текст для отправки
                string finalContent;
                if (!string.IsNullOrWhiteSpace(context))
                {
                    finalContent = $"Context:\n'''\n{context}\n'''\n\nTask: {instruction}"; //
                }
                else
                {
                    finalContent = instruction;
                }

                // Используем анонимные объекты, как в Windows-версии
                var payload = new
                {
                    model = ChatModel,
                    messages = new object[]
                    {
                new
                {
                    role = "system",
                    content = systemPrompt
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
                request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                var currentProxyConfig = Preferences.Get("ProxyConfig", string.Empty);

                var client = GetSmartHttpClient(currentProxyConfig);

                var response = await client.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn("MistralApi", $"Chat API Error: {response.StatusCode} - {responseString}");
                    return null;
                }

                // Парсинг ответа
                using var doc = JsonDocument.Parse(responseString);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out var message) && 
                        message.TryGetProperty("content", out var content))
                    {
                        return content.GetString()?.Trim().Trim('"');
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("MistralApi", $"Chat API Network error: {ex.Message}");
            }
            return null;
        }

        // --- НОВЫЙ МЕТОД: Безопасное чтение буфера обмена ---
        private string GetClipboardTextSafely()
        {
            try
            {
                // Явно указываем глобальный контекст MAUI и полные пути к нативным классам Android
                var clipboard = Android.App.Application.Context.GetSystemService(Android.Content.Context.ClipboardService) as Android.Content.ClipboardManager;

                if (clipboard != null && clipboard.HasPrimaryClip)
                {
                    var clip = clipboard.PrimaryClip;
                    if (clip != null && clip.ItemCount > 0)
                    {
                        var text = clip.GetItemAt(0)?.Text;
                        return text ?? string.Empty;
                    }
                }
                else
                {
                    Log.Warn(TAG, "Буфер обмена пуст или сервис недоступен.");
                }
            }
            catch (Java.Lang.SecurityException secEx)
            {
                // Ловим SecurityException, если Android всё-таки заблокировал доступ
                Log.Warn(TAG, $"SecurityException при доступе к буферу обмена: {secEx.Message}");
            }
            catch (Exception ex)
            {
                Log.Warn(TAG, $"Ошибка чтения буфера обмена: {ex.Message}");
            }

            return string.Empty; // Если ничего нет или ошибка, возвращаем пустую строку
        }
        // --- КОНЕЦ НОВОГО МЕТОДА ---

        // --- НОВЫЙ МЕТОД ОБНОВЛЕНИЯ ИКОНКИ ---
        private void UpdateButtonIcon(bool isCommandMode)
        {
            if (_floatingButton == null) return;

            try
            {
                if (isCommandMode)
                {
                    // Ожидается наличие robot_icon в ресурсах
                    _floatingButton.SetImageResource(Resource.Drawable.creative);
                }
                else
                {
                    _floatingButton.SetImageResource(Resource.Drawable.mic_icon);
                }
                _floatingButton.SetBackgroundColor(Color.Transparent);
            }
            catch
            {
                Log.Warn(TAG, ">>> Resource icon not found. Using gray background..");
                _floatingButton.SetBackgroundColor(Color.ParseColor(isCommandMode ? "#336699" : "#888888")); // Разные цвета для фоллбэка
            }
        }

        // --- НОВЫЙ ОБРАБОТЧИК СОБЫТИЯ ---
        private void MainPage_OnModeChanged(object? sender, bool isCommandMode)
        {
            // Обязательно выполняем обновление UI в главном потоке Android
            MainThread.BeginInvokeOnMainThread(() =>
            {
                UpdateButtonIcon(isCommandMode);
            });
        }

        private void SetRippleWaitingState()
        {
            // На всякий случай очищаем фон контейнера, если он там остался
            if (_rippleContainer != null)
                _rippleContainer.SetBackgroundColor(Android.Graphics.Color.Transparent);

            // Меняем цвет самого круга (овала) на желтый/оранжевый
            if (_rippleView != null && _rippleView.Background is GradientDrawable drawable)
            {
                drawable.SetColor(Android.Graphics.Color.ParseColor("#CCFFA500")); // Полупрозрачный оранжевый
            }
        }

        public override IBinder? OnBind(Intent? intent) => null;
        
        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null || _buttonLayoutParams == null || _rippleLayoutParams == null) return false;

            bool isCommandMode = Preferences.Get(MainPage.IsCommandModeKey, false);

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    Log.Debug(TAG, ">>> Button PRESSED. Starting recording....");

                    try
                    {
                        HapticFeedback.Default.Perform(HapticFeedbackType.Click);
                    }
                    catch
                    {
                        // Игнорируем ошибку, если на устройстве физически нет вибромотора 
                        // или пользователь отключил тактильный отклик в настройках системы
                    }

                    _initialX = _buttonLayoutParams.X;
                    _initialY = _buttonLayoutParams.Y;
                    _initialTouchX = e.RawX;
                    _initialTouchY = e.RawY;

                    StartRippleAnimation();
                    StartRecording();

                    // --- НОВОЕ: Временно даем окну фокус, чтобы система разрешила чтение буфера ---
                    if (isCommandMode && _buttonLayoutParams != null && _windowManager != null)
                    {
                        _buttonLayoutParams.Flags &= ~Android.Views.WindowManagerFlags.NotFocusable; // Удаляем флаг
                        _windowManager.UpdateViewLayout(_floatingButton, _buttonLayoutParams);
                    }

                    return true;

                case MotionEventActions.Move:
                    var dX = e.RawX - _initialTouchX;
                    var dY = e.RawY - _initialTouchY;

                    const float DragThreshold = 10.0f;
                    if (Math.Abs(dX) > DragThreshold || Math.Abs(dY) > DragThreshold)
                    {
                        // 1. Двигаем саму кнопку
                        _buttonLayoutParams.X = _initialX + (int)dX;
                        _buttonLayoutParams.Y = _initialY + (int)dY;
                        _windowManager?.UpdateViewLayout(_floatingButton, _buttonLayoutParams);

                        // 2. Синхронно двигаем слой с волнами (учитывая центрирование)
                        int offset = (RIPPLE_SIZE - BUTTON_SIZE) / 2;
                        _rippleLayoutParams.X = _buttonLayoutParams.X - offset;
                        _rippleLayoutParams.Y = _buttonLayoutParams.Y - offset;
                        _windowManager?.UpdateViewLayout(_rippleContainer, _rippleLayoutParams);
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    Log.Debug(TAG, ">>> Button RELEASED.");

                    // --- НОВОЕ: Захват контекста и подготовка данных ---
                    string clipboardContext = string.Empty;

                    if (isCommandMode)
                    {
                        // В командном режиме меняем цвет пульсации
                        clipboardContext = GetClipboardTextSafely();
                        Log.Debug(TAG, $"Захвачен текст из буфера: {clipboardContext.Length} символов");

                        SetRippleWaitingState();

                        // --- ВОЗВРАЩАЕМ ФЛАГ ТОЛЬКО ЕСЛИ СНИМАЛИ ЕГО ---
                        if (_buttonLayoutParams != null && _windowManager != null)
                        {
                            _buttonLayoutParams.Flags |= Android.Views.WindowManagerFlags.NotFocusable;
                            _windowManager.UpdateViewLayout(_floatingButton, _buttonLayoutParams);
                        }
                    }
                    else
                    {
                        // В обычном режиме диктовки сразу останавливаем анимацию
                        StopRippleAnimation();
                    }

                    // --- НОВОЕ: Сразу возвращаем флаг NotFocusable обратно ---
                    if (_buttonLayoutParams != null && _windowManager != null)
                    {
                        _buttonLayoutParams.Flags |= Android.Views.WindowManagerFlags.NotFocusable; // Возвращаем флаг
                        _windowManager.UpdateViewLayout(_floatingButton, _buttonLayoutParams);
                    }

                    // Запускаем асинхронную задачу в фоне
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // ПЕРЕДАЕМ переменные режима и буфера обмена в метод!
                            await StopAndProcessRecordingAsync(isCommandMode, clipboardContext);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(TAG, $"Critical error during shutdown/processing: {ex.Message}");
                        }
                    });
                    
                    return true; // UI мгновенно "отпускает" кнопку!

            }

            return false;
        }

        private void StartRippleAnimation()
        {
            if (_rippleView == null) return;

            _rippleView.Visibility = ViewStates.Visible;

            var scaleX = ObjectAnimator.OfFloat(_rippleView, "scaleX", 1f, 2.8f);
            scaleX.RepeatCount = ValueAnimator.Infinite;
            scaleX.RepeatMode = ValueAnimatorRepeatMode.Restart;
            scaleX.SetDuration(1200);

            var scaleY = ObjectAnimator.OfFloat(_rippleView, "scaleY", 1f, 2.8f);
            scaleY.RepeatCount = ValueAnimator.Infinite;
            scaleY.RepeatMode = ValueAnimatorRepeatMode.Restart;
            scaleY.SetDuration(1200);

            // Начинаем анимацию с 85% видимости, чтобы эффект был ярче
            var alpha = ObjectAnimator.OfFloat(_rippleView, "alpha", 0.85f, 0f);
            alpha.RepeatCount = ValueAnimator.Infinite;
            alpha.RepeatMode = ValueAnimatorRepeatMode.Restart;
            alpha.SetDuration(1200);

            _rippleAnimator = new AnimatorSet();
            _rippleAnimator.PlayTogether(scaleX, scaleY, alpha);
            _rippleAnimator.Start();
        }

        private void StopRippleAnimation()
        {
            if (_rippleAnimator != null)
            {
                _rippleAnimator.Cancel();
                _rippleAnimator = null;
            }

            if (_rippleView != null)
            {
                _rippleView.Visibility = ViewStates.Invisible;
                _rippleView.ScaleX = 1f;
                _rippleView.ScaleY = 1f;
                _rippleView.Alpha = 1f;

                // ВАЖНО: Возвращаем исходный красный цвет для следующей записи
                if (_rippleView.Background is GradientDrawable drawable)
                {
                    drawable.SetColor(Android.Graphics.Color.ParseColor("#CCFF2323"));
                }
            }

            // Железобетонно убираем фон контейнера
            if (_rippleContainer != null)
            {
                _rippleContainer.SetBackgroundColor(Android.Graphics.Color.Transparent);
            }
        }

        private void StartRecording()
        {
            if (_isRecording) return;
    
            // Ставим флаг СРАЗУ в UI-потоке, чтобы предотвратить множественные 
            // одновременные нажатия, пока фоновый поток запускается
            _isRecording = true;

            LogService.Log("Start of recording...");

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
                    var audioFormat = Android.Media.Encoding.Pcm16bit;
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
                    Log.Error(TAG, $"!!! ERROR StartRecording: {ex.Message}");
                    // В случае ошибки сбрасываем флаг, чтобы можно было попробовать снова
                    _isRecording = false; 
                }
            });
        }

        private async Task StopAndProcessRecordingAsync(bool isCommandMode, string clipboardContext)
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
                Log.Warn(TAG, "An empty audio file was recorded; sending was canceled.");
                // Гарантированно убираем анимацию, если выходим раньше
                MainThread.BeginInvokeOnMainThread(() => StopRippleAnimation());
                return;
            }

            try
            {
                // 3. Создаем WAV-файл в памяти
                byte[] wavData = await Task.Run(() => WavHelper.CreateWavFile(pcmData, 16000, 1, 16));
                Log.Debug(TAG, $"WAV file created in memory, size: {wavData.Length} byte.");

                // 4. Отправляем в API транскрибации (в фоне)
                string? transcribedText = await TranscribeAudioAsync(wavData);

                if (string.IsNullOrEmpty(transcribedText))
                {
                    Log.Error(TAG, "Failed to retrieve text from the API (response may be empty).");
                    MainThread.BeginInvokeOnMainThread(() => LogService.Log("Recognition error"));
                    return; // Прерываем выполнение, если транскрибация пуста
                }

                Log.Debug(TAG, $"Received transcribed text: {transcribedText}");

                // 5. РАЗВЕТВЛЕНИЕ ЛОГИКИ
                if (isCommandMode)
                {
                    // --- КОМАНДНЫЙ РЕЖИМ ---
                    Log.Debug(TAG, "Command Mode: Sending data to LLM...");

                    // Делаем второй сетевой запрос в фоне (не блокируя UI!)
                    string? llmResponse = await SendChatCompletionAsync(clipboardContext, transcribedText);

                    if (!string.IsNullOrEmpty(llmResponse))
                    {
                        // Переходим в UI-поток только для работы с буфером обмена и вибрацией
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(llmResponse);
                            HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
                            LogService.Log("AI response copied to clipboard");
                        });
                    }
                    else
                    {
                        Log.Error(TAG, "LLM returned an empty response.");
                        MainThread.BeginInvokeOnMainThread(() => LogService.Log("AI Error"));
                    }
                }
                else
                {
                    // --- РЕЖИМ ДИКТОВКИ (Стандартный) ---
                    // Переходим в UI-поток
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        // Если ваш старый метод CopyToClipboardAndNotify делал что-то важное 
                        // (например, показывал Toast), можете использовать его здесь.
                        // Иначе используем стандартный вызов:
                        await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(transcribedText);
                        HapticFeedback.Default.Perform(HapticFeedbackType.Click);
                        LogService.Log("Dictation copied to clipboard");
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error during audio processing: {ex.Message}");
                MainThread.BeginInvokeOnMainThread(() => LogService.Log("Processing error"));
            }
            finally
            {
                // 6. ГАРАНТИРОВАННАЯ ОСТАНОВКА АНИМАЦИИ
                // Блок finally выполнится в любом случае (успех или ошибка)
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StopRippleAnimation();
                });
            }
        }

        private async Task<string?> TranscribeAudioAsync(byte[] wavFileBytes)
        {
            // Получаем ключ из настроек приложения
            var mistralApiKey = Preferences.Get("MistralApiKey", string.Empty);
            var currentProxyConfig = Preferences.Get("ProxyConfig", string.Empty);

            if (string.IsNullOrEmpty(mistralApiKey))
            {
                Log.Error(TAG, "API key is not set! Please specify it in the application.");
                LogService.Log("API key is not set! Please specify it in the application.");
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
                    LogService.Log($"API Error: {(int)response.StatusCode} - {responseString}");
                    Log.Error(TAG, $"API Error: {(int)response.StatusCode} - {responseString}");
                    return null;
                }

                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            }
            catch (Exception ex)
            {
                LogService.Log($"Network error: {ex.Message}");
                Log.Error(TAG, $"Network error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogService.Log($"Internal exception: {ex.InnerException.Message}");
                    Log.Error(TAG, $"Internal exception: {ex.InnerException.Message}");
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
                Log.Debug(TAG, "Proxy settings have changed (or first launch). Recreating HttpClient.");

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
                LogService.Log($"Proxy error: {ex.Message}. Reverting to direct connection.");
                Log.Error(TAG, $"Proxy error: {ex.Message}. Reverting to direct connection.");
                return new HttpClient();
            }
        }

        private void CreateFloatingButton()
        {
            try
            {
                Log.Debug(TAG, ">>> Starting button creation...");
                _windowManager = GetSystemService(WindowService).JavaCast<IWindowManager>();

                int startX = 100;
                int startY = 200;
                int offset = (RIPPLE_SIZE - BUTTON_SIZE) / 2;

                // --- 1. СОЗДАЕМ ОКНО ДЛЯ АНИМАЦИИ (ПРОЗРАЧНОЕ ДЛЯ КЛИКОВ) ---
                _rippleContainer = new FrameLayout(this);
                _rippleView = new View(this);
                var rippleDrawable = new GradientDrawable();
                rippleDrawable.SetShape(ShapeType.Oval);
                // Используем насыщенный красный с прозрачностью 80% (CC в HEX)
                rippleDrawable.SetColor(Color.ParseColor("#CCFF2323"));
                _rippleView.Background = rippleDrawable;
                _rippleView.Visibility = ViewStates.Invisible;

                var rippleViewParams = new FrameLayout.LayoutParams(BUTTON_SIZE, BUTTON_SIZE)
                {
                    Gravity = GravityFlags.Center
                };
                _rippleContainer.AddView(_rippleView, rippleViewParams);

                _rippleLayoutParams = new WindowManagerLayoutParams(
                    RIPPLE_SIZE, RIPPLE_SIZE,
                    WindowManagerTypes.ApplicationOverlay,
                    // ↓↓↓ ДОБАВЛЯЕМ ФЛАГ LayoutNoLimits СЮДА ↓↓↓
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutNoLimits,
                    Format.Translucent
                );

                _rippleLayoutParams.Gravity = GravityFlags.Left | GravityFlags.Top;
                _rippleLayoutParams.X = startX - offset;
                _rippleLayoutParams.Y = startY - offset;

                _windowManager.AddView(_rippleContainer, _rippleLayoutParams);


                // --- 2. СОЗДАЕМ САМУ КНОПКУ (КЛИКАБЕЛЬНУЮ) ---
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
                    Log.Warn(TAG, ">>> Resource 'mic_icon' not found. Using gray background..");
                    _floatingButton.SetBackgroundColor(Color.ParseColor("#888888")); // Сделаем непрозрачным серым
                }

                // --- ИЗМЕНЕНИЯ ЗДЕСЬ ---
                // Загружаем текущее состояние из Preferences
                bool isCommandMode = Preferences.Get(MainPage.IsCommandModeKey, false);
                UpdateButtonIcon(isCommandMode);

                // Подписываемся на смену режима на лету
                MainPage.OnModeChanged += MainPage_OnModeChanged;
                // --- КОНЕЦ ИЗМЕНЕНИЙ ---

                _buttonLayoutParams = new WindowManagerLayoutParams(
                    BUTTON_SIZE, BUTTON_SIZE,
                    WindowManagerTypes.ApplicationOverlay,
                    // ↓↓↓ Добавляем флаг LayoutNoLimits и для самой кнопки ↓↓↓
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
                    Format.Translucent
                );

                _buttonLayoutParams.Gravity = GravityFlags.Left | GravityFlags.Top;
                _buttonLayoutParams.X = startX;
                _buttonLayoutParams.Y = startY;

                // Добавляем кнопку ВТОРОЙ, чтобы она была поверх контейнера с анимацией (Z-index)
                _windowManager.AddView(_floatingButton, _buttonLayoutParams);

                Log.Debug(TAG, ">>> BUTTON AND RIPPLE SEPARATED SUCCESSFULLY!");
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"!!! ERROR в CreateFloatingButton: {ex.Message}");
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
            Log.Debug(TAG, ">>> Notification channel created.");
        }
        public override void OnDestroy()
        {
            base.OnDestroy();

            // НОВОЕ: Отписываемся от события
            MainPage.OnModeChanged -= MainPage_OnModeChanged;

            // Удаляем оба окна
            if (_windowManager != null)
            {
                if (_rippleContainer != null) _windowManager.RemoveView(_rippleContainer);
                if (_floatingButton != null) _windowManager.RemoveView(_floatingButton);
            }
        }
    }
}