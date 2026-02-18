namespace WhisperInk.Maui
{
    public static class LogService
    {
        private static readonly string LogFilePath = Path.Combine(FileSystem.AppDataDirectory, "app_log.txt");
        private static readonly object _lockObj = new object();

        // Запись сообщения в лог
        public static void Log(string message)
        {
            lock (_lockObj)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    var line = $"[{timestamp}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка записи лога: {ex.Message}");
                }
            }
        }

        // Чтение последних N строк (по умолчанию 100)
        public static List<string> GetLastLogs(int count = 100)
        {
            lock (_lockObj)
            {
                try
                {
                    if (!File.Exists(LogFilePath))
                        return new List<string> { "Логов пока нет..." };

                    // Читаем все строки и берем последние N, разворачивая порядок (новые сверху)
                    var lines = File.ReadAllLines(LogFilePath);
                    return lines.Reverse().Take(count).ToList();
                }
                catch (Exception ex)
                {
                    return new List<string> { $"Ошибка чтения логов: {ex.Message}" };
                }
            }
        }

        // Очистка логов (опционально, пригодится для отладки)
        public static void ClearLogs()
        {
            lock (_lockObj)
            {
                if (File.Exists(LogFilePath))
                    File.WriteAllText(LogFilePath, string.Empty);
            }
        }
    }
}