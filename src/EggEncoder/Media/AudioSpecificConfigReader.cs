using EggEncoder.Codecs.Aac;

namespace EggEncoder.Media
{
    // AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1): audioObjectType (5 bits, 31 escapes to 6
    // more), samplingFrequencyIndex (4 bits, 15 escapes to an explicit 24-bit rate),
    // channelConfiguration (4 bits). Only the header fields a container needs; any object type is
    // accepted. Shared by the FLV demuxer and the fMP4 init segment writer.
    internal static class AudioSpecificConfigReader
    {
        /// <exception cref="InvalidDataException">Truncated, or a reserved samplingFrequencyIndex.</exception>
        public static (int SampleRate, int Channels) Read(ReadOnlySpan<byte> config)
        {
            var reader = new Transform.BitReader(config.ToArray());
            try
            {
                var objectType = (int)reader.ReadBits(5);
                if (objectType == 31)
                {
                    reader.ReadBits(6);
                }

                var frequencyIndex = (int)reader.ReadBits(4);
                int sampleRate;
                if (frequencyIndex == 15)
                {
                    sampleRate = (int)reader.ReadBits(24);
                }
                else if (frequencyIndex < AacTables.SampleRates.Length)
                {
                    sampleRate = AacTables.SampleRates[frequencyIndex];
                }
                else
                {
                    throw new InvalidDataException($"AudioSpecificConfig has reserved samplingFrequencyIndex {frequencyIndex}");
                }

                var channels = (int)reader.ReadBits(4);
                return (sampleRate, channels);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("Truncated AAC AudioSpecificConfig", e);
            }
        }
    }
}
