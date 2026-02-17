namespace WhisperInk.Maui
{
    public static class WavHelper
    {
        // Новый метод, который принимает сырые PCM-данные в виде массива байт
        public static byte[] CreateWavFile(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
        {
            using (var memoryStream = new MemoryStream())
            using (var writer = new BinaryWriter(memoryStream))
            {
                // Заголовок RIFF
                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + pcmData.Length);
                writer.Write("WAVE".ToCharArray());

                // Секция 'fmt '
                writer.Write("fmt ".ToCharArray());
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * (bitsPerSample / 8));
                writer.Write((short)(channels * (bitsPerSample / 8)));
                writer.Write((short)bitsPerSample);

                // Секция 'data'
                writer.Write("data".ToCharArray());
                writer.Write(pcmData.Length);
                writer.Write(pcmData);

                return memoryStream.ToArray();
            }
        }
    }
}