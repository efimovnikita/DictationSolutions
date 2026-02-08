# WhisperInk (VoiceHUD)

WhisperInk is a lightweight, minimalist "Voice-to-Text" HUD (Heads-Up Display) for Windows. It allows users to dictate text from anywhere in the OS and have it automatically transcribed and typed into the active window using the Mistral AI API.

![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white)
![.NET 8.0](https://img.shields.io/badge/.NET%208.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-blue?style=for-the-badge)

## ✨ Features

*   **Global Hotkey Control**: Hold `Ctrl + Win` to record audio from any application.
*   **Minimalist HUD**: A sleek, semi-transparent overlay that stays on top and provides visual feedback.
*   **Automatic Transcription**: Powered by Mistral AI’s `voxtral-mini-latest` model for high-speed, accurate speech-to-text.
*   **Auto-Type Integration**: Once processing is complete, the text is automatically pasted into your active cursor position.
*   **Microphone Selection**: Right-click the HUD to select your preferred input device.
*   **Smart Config**: Automatically creates a configuration folder in your Windows `AppData` directory.

## 🛠️ Visual Feedback

The HUD border changes color to indicate current status:
- 🔴 **Red**: Recording audio.
- 🟡 **Gold**: Thinking/Transcribing (API request in progress).
- 🟢 **Green**: Success (Text pasted).
- ⚪ **Gray/Dark**: Idle mode.

## 🚀 Getting Started

### Prerequisites
*   [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
*   A **Mistral AI API Key**. You can obtain one at [console.mistral.ai](https://console.mistral.ai/).

### Installation & Setup

1.  **Clone the repository**:
    ```bash
    git clone https://github.com/yourusername/WhisperInk.git
    cd WhisperInk
    ```

2.  **Build the project**:
    ```bash
    dotnet build
    ```

3.  **Run the application**:
    On the first run, the app will create a configuration file and show a message box. 
    It will close automatically to allow you to enter your API key.

4.  **Configure the API Key**:
    - Navigate to `%AppData%\.WhisperInk\`
    - Open `config.json` with a text editor.
    - Replace `"ВСТАВЬТЕ_ВАШ_КЛЮЧ_СЮДА"` with your actual Mistral API key.
    - Save the file and restart **WhisperInk**.

## 🖱️ Usage

1.  **Record**: Press and hold `Ctrl + Win`. Speak clearly into your microphone.
2.  **Transcribe**: Release the keys. The HUD will turn gold while processing.
3.  **Paste**: The transcribed text will appear at your cursor automatically.
4.  **Settings**: Right-click the small HUD window to change your microphone or exit the application.
5.  **Move**: Left-click and drag the HUD to position it anywhere on your screen.

## 📂 Project Structure

*   `MainWindow.xaml`: Defines the transparent, "Always-on-Top" UI.
*   `MainWindow.xaml.cs`: Contains the core logic:
    *   **Low-Level Keyboard Hook**: Captures global hotkeys.
    *   **NAudio Integration**: Handles audio recording and device management.
    *   **Mistral API Client**: Sends multipart/form-data requests to the AI endpoint.
    *   **Input Simulation**: Uses `keybd_event` and `Clipboard` to paste text.
*   `config.json`: Stores your API key locally in a secure application folder.

## 📦 Dependencies

*   **NAudio**: For robust audio capturing and device enumeration.
*   **System.Text.Json**: For lightweight configuration management.

---

### ⚖️ License
This project is provided "as-is". Check your Mistral AI usage limits, as this application makes requests to a paid API service.

### 🛠️ Tech Stack
*   **Language**: C# 12
*   **Framework**: WPF (.NET 8.0)
*   **APIs**: Mistral AI (Speech-to-Text)
*   **Win32 API**: For Global Hooks and Keyboard Simulation.