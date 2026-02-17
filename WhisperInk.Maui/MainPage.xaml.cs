namespace WhisperInk.Maui
{
    public partial class MainPage : ContentPage
    {
        // Ключ для хранения API-ключа в настройках
        public const string ApiKeyPreferenceKey = "MistralApiKey";

        public MainPage()
        {
            InitializeComponent();
            LoadApiKey();
        }

        private void LoadApiKey()
        {
            // Загружаем ключ из хранилища и отображаем в поле ввода
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
                // Сохраняем ключ в хранилище
                Preferences.Set(ApiKeyPreferenceKey, ApiKeyEntry.Text);
                StatusLabel.Text = "API Key saved successfully!";
                StatusLabel.TextColor = Colors.Green;
            }

            // Скрываем сообщение через 3 секунды
            Dispatcher.StartTimer(TimeSpan.FromSeconds(3), () =>
            {
                StatusLabel.Text = string.Empty;
                return false; // Остановить таймер
            });
        }
    }
}