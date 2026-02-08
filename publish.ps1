# ===================================================================
#      Скрипт для публикации WPF приложения в один EXE-файл
# ===================================================================

# --- НАСТРОЙКА ---
# Имя вашего проекта (без .csproj расширения)
$ProjectName = "WhisperInk"
# -----------------

# Получаем путь к папке, где лежит этот скрипт (корень решения)
$SolutionDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectFile = Join-Path $SolutionDir $ProjectName "$ProjectName.csproj"
$PublishDir = Join-Path $SolutionDir "_publish"

# Вывод информации
Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Начинается публикация проекта: $ProjectName"
Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Путь к проекту: $ProjectFile"
Write-Host "Папка вывода:   $PublishDir"
Write-Host ""

# Проверка, существует ли файл проекта
if (-not (Test-Path $ProjectFile)) {
    Write-Host "ОШИБКА: Файл проекта не найден по пути '$ProjectFile'." -ForegroundColor Red
    Write-Host "Проверьте переменную `$ProjectName` в скрипте и структуру папок." -ForegroundColor Red
    exit 1
}

# Очистка старой папки публикации, если она существует
if (Test-Path $PublishDir) {
    Write-Host "Очистка старой папки публикации..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

# --- ИЗМЕНЕНИЕ ЗДЕСЬ ---
# Убираем лишние кавычки (`"`) вокруг переменных $ProjectFile и $PublishDir.
# PowerShell сам правильно обработает пути, даже если в них есть пробелы.
$Arguments = @(
    "publish",
    $ProjectFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=true",
    "-o", $PublishDir
)

# Вывод и выполнение команды
Write-Host "Выполняется команда:" -ForegroundColor Green
Write-Host "dotnet $Arguments"
Write-Host ""

& dotnet $Arguments

# Проверка результата
if ($?) {
    Write-Host "----------------------------------" -ForegroundColor Green
    Write-Host "Публикация успешно завершена!" -ForegroundColor Green
    Write-Host "Готовый EXE-файл находится здесь: $PublishDir"
    Write-Host "----------------------------------" -ForegroundColor Green
    
    # Открываем папку с результатом
    Invoke-Item $PublishDir
} else {
    Write-Host "----------------------------------" -ForegroundColor Red
    Write-Host "ОШИБКА: Публикация не удалась." -ForegroundColor Red
    Write-Host "----------------------------------" -ForegroundColor Red
}