using System.Collections.ObjectModel;

namespace WhisperInk.Maui
{
    public partial class MainPage : ContentPage
    {
        public const string ApiKeyPreferenceKey = "MistralApiKey";
        public const string ProxyPreferenceKey = "ProxyConfig";

        // НОВЫЕ КЛЮЧИ:
        public const string IsCommandModeKey = "IsCommandMode";
        public const string SystemPromptKey = "SystemPrompt";

        // НОВОЕ СОБЫТИЕ: Для оповещения сервиса на лету
        public static event EventHandler<bool>? OnModeChanged;

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

            // НОВОЕ: Загружаем режим и промпт
            bool isCommandMode = Preferences.Get(IsCommandModeKey, false);
            ModeSwitch.IsToggled = isCommandMode;
            SystemPromptContainer.IsVisible = isCommandMode; // Показываем/скрываем поле

            SystemPromptEditor.Text = Preferences.Get(SystemPromptKey, "You are a precise execution engine. Output ONLY the direct result of the task.");
        }

        // НОВЫЙ ОБРАБОТЧИК: Переключение Switch "на лету"
        private void OnModeToggled(object sender, ToggledEventArgs e)
        {
            SystemPromptContainer.IsVisible = e.Value;
            Preferences.Set(IsCommandModeKey, e.Value); // Сохраняем состояние

            // Отправляем сигнал сервису для смены иконки
            OnModeChanged?.Invoke(this, e.Value);
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

                // НОВОЕ: Сохраняем системный промпт при нажатии Save
                Preferences.Set(SystemPromptKey, SystemPromptEditor.Text);

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