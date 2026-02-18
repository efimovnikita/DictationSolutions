using System.Collections.ObjectModel;

namespace WhisperInk.Maui
{
    public partial class MainPage : ContentPage
    {
        public const string ApiKeyPreferenceKey = "MistralApiKey";

        public MainPage()
        {
            InitializeComponent();
            LoadApiKey();
        }

        // Загружаем логи при открытии страницы
        protected override void OnAppearing()
        {
            base.OnAppearing();
            LoadLogs();
        }

        private void LoadApiKey()
        {
            var apiKey = Preferences.Get(ApiKeyPreferenceKey, string.Empty);
            ApiKeyEntry.Text = apiKey;
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
                StatusLabel.Text = "API Key saved successfully!";
                StatusLabel.TextColor = Colors.Green;

                // Добавим запись в лог о сохранении настроек
                LogService.Log("Настройки: API ключ обновлен пользователем.");
                LoadLogs(); // Обновим список сразу
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