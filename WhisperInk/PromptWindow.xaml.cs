using System.Windows;

namespace WhisperInk
{
    public partial class PromptWindow : Window
    {
        public string PromptText { get; private set; }

        public PromptWindow(string currentPrompt)
        {
            InitializeComponent();
            txtPrompt.Text = currentPrompt;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Checking if the text field is empty (including spaces-only check)
            if (string.IsNullOrWhiteSpace(txtPrompt.Text))
            {
                MessageBox.Show("The system prompt cannot be empty. Please enter text or click 'Reset to Default'.",
                                "Attention",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return; // Execution interrupted, window remains open.
            }

            PromptText = txtPrompt.Text;
            DialogResult = true; // Indicates that the window was closed with confirmation.
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // Creating an empty config to extract the default value from it.
            var defaultConfig = new AppConfig();
            txtPrompt.Text = defaultConfig.SystemPrompt;
        }   
    }
}