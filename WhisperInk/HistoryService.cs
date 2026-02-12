using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WhisperInk
{
    public class HistoryItem
    {
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = "";

        // Для удобного отображения времени в XAML (например "14:30")
        public string TimeStr => Timestamp.ToString("HH:mm");
    }

    public static class HistoryService
    {
        // Коллекция, к которой привяжется UI
        public static ObservableCollection<HistoryItem> Items { get; private set; } = new();

        private static string _historyPath;

        static HistoryService()
        {
            // Путь к файлу там же, где конфиг
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDirectory = Path.Combine(appDataPath, ".WhisperInk");
            if (!Directory.Exists(configDirectory)) Directory.CreateDirectory(configDirectory);
            _historyPath = Path.Combine(configDirectory, "history.json");

            Load();
        }

        public static void Add(string text)
        {
            // Выполняем в UI потоке, так как коллекция привязана к UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                var item = new HistoryItem
                {
                    Timestamp = DateTime.Now,
                    Text = text
                };

                // Вставляем в начало списка
                Items.Insert(0, item);

                // Ограничим историю, например, 100 записями
                if (Items.Count > 100) Items.RemoveAt(Items.Count - 1);

                Save();
            });
        }

        public static void Remove(HistoryItem item)
        {
            if (Items.Contains(item))
            {
                Items.Remove(item);
                Save();
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    string json = File.ReadAllText(_historyPath);
                    var list = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                    if (list != null)
                    {
                        // Сортируем: новые сверху
                        list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                        foreach (var i in list) Items.Add(i);
                    }
                }
            }
            catch { /* Игнорируем ошибки чтения */ }
        }

        private static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_historyPath, json);
            }
            catch { /* Игнорируем ошибки записи */ }
        }
    }
}