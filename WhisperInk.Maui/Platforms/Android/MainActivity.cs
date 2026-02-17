using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Microsoft.Maui.ApplicationModel;

namespace WhisperInk.Maui
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        public const string TAG = "WhisperInkDebug"; // Наш уникальный тег для логов

        protected override async void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Это самое первое, что мы делаем. Если этого лога нет, проблема глобальная.
            Log.Debug(TAG, ">>> MainActivity.OnCreate: Приложение запущено."); 

            if (await Permissions.CheckStatusAsync<Permissions.Microphone>() != PermissionStatus.Granted)
            {
                Log.Debug(TAG, ">>> Запрашиваем разрешение на микрофон...");
                await Permissions.RequestAsync<Permissions.Microphone>();
            }

            StartFloatingButtonService();
        }

        public void StartFloatingButtonService()
        {
            Log.Debug(TAG, ">>> MainActivity: Вызываем StartFloatingButtonService...");
            var intent = new Intent(this, typeof(FloatingButtonService));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                StartForegroundService(intent);
            }
            else
            {
                StartService(intent);
            }
            Log.Debug(TAG, ">>> MainActivity: Команда на запуск сервиса ОТПРАВЛЕНА.");
        }
    }
}