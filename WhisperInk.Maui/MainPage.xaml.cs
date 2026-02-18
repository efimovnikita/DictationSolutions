using System.Collections.ObjectModel;

namespace WhisperInk.Maui
{
    public partial class MainPage : ContentPage
    {
        public const string ApiKeyPreferenceKey = "MistralApiKey";
        public const string ProxyPreferenceKey = "ProxyConfig";

        public MainPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        // Загружаем логи при открытии страницы
        protected override void OnAppearing()
        {
            base.OnAppearing();
            LoadLogs();
        }

        private void LoadSettings()
        {
            var apiKey = Preferences.Get(ApiKeyPreferenceKey, string.Empty);
            ApiKeyEntry.Text = apiKey;

            // Загружаем прокси
            var proxyConf = Preferences.Get(ProxyPreferenceKey, string.Empty);
            ProxyEntry.Text = proxyConf;
        }

        private void OnSaveClicked(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ApiKeyEntry.Text))
            {
                StatusLabel.Text = "API key cannot be empty.";
                StatusLabel.TextColor = Colors.Red;
            }
            else
            {
                Preferences.Set(ApiKeyPreferenceKey, ApiKeyEntry.Text);

                // Сохраняем прокси (даже если пусто, чтобы можно было сбросить)
                if (string.IsNullOrWhiteSpace(ProxyEntry.Text))
                    Preferences.Remove(ProxyPreferenceKey);
                else
                    Preferences.Set(ProxyPreferenceKey, ProxyEntry.Text.Trim());

                StatusLabel.Text = "Settings saved successfully!";
                StatusLabel.TextColor = Colors.Green;

                LogService.Log("Настройки: API ключ и прокси обновлены.");
                LoadLogs();
            }

            Dispatcher.StartTimer(TimeSpan.FromSeconds(3), () =>
            {
                StatusLabel.Text = string.Empty;
                return false;
            });
        }

        // Метод загрузки логов в UI
        private void LoadLogs()
        {
            // Получаем список строк из сервиса
            var logs = LogService.GetLastLogs(100);

            // Привязываем к CollectionView
            LogsCollectionView.ItemsSource = logs;
        }

        // Обработчик "потянуть для обновления"
        private void OnLogsRefreshing(object sender, EventArgs e)
        {
            LoadLogs();
            LogsRefreshView.IsRefreshing = false; // Остановить анимацию спиннера
        }
    }
}