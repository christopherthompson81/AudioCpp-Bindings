namespace AudioCpp.Demo;

/// <summary>Minimal 16-bit PCM WAV read/write, so the sample needs no audio package.</summary>
internal static class Wav
{
    internal static (float[] Samples, int SampleRate, int Channels) Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");

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
                if (bits != 16) throw new InvalidDataException($"{bits}-bit WAV is not supported");
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
        throw new InvalidDataException("no data chunk");
    }

    internal static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);                       // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);      // byte rate
        writer.Write((short)(channels * 2));          // block align
        writer.Write((short)16);                      // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            writer.Write((short)Math.Round(clamped * 32767f));
        }
    }
}
