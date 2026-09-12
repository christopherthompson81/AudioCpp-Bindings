namespace AudioCpp.PathTest;

/// <summary>Minimal 16-bit PCM WAV reader, so the test needs no package references.</summary>
internal static class Wav
{
    internal static (float[] Samples, int SampleRate, int Channels) Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException($"{path}: not RIFF");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException($"{path}: not WAVE");

        int channels = 0, sampleRate = 0, bits = 0;
        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (size > 16) stream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException($"{path}: {bits}-bit is not supported");
                var bytes = reader.ReadBytes((int)size);
                var samples = new float[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                }
                return (samples, sampleRate, channels);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }
}
