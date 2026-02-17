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
        private string? _pcmFilePath;
        private bool _isRecording;
        private Task? _recordingTask;
        private MemoryStream? _recordingStream; // <-- Используем MemoryStream вместо пути к файлу

        private int _initialX;
        private int _initialY;
        private float _initialTouchX;
        private float _initialTouchY;
        private bool _wasDragged = false;

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

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
                    _wasDragged = false; // Сбрасываем флаг перетаскивания

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
                        _wasDragged = true; // Устанавливаем флаг, что это перетаскивание
                        // Обновляем параметры положения окна
                        _layoutParams.X = _initialX + (int)dX;
                        _layoutParams.Y = _initialY + (int)dY;
                        _windowManager?.UpdateViewLayout(_floatingButton, _layoutParams);
                    }
                    return true;

                case MotionEventActions.Up:
                    Log.Debug(TAG, ">>> Кнопка ОТПУЩЕНА.");
                    // Теперь мы сохраняем и обрабатываем запись в любом случае,
                    // независимо от того, перетаскивали кнопку или нет.
                    StopAndProcessRecording();
                    return true;
            
                case MotionEventActions.Cancel:
                    // ДОПОЛНИТЕЛЬНО: Полезно обрабатывать системную отмену 
                    // (например, если во время записи вам позвонили или вылезло системное окно)
                    Log.Debug(TAG, ">>> Касание ОТМЕНЕНО системой.");
                    StopRecordingAndDiscard();
                    return true;
            }
            return false;
        }

        private void StopRecordingAndDiscard()
        {
            Log.Debug(TAG, "Обнаружено перетаскивание. Запись отменена.");
            if (!_isRecording) return;

            _isRecording = false;
            // Не ждем завершения задачи, просто даем команду на остановку
            _audioRecord?.Stop();
            _audioRecord?.Release();
            _audioRecord = null;

            _recordingStream?.Close(); // Закрываем и удаляем поток с данными
        }

        private void StartRecording()
        {
            if (_isRecording) return;
            try
            {
                // Инициализируем поток в памяти
                _recordingStream = new MemoryStream();

                var audioSource = AudioSource.Mic;
                var sampleRate = 16000;
                var channelConfig = ChannelIn.Mono;
                var audioFormat = Encoding.Pcm16bit;
                var bufferSize = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, audioFormat);

                _audioRecord = new AudioRecord(audioSource, sampleRate, channelConfig, audioFormat, bufferSize);
                _audioRecord.StartRecording();
                _isRecording = true;

                // Запускаем запись в фоновом потоке
                _recordingTask = Task.Run(() =>
                {
                    var buffer = new byte[bufferSize];
                    while (_isRecording)
                    {
                        int read = _audioRecord.Read(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            // Пишем напрямую в MemoryStream
                            _recordingStream.Write(buffer, 0, read);
                        }
                    }
                });
            }
            catch (Exception ex) { Log.Error(TAG, $"!!! ОШИБКА StartRecording: {ex.Message}"); }
        }
        private async void StopAndProcessRecording()
        {
            if (!_isRecording || _recordingStream == null) return;

            // 1. Останавливаем запись
            _isRecording = false;
            if (_recordingTask != null) await _recordingTask; // Гарантированно дожидаемся завершения потока
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
            byte[] wavData = WavHelper.CreateWavFile(pcmData, 16000, 1, 16);
            Log.Debug(TAG, $"WAV-файл создан в памяти, размер: {wavData.Length} байт.");

            // 4. Отправляем в API
            string? transcribedText = await TranscribeAudioAsync(wavData);

            // 5. Обрабатываем результат
            if (!string.IsNullOrEmpty(transcribedText))
            {
                Log.Debug(TAG, $"Получен текст: {transcribedText}");
                CopyToClipboardAndNotify(transcribedText);
            }
            else
            {
                Log.Error(TAG, "Не удалось получить текст от API (возможно, пустой ответ).");
                ShowToast("Ошибка распознавания");
            }
        }

        private async Task<string?> TranscribeAudioAsync(byte[] wavFileBytes)
        {
            // Получаем ключ из настроек приложения
            var mistralApiKey = Preferences.Get("MistralApiKey", string.Empty);

            if (string.IsNullOrEmpty(mistralApiKey))
            {
                Log.Error(TAG, "API ключ не установлен! Пожалуйста, задайте его в приложении.");
                ShowToast("API key not set");
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/audio/transcriptions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mistralApiKey);

                using var content = new MultipartFormDataContent();
                var audioContent = new ByteArrayContent(wavFileBytes);
                audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

                content.Add(audioContent, "file", "audio.wav");
                content.Add(new StringContent("voxtral-mini-latest"), "model");

                request.Content = content;
                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error(TAG, $"Ошибка API: {(int)response.StatusCode} - {responseString}");
                    return null;
                }
        
                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Сетевая ошибка: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Log.Error(TAG, $"Внутреннее исключение: {ex.InnerException.Message}");
                }
                return null;
            }
        }

        private void CopyToClipboardAndNotify(string text)
        {
            // Используем нативный API Android для работы с буфером обмена
            var clipboardManager = (Clipboard)GetSystemService(ClipboardService);
            var clipData = ClipData.NewPlainText("WhisperInk Result", text);
            clipboardManager.PrimaryClip = clipData;
            
            // Показываем всплывающее уведомление
            ShowToast("Текст скопирован!");
        }

        private void ShowToast(string message)
        {
            // Toast нужно показывать в UI-потоке
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Toast.MakeText(this, message, ToastLength.Short)?.Show();
            });
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